using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using SecureUpload.Core.Telemetry;

namespace SecureUpload.Processor.Scanning;

public sealed record StalePendingResult(int MarkedScanError);

public sealed class StalePendingWatchdog
{
    private readonly IFileStatusStore _statusStore;
    private readonly ScanProcessorOptions _options;
    private readonly ScanTelemetry _telemetry;

    public StalePendingWatchdog(
        IFileStatusStore statusStore,
        ScanProcessorOptions options,
        ScanTelemetry telemetry)
    {
        _statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _options.Validate();
    }

    public StalePendingWatchdog(IFileStatusStore statusStore, ScanProcessorOptions options)
        : this(
            statusStore,
            options,
            new ScanTelemetry(
                new TelemetryCorrelation("test-only-correlation-key-32-characters"),
                NullLogger<ScanTelemetry>.Instance))
    {
    }

    public async Task<StalePendingResult> DetectAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var threshold = now - _options.ScanWatchdogThreshold;
        var records = new List<FileRecord>();
        await foreach (var record in _statusStore.QueryAsync(
                           new FileStatusQuery(FileState.Pending, threshold),
                           cancellationToken))
        {
            records.Add(record);
        }

        var marked = 0;
        foreach (var record in records)
        {
            if (await MarkStaleAsync(record, threshold, now, cancellationToken))
            {
                marked++;
            }
        }

        return new(marked);
    }

    private async Task<bool> MarkStaleAsync(
        FileRecord queriedRecord,
        DateTimeOffset threshold,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = queriedRecord;
        for (var attempt = 0; attempt < _options.MaximumConcurrencyAttempts; attempt++)
        {
            if (current.State != FileState.Pending ||
                current.UpdatedAt >= threshold ||
                string.IsNullOrWhiteSpace(current.SourceETag) ||
                current.StoreETag is null)
            {
                return false;
            }

            var transition = FileStateMachine.Transition(
                current,
                FileTransition.ScanFailed(
                    "watchdog",
                    "watchdog",
                    current.SourceETag,
                    "scan-watchdog-expired",
                    now));
            if (transition.Disposition != TransitionDisposition.Applied)
            {
                return false;
            }

            var write = await _statusStore.UpdateAsync(
                transition.Record,
                current.StoreETag.Value,
                cancellationToken);
            if (write.Disposition == StatusWriteDisposition.Succeeded)
            {
                _telemetry.RecordStalePending(current.StableId, now - current.UpdatedAt);
                return true;
            }

            if (write.Disposition != StatusWriteDisposition.ConcurrencyConflict)
            {
                throw new RetryableScanProcessingException("Watchdog status update failed.");
            }

            _telemetry.RecordRetry("concurrency", current.StableId);
            var refreshed = await _statusStore.GetAsync(current.StableId, cancellationToken);
            if (refreshed is null)
            {
                return false;
            }

            current = refreshed;
        }

        throw new RetryableScanProcessingException("Watchdog concurrency retry limit reached.");
    }
}
