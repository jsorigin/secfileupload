using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SecureUpload.Core.Telemetry;

public static class TelemetryNames
{
    public const string ServiceNamespace = "SecureUpload";
    public const string ActivitySource = "SecureUpload";
    public const string Meter = "SecureUpload";

    public const string UploadAccepted = "secure_upload.upload.accepted";
    public const string UploadRejected = "secure_upload.upload.rejected";
    public const string UploadBytes = "secure_upload.upload.bytes";
    public const string UploadRateLimited = "secure_upload.upload.rate_limited";
    public const string UploadFailure = "secure_upload.upload.failure";
    public const string UploadCleanupFailure = "secure_upload.upload.cleanup_failure";
    public const string UploadKillSwitch = "secure_upload.upload.kill_switch";

    public const string ScanOutcome = "secure_upload.scan.outcome";
    public const string ScanLatency = "secure_upload.scan.latency";
    public const string InvalidEvent = "secure_upload.scan.invalid_event";
    public const string ProcessingRetry = "secure_upload.scan.processing_retry";
    public const string StalePending = "secure_upload.scan.stale_pending";
    public const string OldestPendingAge = "secure_upload.scan.oldest_pending_age";
    public const string BlobOperationFailure = "secure_upload.scan.blob_operation_failure";
    public const string TerminalConflict = "secure_upload.scan.terminal_conflict";
    public const string DeletionCleanupRetry = "secure_upload.scan.deletion_cleanup_retry";
    public const string DeletionCleanupFailure = "secure_upload.scan.deletion_cleanup_failure";
    public const string ManagementInventoryCapacityExceeded =
        "secure_upload.management.inventory_capacity_exceeded";
    public const string ManagementInventoryStorageFailure =
        "secure_upload.management.inventory_storage_failure";
    public const string ManagementDownloadIntegrityFailure =
        "secure_upload.management.download_integrity_failure";
    public const string ManagementActionStorageFailure =
        "secure_upload.management.action_storage_failure";

    public const string OperationIdTag = "secure_upload.operation_id";
    public const string OutcomeTag = "secure_upload.outcome";
    public const string ReasonTag = "secure_upload.reason";
    public const string OperationTag = "secure_upload.operation";
    public const string BlobAreaTag = "secure_upload.blob_area";
}

public sealed class TelemetryCorrelation
{
    private readonly byte[] _key;

    public TelemetryCorrelation(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length < 32)
        {
            throw new ArgumentException("The telemetry correlation key must be at least 32 characters.", nameof(key));
        }

        _key = Encoding.UTF8.GetBytes(key);
    }

    public string ForStableId(string stableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        var hash = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(stableId));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    public static string CreateOperationId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

public static partial class TelemetryPathRedactor
{
    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return StableIdRegex().Replace(value, "{fileId}");
    }

    public static void RedactHttpDependency(Activity activity, HttpRequestMessage request)
    {
        if (request.RequestUri is not { } uri)
        {
            return;
        }

        var redacted = Redact(uri.AbsoluteUri);
        activity.SetTag("url.full", redacted);
        activity.SetTag("http.url", redacted);
    }

    [GeneratedRegex(@"(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdRegex();
}
