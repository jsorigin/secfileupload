using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using SecureUpload.Core.Storage;
using SecureUpload.Core.Telemetry;
using SecureUpload.Processor.Scanning;

namespace SecureUpload.Processor.Telemetry;

public sealed class ScanTelemetry
{
    private static readonly ActivitySource Activities = new(TelemetryNames.ActivitySource);
    private static readonly Meter Metrics = new(TelemetryNames.Meter);
    private static readonly Counter<long> Outcomes = Metrics.CreateCounter<long>(TelemetryNames.ScanOutcome);
    private static readonly Histogram<double> Latency = Metrics.CreateHistogram<double>(
        TelemetryNames.ScanLatency,
        unit: "s");
    private static readonly Counter<long> InvalidEvents = Metrics.CreateCounter<long>(TelemetryNames.InvalidEvent);
    private static readonly Counter<long> Retries = Metrics.CreateCounter<long>(TelemetryNames.ProcessingRetry);
    private static readonly Counter<long> StalePending = Metrics.CreateCounter<long>(TelemetryNames.StalePending);
    private static readonly Histogram<double> PendingAge = Metrics.CreateHistogram<double>(
        TelemetryNames.OldestPendingAge,
        unit: "s");
    private static readonly Counter<long> BlobFailures =
        Metrics.CreateCounter<long>(TelemetryNames.BlobOperationFailure);
    private static readonly Counter<long> Conflicts = Metrics.CreateCounter<long>(TelemetryNames.TerminalConflict);
    private static readonly Counter<long> DeletionCleanupRetries =
        Metrics.CreateCounter<long>(TelemetryNames.DeletionCleanupRetry);
    private static readonly Counter<long> DeletionCleanupFailures =
        Metrics.CreateCounter<long>(TelemetryNames.DeletionCleanupFailure);

    private readonly TelemetryCorrelation _correlation;
    private readonly ILogger<ScanTelemetry> _logger;

    public ScanTelemetry(TelemetryCorrelation correlation, ILogger<ScanTelemetry> logger)
    {
        _correlation = correlation;
        _logger = logger;
    }

    public ScanOperation Start(MalwareScanEvent scanEvent)
    {
        var operationId = _correlation.ForStableId(scanEvent.StableId);
        var activity = Activities.StartActivity("secure-upload.scan", ActivityKind.Consumer);
        activity?.SetTag(TelemetryNames.OperationIdTag, operationId);
        return new ScanOperation(operationId, activity);
    }

    public void RecordOutcome(
        ScanOperation operation,
        MalwareScanEvent scanEvent,
        ScanProcessingDisposition disposition)
    {
        var outcome = disposition.ToString().ToLowerInvariant();
        var tags = new TagList
        {
            { TelemetryNames.OperationIdTag, operation.Id },
            { TelemetryNames.OutcomeTag, outcome },
            { "secure_upload.scan_result", SafeOutcome(scanEvent.Outcome) },
            { "secure_upload.failure_class", SafeFailureClass(scanEvent.FailureCode) }
        };
        Outcomes.Add(1, tags);
        if (disposition == ScanProcessingDisposition.OperationalConflict)
        {
            Conflicts.Add(1, tags);
        }

        operation.Activity?.SetTag(TelemetryNames.OutcomeTag, outcome);
        operation.Activity?.SetStatus(
            disposition is ScanProcessingDisposition.OperationalConflict or
                ScanProcessingDisposition.ScanErrorRecorded
                ? ActivityStatusCode.Error
                : ActivityStatusCode.Ok);
        _logger.Log(
            disposition is ScanProcessingDisposition.OperationalConflict or
                ScanProcessingDisposition.ScanErrorRecorded
                ? LogLevel.Error
                : LogLevel.Information,
            "Scan processing completed. OperationId={OperationId} Outcome={Outcome} ScanResult={ScanResult} FailureClass={FailureClass}",
            operation.Id,
            outcome,
            SafeOutcome(scanEvent.Outcome),
            SafeFailureClass(scanEvent.FailureCode));
    }

    public void RecordScanLatency(
        string stableId,
        DateTimeOffset uploadStartedAt,
        DateTimeOffset scanFinishedAt)
    {
        Latency.Record(
            Math.Max(0, (scanFinishedAt - uploadStartedAt).TotalSeconds),
            new KeyValuePair<string, object?>(
                TelemetryNames.OperationIdTag,
                _correlation.ForStableId(stableId)));
    }

    public void RecordInvalidEvent(string category)
    {
        var safeCategory = category is
            "blob-uri-invalid" or
            "blob-origin" or
            "blob-container" or
            "blob-name" or
            "subject" or
            "event-type" or
            "data-version" or
            "metadata-version" or
            "topic" or
            "envelope" or
            "malformed" or
            "oversized"
            ? category
            : "unknown";
        InvalidEvents.Add(1, new KeyValuePair<string, object?>(TelemetryNames.ReasonTag, safeCategory));
        _logger.LogWarning("Scan event rejected. Reason={Reason}", safeCategory);
    }

    public void RecordRetry(string reason, string? stableId = null)
    {
        var safeReason = reason is "storage" or "concurrency" or "unexpected" or "watchdog"
            ? reason
            : "other";
        var tags = new TagList { { TelemetryNames.ReasonTag, safeReason } };
        if (stableId is not null)
        {
            tags.Add(TelemetryNames.OperationIdTag, _correlation.ForStableId(stableId));
        }

        Retries.Add(1, tags);
        _logger.LogWarning("Scan processing will retry. Reason={Reason}", safeReason);
    }

    public void RecordStalePending(string stableId, TimeSpan age)
    {
        var operationId = _correlation.ForStableId(stableId);
        var tags = new TagList { { TelemetryNames.OperationIdTag, operationId } };
        StalePending.Add(1, tags);
        PendingAge.Record(Math.Max(0, age.TotalSeconds), tags);
        _logger.LogError(
            "Pending scan exceeded the watchdog threshold. OperationId={OperationId} AgeSeconds={AgeSeconds}",
            operationId,
            Math.Max(0, age.TotalSeconds));
    }

    public void RecordBlobFailure(string stableId, string operation, Exception exception)
    {
        var operationId = _correlation.ForStableId(stableId);
        var safeOperation = operation is "copy" or "source-delete" or "target-delete" or "verify"
            ? operation
            : "other";
        BlobFailures.Add(
            1,
            new TagList
            {
                { TelemetryNames.OperationIdTag, operationId },
                { TelemetryNames.OperationTag, safeOperation }
            });
        _logger.LogError(
            "Scan blob operation failed. OperationId={OperationId} Operation={Operation} ExceptionType={ExceptionType}",
            operationId,
            safeOperation,
            exception.GetType().Name);
    }

    public void RecordDeletionCleanup(FileDeletionCleanupResult cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);

        foreach (var area in cleanup.Areas)
        {
            var safeArea = SafeArea(area.Area);
            var retries = Math.Max(0, area.Attempts - 1);
            if (retries > 0)
            {
                DeletionCleanupRetries.Add(
                    retries,
                    new TagList
                    {
                        { TelemetryNames.BlobAreaTag, safeArea }
                    });
                _logger.LogWarning(
                    "Deletion cleanup retried. BlobArea={BlobArea} Retries={Retries}",
                    safeArea,
                    retries);
            }

            if (area.Disposition != BlobAreaCleanupDisposition.Incomplete)
            {
                continue;
            }

            DeletionCleanupFailures.Add(
                1,
                new TagList
                {
                    { TelemetryNames.BlobAreaTag, safeArea }
                });
            _logger.LogError(
                "Deletion cleanup remained incomplete. BlobArea={BlobArea}",
                safeArea);
        }
    }

    private static string SafeOutcome(MalwareScanOutcome outcome) =>
        outcome switch
        {
            MalwareScanOutcome.Clean => "clean",
            MalwareScanOutcome.Malicious => "malicious",
            MalwareScanOutcome.Delayed => "delayed",
            MalwareScanOutcome.ScanError => "scan-error",
            _ => "unknown"
        };

    private static string SafeFailureClass(string? failureCode)
    {
        if (failureCode is null)
        {
            return "none";
        }

        if (failureCode.StartsWith("sam-", StringComparison.Ordinal))
        {
            return failureCode;
        }

        return failureCode is "scan-error" or "scan-result-unknown" or
            "scan-event-malformed" or "blob-state-invalid" or "scan-watchdog-expired"
            ? failureCode
            : "other";
    }

    private static string SafeArea(BlobArea area) =>
        area switch
        {
            BlobArea.Pending => "pending",
            BlobArea.Clean => "clean",
            BlobArea.Quarantine => "quarantine",
            _ => "other"
        };
}

public sealed class ScanOperation(string id, Activity? activity) : IDisposable
{
    public string Id { get; } = id;
    public Activity? Activity { get; } = activity;

    public void Dispose() => Activity?.Dispose();
}
