using Azure;
using Microsoft.Extensions.Logging.Abstractions;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Core.Telemetry;
using SecureUpload.Processor.Telemetry;

namespace SecureUpload.Processor.Scanning;

public enum DeletionReconciliationDisposition
{
    Deleted,
    AlreadyDeleted,
    Incomplete
}

public sealed record DeletionReconciliationResult(DeletionReconciliationDisposition Disposition);

public sealed record PendingDeletionSweepResult(
    int Finalized,
    int AlreadyDeleted,
    int Incomplete);

public sealed class DeletionProcessor
{
    private readonly IFileStatusStore _statusStore;
    private readonly FileDeletionCleanup _cleanup;
    private readonly ScanProcessorOptions _options;
    private readonly ScanTelemetry _telemetry;

    public DeletionProcessor(
        IFileStatusStore statusStore,
        FileDeletionCleanup cleanup,
        ScanProcessorOptions options,
        ScanTelemetry telemetry)
    {
        _statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _options.Validate();
    }

    public DeletionProcessor(
        IFileStatusStore statusStore,
        FileDeletionCleanup cleanup,
        ScanProcessorOptions options)
        : this(
            statusStore,
            cleanup,
            options,
            new ScanTelemetry(
                new TelemetryCorrelation("test-only-correlation-key-32-characters"),
                NullLogger<ScanTelemetry>.Instance))
    {
    }

    public async Task<PendingDeletionSweepResult> ProcessPendingAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var records = new List<FileRecord>();
        await foreach (var record in _statusStore.QueryAsync(
                           new FileStatusQuery(FileState.Deleting),
                           cancellationToken))
        {
            records.Add(record);
        }

        var finalized = 0;
        var alreadyDeleted = 0;
        var incomplete = 0;
        foreach (var record in records)
        {
            var result = await ReconcileAsync(record, now, cancellationToken)
                ?? throw new InvalidOperationException("Deleting record unexpectedly skipped reconciliation.");
            switch (result.Disposition)
            {
                case DeletionReconciliationDisposition.Deleted:
                    finalized++;
                    break;
                case DeletionReconciliationDisposition.AlreadyDeleted:
                    alreadyDeleted++;
                    break;
                case DeletionReconciliationDisposition.Incomplete:
                    incomplete++;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected deletion reconciliation result: {result.Disposition}.");
            }
        }

        return new(finalized, alreadyDeleted, incomplete);
    }

    public async Task<DeletionReconciliationResult?> ReconcileAsync(
        FileRecord current,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.State is not (FileState.Deleting or FileState.Deleted))
        {
            return null;
        }

        try
        {
            var cleanup = await _cleanup.CleanupAsync(current.StableId, cancellationToken);
            _telemetry.RecordDeletionCleanup(cleanup);
            if (!cleanup.IsComplete)
            {
                return new(DeletionReconciliationDisposition.Incomplete);
            }

            if (current.State == FileState.Deleted)
            {
                return new(DeletionReconciliationDisposition.AlreadyDeleted);
            }

            var record = current;
            for (var attempt = 0; attempt < _options.MaximumConcurrencyAttempts; attempt++)
            {
                if (record.State == FileState.Deleted)
                {
                    return new(DeletionReconciliationDisposition.AlreadyDeleted);
                }

                if (record.State != FileState.Deleting || record.StoreETag is null)
                {
                    throw new InvalidOperationException("Deletion reconciliation requires a deleting record with a concrete Table ETag.");
                }

                var transition = FileStateMachine.Transition(record, FileTransition.DeleteCompleted(completedAt));
                if (transition.Disposition == TransitionDisposition.Idempotent)
                {
                    return new(DeletionReconciliationDisposition.AlreadyDeleted);
                }

                if (transition.Disposition != TransitionDisposition.Applied)
                {
                    throw new InvalidOperationException("Deletion reconciliation could not complete the tombstone.");
                }

                var write = await _statusStore.UpdateAsync(
                    transition.Record,
                    record.StoreETag.Value,
                    cancellationToken);
                switch (write.Disposition)
                {
                    case StatusWriteDisposition.Succeeded:
                        return new(DeletionReconciliationDisposition.Deleted);
                    case StatusWriteDisposition.NotFound:
                        throw new RetryableScanProcessingException("Status record disappeared.");
                    case StatusWriteDisposition.ConcurrencyConflict:
                        _telemetry.RecordRetry("concurrency");
                        var refreshed = await _statusStore.GetAsync(record.StableId, cancellationToken);
                        if (refreshed is null)
                        {
                            throw new RetryableScanProcessingException("Status record disappeared.");
                        }

                        record = refreshed;
                        break;
                    default:
                        throw new RetryableScanProcessingException("Status update failed.");
                }
            }

            throw new RetryableScanProcessingException("Status concurrency retry limit reached.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RequestFailedException exception) when (IsTransient(exception.Status))
        {
            throw new RetryableScanProcessingException("Transient storage failure.");
        }
    }

    private static bool IsTransient(int status) =>
        status is 408 or 429 || status >= 500;
}
