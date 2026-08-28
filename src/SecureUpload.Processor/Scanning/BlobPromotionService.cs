using Azure;
using Microsoft.Extensions.Logging.Abstractions;
using SecureUpload.Core.Telemetry;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Telemetry;

namespace SecureUpload.Processor.Scanning;

public sealed record PreparedBlobCopy(ETag TargetETag);

public sealed class BlobPromotionService(IBlobFileStore blobs, ScanTelemetry telemetry)
{
    private readonly IBlobFileStore _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
    private readonly ScanTelemetry _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

    public BlobPromotionService(IBlobFileStore blobs)
        : this(
            blobs,
            new ScanTelemetry(
                new TelemetryCorrelation("test-only-correlation-key-32-characters"),
                NullLogger<ScanTelemetry>.Instance))
    {
    }

    public async Task<PreparedBlobCopy> EnsureTargetCopyAsync(
        FileRecord record,
        BlobArea destination,
        CancellationToken cancellationToken = default)
    {
        var sourceETag = RequiredSourceETag(record);
        BlobWriteResult? target;
        BlobWriteResult? source;
        try
        {
            target = await _blobs.GetPropertiesAsync(record.StableId, destination, cancellationToken);
            source = await _blobs.GetPropertiesAsync(record.StableId, BlobArea.Pending, cancellationToken);
        }
        catch (Exception exception)
        {
            _telemetry.RecordBlobFailure(record.StableId, "verify", exception);
            throw;
        }

        if (record.TargetETag is not null &&
            target is not null &&
            StringComparer.Ordinal.Equals(record.TargetETag, target.ETag.ToString()))
        {
            return new(target.ETag);
        }

        if (target is not null)
        {
            var deletion = await _blobs.DeleteIfMatchAsync(
                record.StableId,
                destination,
                target.ETag,
                cancellationToken);
            if (deletion == ConditionalBlobDeleteDisposition.ETagMismatch)
            {
                var exception = new RetryableBlobOperationException();
                _telemetry.RecordBlobFailure(record.StableId, "target-delete", exception);
                throw exception;
            }
        }

        if (source is null || source.ETag != sourceETag)
        {
            throw new InvalidBlobRecoveryStateException();
        }

        BlobCopyResult copy;
        try
        {
            copy = await _blobs.CopyPendingAsync(
                record.StableId,
                destination,
                sourceETag,
                cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 404 or 412)
        {
            _telemetry.RecordBlobFailure(record.StableId, "copy", exception);
            throw new InvalidBlobRecoveryStateException();
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            _telemetry.RecordBlobFailure(record.StableId, "copy", exception);
            throw new RetryableBlobOperationException();
        }
        catch (Exception exception)
        {
            _telemetry.RecordBlobFailure(record.StableId, "copy", exception);
            throw;
        }
        var verified = await _blobs.GetPropertiesAsync(record.StableId, destination, cancellationToken);
        if (verified is null || verified.ETag != copy.DestinationETag)
        {
            var exception = new RetryableBlobOperationException();
            _telemetry.RecordBlobFailure(record.StableId, "verify", exception);
            throw exception;
        }

        return new(copy.DestinationETag);
    }

    public async Task CompleteSourceCleanupAsync(
        FileRecord record,
        BlobArea destination,
        ETag expectedTargetETag,
        CancellationToken cancellationToken = default)
    {
        var target = await _blobs.GetPropertiesAsync(record.StableId, destination, cancellationToken);
        if (target is null || target.ETag != expectedTargetETag)
        {
            throw new InvalidBlobRecoveryStateException();
        }

        var source = await _blobs.GetPropertiesAsync(record.StableId, BlobArea.Pending, cancellationToken);
        if (source is null)
        {
            return;
        }

        var sourceETag = RequiredSourceETag(record);
        if (source.ETag != sourceETag)
        {
            throw new InvalidBlobRecoveryStateException();
        }

        ConditionalBlobDeleteDisposition deletion;
        try
        {
            deletion = await _blobs.DeleteIfMatchAsync(
                record.StableId,
                BlobArea.Pending,
                sourceETag,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _telemetry.RecordBlobFailure(record.StableId, "source-delete", exception);
            throw;
        }
        if (deletion == ConditionalBlobDeleteDisposition.ETagMismatch)
        {
            var exception = new InvalidBlobRecoveryStateException();
            _telemetry.RecordBlobFailure(record.StableId, "source-delete", exception);
            throw exception;
        }
    }

    public async Task<bool> ConfirmTerminalAndCleanupSourceAsync(
        FileRecord record,
        BlobArea destination,
        CancellationToken cancellationToken = default)
    {
        var target = await _blobs.GetPropertiesAsync(record.StableId, destination, cancellationToken);
        if (target is null ||
            (record.TargetETag is not null &&
             !StringComparer.Ordinal.Equals(record.TargetETag, target.ETag.ToString())))
        {
            return false;
        }

        if (record.SourceETag is null)
        {
            return true;
        }

        ConditionalBlobDeleteDisposition deletion;
        try
        {
            deletion = await _blobs.DeleteIfMatchAsync(
                record.StableId,
                BlobArea.Pending,
                new ETag(record.SourceETag),
                cancellationToken);
        }
        catch (Exception exception)
        {
            _telemetry.RecordBlobFailure(record.StableId, "source-delete", exception);
            throw;
        }
        return deletion is ConditionalBlobDeleteDisposition.Deleted or
            ConditionalBlobDeleteDisposition.NotFound;
    }

    public async Task RemoveTargetAsync(
        string stableId,
        BlobArea destination,
        CancellationToken cancellationToken = default)
    {
        var target = await _blobs.GetPropertiesAsync(stableId, destination, cancellationToken);
        if (target is null)
        {
            return;
        }

        ConditionalBlobDeleteDisposition result;
        try
        {
            result = await _blobs.DeleteIfMatchAsync(
                stableId,
                destination,
                target.ETag,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _telemetry.RecordBlobFailure(stableId, "target-delete", exception);
            throw;
        }
        if (result == ConditionalBlobDeleteDisposition.ETagMismatch)
        {
            var exception = new RetryableBlobOperationException();
            _telemetry.RecordBlobFailure(stableId, "target-delete", exception);
            throw exception;
        }
    }

    private static ETag RequiredSourceETag(FileRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.SourceETag))
        {
            throw new InvalidBlobRecoveryStateException();
        }

        return new ETag(record.SourceETag);
    }
}

public sealed class InvalidBlobRecoveryStateException : Exception;

public sealed class RetryableBlobOperationException : Exception;
