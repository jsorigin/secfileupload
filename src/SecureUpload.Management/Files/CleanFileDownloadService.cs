using Azure;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Management.Telemetry;

namespace SecureUpload.Management.Files;

public enum CleanFileDownloadDisposition
{
    Ready,
    InvalidId,
    NotFound,
    NotAvailable,
    IntegrityFailure,
    StorageError
}

public enum CleanFileIntegrityFailureReason
{
    None,
    MissingTargetETag,
    BlobMissing,
    ETagMismatch
}

public sealed record CleanFileDownloadResult(
    CleanFileDownloadDisposition Disposition,
    Stream? Content = null,
    string DownloadFileName = "",
    CleanFileIntegrityFailureReason IntegrityFailureReason = CleanFileIntegrityFailureReason.None);

public sealed class CleanFileDownloadService(
    IFileStatusStore statusStore,
    IBlobFileStore blobs,
    ManagementTelemetry telemetry)
{
    public async Task<CleanFileDownloadResult> OpenReadAsync(
        string? fileId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeStableId(fileId, out var stableId))
        {
            return new(CleanFileDownloadDisposition.InvalidId);
        }

        try
        {
            var record = await statusStore.GetAsync(stableId, cancellationToken);
            if (record is null)
            {
                return new(CleanFileDownloadDisposition.NotFound);
            }

            if (record.State != FileState.Available)
            {
                return new(CleanFileDownloadDisposition.NotAvailable);
            }

            if (string.IsNullOrWhiteSpace(record.TargetETag))
            {
                telemetry.RecordDownloadIntegrityFailure("target-etag-missing");
                return new(
                    CleanFileDownloadDisposition.IntegrityFailure,
                    IntegrityFailureReason: CleanFileIntegrityFailureReason.MissingTargetETag);
            }

            var expectedTargetETag = new ETag(record.TargetETag);
            var read = await blobs.OpenCleanReadIfMatchAsync(
                stableId,
                expectedTargetETag,
                cancellationToken);

            switch (read.Disposition)
            {
                case ConditionalBlobReadDisposition.Succeeded when
                    read.Blob is not null &&
                    read.Blob.ETag == expectedTargetETag:
                    return new(
                        CleanFileDownloadDisposition.Ready,
                        read.Blob.Content,
                        GetDownloadFileName(record));
                case ConditionalBlobReadDisposition.Succeeded when read.Blob is not null:
                    await read.Blob.Content.DisposeAsync();
                    telemetry.RecordDownloadIntegrityFailure("etag-mismatch");
                    return new(
                        CleanFileDownloadDisposition.IntegrityFailure,
                        IntegrityFailureReason: CleanFileIntegrityFailureReason.ETagMismatch);
                case ConditionalBlobReadDisposition.NotFound:
                    telemetry.RecordDownloadIntegrityFailure("blob-missing");
                    return new(
                        CleanFileDownloadDisposition.IntegrityFailure,
                        IntegrityFailureReason: CleanFileIntegrityFailureReason.BlobMissing);
                case ConditionalBlobReadDisposition.ETagMismatch:
                    telemetry.RecordDownloadIntegrityFailure("etag-mismatch");
                    return new(
                        CleanFileDownloadDisposition.IntegrityFailure,
                        IntegrityFailureReason: CleanFileIntegrityFailureReason.ETagMismatch);
                default:
                    telemetry.RecordActionStorageFailure(
                        "download",
                        new InvalidOperationException("Unexpected clean-read result."));
                    return new(CleanFileDownloadDisposition.StorageError);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            telemetry.RecordActionStorageFailure("download", exception);
            return new(CleanFileDownloadDisposition.StorageError);
        }
    }

    private static string GetDownloadFileName(FileRecord record)
    {
        var normalized = ManagementFileView.NormalizeFileName(record.OriginalFileName);
        return string.IsNullOrWhiteSpace(normalized)
            ? "download.bin"
            : normalized;
    }

    private static bool TryNormalizeStableId(string? candidate, out string stableId)
    {
        stableId = candidate?.Trim().ToLowerInvariant() ?? string.Empty;
        if (stableId.Length != 64 || stableId.Any(character =>
                character is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
        {
            stableId = string.Empty;
            return false;
        }

        return true;
    }
}
