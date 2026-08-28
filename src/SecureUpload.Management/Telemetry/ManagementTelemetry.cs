using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using SecureUpload.Core.Telemetry;

namespace SecureUpload.Management.Telemetry;

public sealed class ManagementTelemetry(ILogger<ManagementTelemetry> logger)
{
    private static readonly Meter Metrics = new(TelemetryNames.Meter);
    private static readonly Counter<long> CapacityExceeded =
        Metrics.CreateCounter<long>(TelemetryNames.ManagementInventoryCapacityExceeded);
    private static readonly Counter<long> StorageFailures =
        Metrics.CreateCounter<long>(TelemetryNames.ManagementInventoryStorageFailure);
    private static readonly Counter<long> DownloadIntegrityFailures =
        Metrics.CreateCounter<long>(TelemetryNames.ManagementDownloadIntegrityFailure);
    private static readonly Counter<long> ActionStorageFailures =
        Metrics.CreateCounter<long>(TelemetryNames.ManagementActionStorageFailure);

    public void RecordCapacityExceeded()
    {
        CapacityExceeded.Add(1);
        logger.LogError("Management inventory exceeded the configured browsing capacity.");
    }

    public void RecordInventoryStorageFailure(string operation, Exception exception)
    {
        var safeOperation = operation is "query" or "lookup" ? operation : "other";
        StorageFailures.Add(
            1,
            new TagList
            {
                { TelemetryNames.OperationTag, safeOperation }
            });
        logger.LogError(
            "Management storage access failed. Operation={Operation} ExceptionType={ExceptionType}",
            safeOperation,
            exception.GetType().Name);
    }

    public void RecordDownloadIntegrityFailure(string reason)
    {
        var safeReason = reason is "blob-missing" or "etag-mismatch" or "target-etag-missing"
            ? reason
            : "other";
        DownloadIntegrityFailures.Add(
            1,
            new TagList
            {
                { TelemetryNames.ReasonTag, safeReason }
            });
        logger.LogWarning(
            "Management clean download integrity check failed. Reason={Reason}",
            safeReason);
    }

    public void RecordActionStorageFailure(string operation, Exception exception)
    {
        var safeOperation = operation is "download" or "delete" ? operation : "other";
        ActionStorageFailures.Add(
            1,
            new TagList
            {
                { TelemetryNames.OperationTag, safeOperation }
            });
        logger.LogError(
            "Management action failed. Operation={Operation} ExceptionType={ExceptionType}",
            safeOperation,
            exception.GetType().Name);
    }
}
