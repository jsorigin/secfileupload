using Azure;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using SecureUpload.Core.Telemetry;

namespace SecureUpload.Processor.Scanning;

public enum ScanProcessingDisposition
{
    Completed,
    Duplicate,
    ScanErrorRecorded,
    Deferred,
    PermanentRejection,
    OperationalConflict
}

public sealed record ScanProcessingResult(ScanProcessingDisposition Disposition);

public sealed class RetryableScanProcessingException(string message) : Exception(message);

public sealed class ScanResultProcessor
{
    private readonly IFileStatusStore _statusStore;
    private readonly BlobPromotionService _promotion;
    private readonly DeletionProcessor _deletions;
    private readonly ScanProcessorOptions _options;
    private readonly ScanTelemetry _telemetry;

    public ScanResultProcessor(
        IFileStatusStore statusStore,
        BlobPromotionService promotion,
        DeletionProcessor deletions,
        ScanProcessorOptions options,
        ScanTelemetry telemetry)
    {
        _statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
        _promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
        _deletions = deletions ?? throw new ArgumentNullException(nameof(deletions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _options.Validate();
    }

    public ScanResultProcessor(
        IFileStatusStore statusStore,
        BlobPromotionService promotion,
        DeletionProcessor deletions,
        ScanProcessorOptions options)
        : this(
            statusStore,
            promotion,
            deletions,
            options,
            new ScanTelemetry(
                new TelemetryCorrelation("test-only-correlation-key-32-characters"),
                NullLogger<ScanTelemetry>.Instance))
    {
    }

    public async Task<ScanProcessingResult> ProcessAsync(
        MalwareScanEvent scanEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanEvent);

        var latencyRecorded = false;
        for (var attempt = 0; attempt < _options.MaximumConcurrencyAttempts; attempt++)
        {
            try
            {
                var current = await _statusStore.GetAsync(scanEvent.StableId, cancellationToken);
                if (current is null || !BlobIdentityMatches(current, scanEvent))
                {
                    return new(ScanProcessingDisposition.PermanentRejection);
                }

                if (!latencyRecorded)
                {
                    _telemetry.RecordScanLatency(
                        current.StableId,
                        current.UploadedAt ?? current.CreatedAt,
                        scanEvent.ScanFinishedAt);
                    latencyRecorded = true;
                }

                var deletion = await _deletions.ReconcileAsync(
                    current,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                if (deletion is not null)
                {
                    if (deletion.Disposition == DeletionReconciliationDisposition.Incomplete)
                    {
                        throw new RetryableScanProcessingException("Deletion cleanup incomplete.");
                    }

                    return new(ScanProcessingDisposition.Duplicate);
                }

                if (scanEvent.Outcome == MalwareScanOutcome.Delayed)
                {
                    return new(ScanProcessingDisposition.Deferred);
                }

                if ((current.State == FileState.Promoting &&
                     scanEvent.Outcome == MalwareScanOutcome.Malicious) ||
                    (current.State == FileState.Quarantining &&
                     scanEvent.Outcome == MalwareScanOutcome.Clean))
                {
                    return new(ScanProcessingDisposition.OperationalConflict);
                }

                var terminal = await HandleTerminalAsync(current, scanEvent, cancellationToken);
                if (terminal is not null)
                {
                    return terminal;
                }

                if (scanEvent.Outcome == MalwareScanOutcome.ScanError)
                {
                    if (current.State is FileState.Promoting or FileState.Quarantining)
                    {
                        await _promotion.RemoveTargetAsync(
                            current.StableId,
                            current.State == FileState.Promoting
                                ? BlobArea.Clean
                                : BlobArea.Quarantine,
                            cancellationToken);
                    }

                    var transition = FileStateMachine.Transition(
                        current,
                        FileTransition.ScanFailed(
                            scanEvent.EventId,
                            scanEvent.CorrelationId,
                            scanEvent.SourceETag.ToString(),
                            scanEvent.FailureCode ?? "scan-error",
                            scanEvent.ScanFinishedAt));
                    if (transition.Disposition == TransitionDisposition.Rejected)
                    {
                        return MapRejection(transition.Rejection);
                    }

                    if (transition.Disposition == TransitionDisposition.Idempotent)
                    {
                        return new(ScanProcessingDisposition.Duplicate);
                    }

                    var write = await UpdateAsync(current, transition.Record, cancellationToken);
                    if (write is null)
                    {
                        continue;
                    }

                    return new(ScanProcessingDisposition.ScanErrorRecorded);
                }

                var processing = await EnterProcessingAsync(current, scanEvent, cancellationToken);
                if (processing.Result is not null)
                {
                    return processing.Result;
                }

                if (processing.Record is null)
                {
                    continue;
                }

                var destination = scanEvent.Outcome == MalwareScanOutcome.Clean
                    ? BlobArea.Clean
                    : BlobArea.Quarantine;
                PreparedBlobCopy copy;
                try
                {
                    copy = await _promotion.EnsureTargetCopyAsync(
                        processing.Record,
                        destination,
                        cancellationToken);
                }
                catch (InvalidBlobRecoveryStateException)
                {
                    return await FailClosedRecoveryAsync(
                        processing.Record,
                        scanEvent,
                        destination,
                        cancellationToken);
                }

                var copiedTransition = FileStateMachine.Transition(
                    processing.Record,
                    FileTransition.TargetCopyRecorded(copy.TargetETag.ToString(), scanEvent.ScanFinishedAt));
                if (copiedTransition.Disposition == TransitionDisposition.Rejected)
                {
                    return MapRejection(copiedTransition.Rejection);
                }

                var copied = processing.Record;
                if (copiedTransition.Disposition == TransitionDisposition.Applied)
                {
                    var write = await UpdateAsync(processing.Record, copiedTransition.Record, cancellationToken);
                    if (write is null)
                    {
                        continue;
                    }

                    copied = write;
                }

                try
                {
                    await _promotion.CompleteSourceCleanupAsync(
                        copied,
                        destination,
                        copy.TargetETag,
                        cancellationToken);
                }
                catch (InvalidBlobRecoveryStateException)
                {
                    return await FailClosedRecoveryAsync(
                        copied,
                        scanEvent,
                        destination,
                        cancellationToken);
                }

                var completion = FileStateMachine.Transition(
                    copied,
                    scanEvent.Outcome == MalwareScanOutcome.Clean
                        ? FileTransition.PromotionCompleted(copy.TargetETag.ToString(), scanEvent.ScanFinishedAt)
                        : FileTransition.QuarantineCompleted(copy.TargetETag.ToString(), scanEvent.ScanFinishedAt));
                if (completion.Disposition == TransitionDisposition.Rejected)
                {
                    return MapRejection(completion.Rejection);
                }

                var completed = await UpdateAsync(copied, completion.Record, cancellationToken);
                if (completed is null)
                {
                    continue;
                }

                return new(ScanProcessingDisposition.Completed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RequestFailedException exception) when (IsTransient(exception.Status))
            {
                throw new RetryableScanProcessingException("Transient storage failure.");
            }
            catch (RetryableBlobOperationException)
            {
                throw new RetryableScanProcessingException("Transient storage failure.");
            }
        }

        throw new RetryableScanProcessingException("Status concurrency retry limit reached.");
    }

    private async Task<(FileRecord? Record, ScanProcessingResult? Result)> EnterProcessingAsync(
        FileRecord current,
        MalwareScanEvent scanEvent,
        CancellationToken cancellationToken)
    {
        var transition = FileStateMachine.Transition(
            current,
            scanEvent.Outcome == MalwareScanOutcome.Clean
                ? FileTransition.Clean(
                    scanEvent.EventId,
                    scanEvent.CorrelationId,
                    scanEvent.SourceETag.ToString(),
                    scanEvent.ScanFinishedAt)
                : FileTransition.Malicious(
                    scanEvent.EventId,
                    scanEvent.CorrelationId,
                    scanEvent.SourceETag.ToString(),
                    scanEvent.ScanFinishedAt));

        if (transition.Disposition == TransitionDisposition.Rejected)
        {
            return (null, MapRejection(transition.Rejection));
        }

        if (transition.Disposition == TransitionDisposition.Idempotent)
        {
            return (current, null);
        }

        var write = await UpdateAsync(current, transition.Record, cancellationToken);
        return (write, null);
    }

    private async Task<ScanProcessingResult?> HandleTerminalAsync(
        FileRecord current,
        MalwareScanEvent scanEvent,
        CancellationToken cancellationToken)
    {
        if (current.State is not (FileState.Available or FileState.Rejected))
        {
            return null;
        }

        var matching =
            (current.State == FileState.Available && scanEvent.Outcome == MalwareScanOutcome.Clean) ||
            (current.State == FileState.Rejected && scanEvent.Outcome == MalwareScanOutcome.Malicious);
        if (!matching)
        {
            return new(ScanProcessingDisposition.OperationalConflict);
        }

        if (!StringComparer.Ordinal.Equals(current.SourceETag, scanEvent.SourceETag.ToString()))
        {
            return new(ScanProcessingDisposition.PermanentRejection);
        }

        var confirmed = await _promotion.ConfirmTerminalAndCleanupSourceAsync(
            current,
            current.State == FileState.Available ? BlobArea.Clean : BlobArea.Quarantine,
            cancellationToken);
        return new(
            confirmed
                ? ScanProcessingDisposition.Duplicate
                : ScanProcessingDisposition.OperationalConflict);
    }

    private async Task<ScanProcessingResult> FailClosedRecoveryAsync(
        FileRecord current,
        MalwareScanEvent scanEvent,
        BlobArea destination,
        CancellationToken cancellationToken)
    {
        await _promotion.RemoveTargetAsync(current.StableId, destination, cancellationToken);
        var failed = FileStateMachine.Transition(
            current,
            FileTransition.ScanFailed(
                scanEvent.EventId,
                scanEvent.CorrelationId,
                scanEvent.SourceETag.ToString(),
                "blob-state-invalid",
                scanEvent.ScanFinishedAt));
        if (failed.Disposition == TransitionDisposition.Rejected)
        {
            return MapRejection(failed.Rejection);
        }

        var write = await UpdateAsync(current, failed.Record, cancellationToken);
        return write is null
            ? throw new RetryableScanProcessingException("Status concurrency retry limit reached.")
            : new ScanProcessingResult(ScanProcessingDisposition.ScanErrorRecorded);
    }

    private async Task<FileRecord?> UpdateAsync(
        FileRecord current,
        FileRecord updated,
        CancellationToken cancellationToken)
    {
        if (current.StoreETag is null)
        {
            return null;
        }

        var write = await _statusStore.UpdateAsync(
            updated,
            current.StoreETag.Value,
            cancellationToken);
        return write.Disposition switch
        {
            StatusWriteDisposition.Succeeded => write.Record,
            StatusWriteDisposition.ConcurrencyConflict => null,
            StatusWriteDisposition.NotFound => throw new RetryableScanProcessingException("Status record disappeared."),
            _ => throw new RetryableScanProcessingException("Status update failed.")
        };
    }

    private static bool BlobIdentityMatches(FileRecord record, MalwareScanEvent scanEvent) =>
        (record.PendingBlobUri is null ||
         Uri.Compare(
             record.PendingBlobUri,
             scanEvent.BlobUri,
             UriComponents.AbsoluteUri,
             UriFormat.SafeUnescaped,
             StringComparison.Ordinal) == 0) &&
        (record.SourceETag is null ||
         StringComparer.Ordinal.Equals(record.SourceETag, scanEvent.SourceETag.ToString()));

    private static ScanProcessingResult MapRejection(TransitionRejection rejection) =>
        new(rejection == TransitionRejection.TerminalConflict
            ? ScanProcessingDisposition.OperationalConflict
            : ScanProcessingDisposition.PermanentRejection);

    private static bool IsTransient(int status) =>
        status is 408 or 429 || status >= 500;
}
