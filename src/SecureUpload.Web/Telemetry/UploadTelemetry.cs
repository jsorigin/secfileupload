using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using SecureUpload.Core.Telemetry;

namespace SecureUpload.Web.Telemetry;

public sealed class UploadTelemetry
{
    private static readonly ActivitySource Activities = new(TelemetryNames.ActivitySource);
    private static readonly Meter Metrics = new(TelemetryNames.Meter);
    private static readonly Counter<long> Accepted = Metrics.CreateCounter<long>(TelemetryNames.UploadAccepted);
    private static readonly Counter<long> Rejected = Metrics.CreateCounter<long>(TelemetryNames.UploadRejected);
    private static readonly Histogram<long> Bytes = Metrics.CreateHistogram<long>(
        TelemetryNames.UploadBytes,
        unit: "By");
    private static readonly Counter<long> RateLimited = Metrics.CreateCounter<long>(TelemetryNames.UploadRateLimited);
    private static readonly Counter<long> Failures = Metrics.CreateCounter<long>(TelemetryNames.UploadFailure);
    private static readonly Counter<long> CleanupFailures =
        Metrics.CreateCounter<long>(TelemetryNames.UploadCleanupFailure);
    private static readonly Counter<long> KillSwitch = Metrics.CreateCounter<long>(TelemetryNames.UploadKillSwitch);

    private readonly TelemetryCorrelation _correlation;
    private readonly ILogger<UploadTelemetry> _logger;

    public UploadTelemetry(TelemetryCorrelation correlation, ILogger<UploadTelemetry> logger)
    {
        _correlation = correlation;
        _logger = logger;
    }

    public UploadOperation Start()
    {
        var operationId = TelemetryCorrelation.CreateOperationId();
        var activity = Activities.StartActivity("secure-upload.accept", ActivityKind.Server);
        activity?.SetTag(TelemetryNames.OperationIdTag, operationId);
        return new UploadOperation(operationId, activity);
    }

    public void RecordAccepted(UploadOperation operation, string stableId, long bytes)
    {
        var correlationId = _correlation.ForStableId(stableId);
        var tags = Tags(operation.Id, correlationId);
        Accepted.Add(1, tags);
        Bytes.Record(bytes, tags);
        operation.Activity?.SetTag("secure_upload.file_correlation", correlationId);
        operation.Activity?.SetStatus(ActivityStatusCode.Ok);
        _logger.LogInformation(
            "Upload accepted. OperationId={OperationId} FileCorrelation={FileCorrelation} Bytes={Bytes}",
            operation.Id,
            correlationId,
            bytes);
    }

    public void RecordRejected(UploadOperation operation, string reason)
    {
        var tags = new TagList
        {
            { TelemetryNames.OperationIdTag, operation.Id },
            { TelemetryNames.ReasonTag, SafeReason(reason) }
        };
        Rejected.Add(1, tags);
        operation.Activity?.SetTag(TelemetryNames.ReasonTag, SafeReason(reason));
        operation.Activity?.SetStatus(ActivityStatusCode.Error);
        _logger.LogInformation(
            "Upload rejected. OperationId={OperationId} Reason={Reason}",
            operation.Id,
            SafeReason(reason));
    }

    public void RecordRateLimited(string reason)
    {
        var safeReason = SafeReason(reason);
        RateLimited.Add(1, new KeyValuePair<string, object?>(TelemetryNames.ReasonTag, safeReason));
        if (safeReason == "disabled")
        {
            KillSwitch.Add(1);
        }

        _logger.LogWarning("Upload admission rejected. Reason={Reason}", safeReason);
    }

    public void RecordFailure(UploadOperation operation, string stage)
    {
        Failures.Add(
            1,
            new TagList
            {
                { TelemetryNames.OperationIdTag, operation.Id },
                { TelemetryNames.OperationTag, SafeStage(stage) }
            });
        operation.Activity?.SetStatus(ActivityStatusCode.Error);
        _logger.LogWarning(
            "Upload operation failed. OperationId={OperationId} Stage={Stage}",
            operation.Id,
            SafeStage(stage));
    }

    public void RecordCleanupFailure(UploadOperation operation, string stage, Exception exception)
    {
        CleanupFailures.Add(
            1,
            new TagList
            {
                { TelemetryNames.OperationIdTag, operation.Id },
                { TelemetryNames.OperationTag, SafeStage(stage) }
            });
        _logger.LogError(
            "Upload cleanup failed. OperationId={OperationId} Stage={Stage} ExceptionType={ExceptionType}",
            operation.Id,
            SafeStage(stage),
            exception.GetType().Name);
    }

    private static TagList Tags(string operationId, string correlationId) =>
        new()
        {
            { TelemetryNames.OperationIdTag, operationId },
            { "secure_upload.file_correlation", correlationId }
        };

    private static string SafeReason(string reason) =>
        reason is "disabled" or "concurrency" or "request-budget" or "byte-budget" or
            "defender-cap" or "one-file-required" or "empty-file" or "file-too-large" or
            "extension-not-allowed" or "media-type-not-allowed" or "multipart-required" or
            "invalid-boundary" or "status-create-failed" or "status-finalize-failed" or
            "upload-cancelled" or "upload-failed"
            ? reason
            : "other";

    private static string SafeStage(string stage) =>
        stage is "status-create" or "stream" or "status-finalize" or "pending-delete" or "status-failure"
            ? stage
            : "other";
}

public sealed class UploadOperation(string id, Activity? activity) : IDisposable
{
    public string Id { get; } = id;
    public Activity? Activity { get; } = activity;

    public void Dispose() => Activity?.Dispose();
}
