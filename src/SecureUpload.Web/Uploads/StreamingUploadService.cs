using Azure;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Web.Telemetry;

namespace SecureUpload.Web.Uploads;

public abstract record UploadResult
{
    public sealed record Accepted(string StableId, PublicFileState State) : UploadResult;
    public sealed record Rejected(int StatusCode, string Code, string Message) : UploadResult;
}

public sealed class StreamingUploadService(
    IFileStatusStore statuses,
    IBlobFileStore blobs,
    UploadPolicyValidator policy,
    TimeProvider timeProvider,
    UploadTelemetry telemetry)
{
    private const int MaxBoundaryLength = 256;

    public async Task<UploadResult> UploadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var operation = telemetry.Start();
        FileRecord? record = null;
        BlobWriteResult? writtenBlob = null;

        try
        {
            var boundary = GetBoundary(request.ContentType);
            var reader = new MultipartReader(boundary, request.Body)
            {
                HeadersCountLimit = 16,
                HeadersLengthLimit = 16 * 1024,
                BodyLengthLimit = policy.MaximumFileSizeBytes + 1
            };

            var section = await reader.ReadNextSectionAsync(cancellationToken);
            if (section is null ||
                !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !UploadPolicyValidator.IsFileSection(disposition))
            {
                throw new UploadPolicyException("one-file-required", "Choose exactly one file.");
            }

            var rawFileName = disposition.FileNameStar.HasValue
                ? disposition.FileNameStar.Value!
                : disposition.FileName.Value!;
            var validated = policy.Validate(rawFileName, section.ContentType);
            record = FileRecord.CreateUploading(
                validated.FileName,
                validated.MediaType,
                timeProvider.GetUtcNow());

            var created = await statuses.CreateAsync(record, cancellationToken);
            if (created.Disposition != StatusWriteDisposition.Succeeded || created.Record is null)
            {
                telemetry.RecordFailure(operation, "status-create");
                telemetry.RecordRejected(operation, "status-create-failed");
                return Unavailable("status-create-failed");
            }

            record = created.Record;
            await using var limited = new SizeLimitedReadStream(section.Body, policy.MaximumFileSizeBytes);
            writtenBlob = await blobs.UploadPendingAsync(
                record.StableId,
                limited,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fileId"] = record.StableId,
                    ["originalFileNameBase64"] = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes(validated.FileName)),
                    ["mediaType"] = validated.MediaType
                },
                cancellationToken);

            if (limited.BytesRead == 0)
            {
                throw new UploadPolicyException("empty-file", "The selected file is empty.");
            }

            if (await reader.ReadNextSectionAsync(cancellationToken) is not null)
            {
                throw new UploadPolicyException("one-file-required", "Choose exactly one file.");
            }

            try
            {
                var finalized = await FinalizeUploadAsync(record, writtenBlob, cancellationToken);
                if (finalized is null)
                {
                    telemetry.RecordFailure(operation, "status-finalize");
                    await FailAndCleanupAsync(
                        operation,
                        record,
                        writtenBlob,
                        "status-finalize-failed",
                        cancellationToken);
                    telemetry.RecordRejected(operation, "status-finalize-failed");
                    return Unavailable("status-finalize-failed");
                }

                telemetry.RecordAccepted(operation, finalized.StableId, writtenBlob.SizeBytes);
                return new UploadResult.Accepted(finalized.StableId, finalized.State.ToPublicState());
            }
            catch (Exception exception)
            {
                telemetry.RecordCleanupFailure(operation, "status-finalize", exception);
                telemetry.RecordRejected(operation, "status-finalize-failed");
                return Unavailable("status-finalize-failed");
            }
        }
        catch (UploadPolicyException exception)
        {
            await FailAndCleanupAsync(operation, record, writtenBlob, exception.Code, cancellationToken);
            telemetry.RecordRejected(operation, exception.Code);
            return new UploadResult.Rejected(exception.StatusCode, exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FailAndCleanupAsync(
                operation,
                record,
                writtenBlob,
                "upload-cancelled",
                CancellationToken.None);
            telemetry.RecordFailure(operation, "stream");
            telemetry.RecordRejected(operation, "upload-cancelled");
            return Unavailable("upload-cancelled");
        }
        catch (Exception)
        {
            telemetry.RecordFailure(operation, "stream");
            await FailAndCleanupAsync(
                operation,
                record,
                writtenBlob,
                "upload-failed",
                CancellationToken.None);
            telemetry.RecordRejected(operation, "upload-failed");
            return Unavailable("upload-failed");
        }
    }

    private async Task<FileRecord?> FinalizeUploadAsync(
        FileRecord record,
        BlobWriteResult blob,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (record.StoreETag is not { } storeETag)
            {
                return null;
            }

            var transition = FileStateMachine.Transition(
                record,
                FileTransition.UploadCompleted(
                    blob.ETag.ToString(),
                    blob.SizeBytes,
                    timeProvider.GetUtcNow(),
                    blob.BlobUri));

            if (transition.Disposition == TransitionDisposition.Reconciled)
            {
                return transition.Record;
            }

            if (transition.Disposition != TransitionDisposition.Applied)
            {
                return null;
            }

            var updated = await statuses.UpdateAsync(transition.Record, storeETag, cancellationToken);
            if (updated.Disposition == StatusWriteDisposition.Succeeded)
            {
                return updated.Record;
            }

            if (updated.Disposition != StatusWriteDisposition.ConcurrencyConflict)
            {
                return null;
            }

            record = await statuses.GetAsync(record.StableId, cancellationToken) ?? record;
        }

        return null;
    }

    private async Task FailAndCleanupAsync(
        UploadOperation operation,
        FileRecord? record,
        BlobWriteResult? writtenBlob,
        string code,
        CancellationToken cancellationToken)
    {
        if (record is null)
        {
            return;
        }

        try
        {
            var properties = writtenBlob ??
                await blobs.GetPropertiesAsync(record.StableId, BlobArea.Pending, cancellationToken);
            if (properties is not null)
            {
                await blobs.DeleteIfMatchAsync(
                    record.StableId,
                    BlobArea.Pending,
                    properties.ETag,
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            telemetry.RecordCleanupFailure(operation, "pending-delete", exception);
        }

        try
        {
            var current = record;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (current.StoreETag is not { } currentETag)
                {
                    throw new InvalidOperationException("Upload status has no concurrency token.");
                }

                var transition = FileStateMachine.Transition(
                    current,
                    FileTransition.UploadFailed(code, timeProvider.GetUtcNow()));
                if (transition.Disposition == TransitionDisposition.Idempotent)
                {
                    return;
                }

                if (transition.Disposition != TransitionDisposition.Applied)
                {
                    throw new InvalidOperationException("Upload status could not be marked failed.");
                }

                var write = await statuses.UpdateAsync(
                    transition.Record,
                    currentETag,
                    cancellationToken);
                if (write.Disposition == StatusWriteDisposition.Succeeded)
                {
                    return;
                }

                if (write.Disposition != StatusWriteDisposition.ConcurrencyConflict)
                {
                    throw new InvalidOperationException(
                        $"Upload failure status write returned {write.Disposition}.");
                }

                current = await statuses.GetAsync(record.StableId, cancellationToken)
                    ?? throw new InvalidOperationException(
                        "Upload status disappeared during cleanup.");
            }

            throw new InvalidOperationException(
                "Upload failure status concurrency retry limit reached.");
        }
        catch (Exception exception)
        {
            telemetry.RecordCleanupFailure(operation, "status-failure", exception);
        }
    }

    private static string GetBoundary(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadPolicyException("multipart-required", "Submit one file as multipart form data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > MaxBoundaryLength)
        {
            throw new UploadPolicyException("invalid-boundary", "The upload request is malformed.");
        }

        return boundary;
    }

    private static UploadResult.Rejected Unavailable(string code) =>
        new(StatusCodes.Status503ServiceUnavailable, code, "The upload could not be accepted. Try again.");
}
