using System.Globalization;
using SecureUpload.Core.Files;

namespace SecureUpload.Management.Files;

public sealed record ManagementFileView
{
    public required string StableId { get; init; }
    public required string OriginalFileName { get; init; }
    public required string MediaType { get; init; }
    public required FileState State { get; init; }
    public required string StatusKey { get; init; }
    public required string StatusLabel { get; init; }
    public required string ScanResultKey { get; init; }
    public required string ScanResultLabel { get; init; }
    public required string DestinationKey { get; init; }
    public required string DestinationLabel { get; init; }
    public required string StateSummary { get; init; }
    public required bool CanDownloadClean { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? UploadedAt { get; init; }
    public DateTimeOffset? ProcessingStartedAt { get; init; }
    public DateTimeOffset? ScanCompletedAt { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? DeletionRequestedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public string? DeletedBy { get; init; }

    public string CreatedAtDisplay => FormatTimestamp(CreatedAt);
    public string UpdatedAtDisplay => FormatTimestamp(UpdatedAt);
    public string UploadedAtDisplay => FormatTimestamp(UploadedAt);
    public string ProcessingStartedAtDisplay => FormatTimestamp(ProcessingStartedAt);
    public string ScanCompletedAtDisplay => FormatTimestamp(ScanCompletedAt);
    public string DeletionRequestedAtDisplay => FormatTimestamp(DeletionRequestedAt);
    public string DeletedAtDisplay => FormatTimestamp(DeletedAt);
    public string SizeDisplay => SizeBytes is { } sizeBytes ? $"{sizeBytes:N0} bytes" : "Unknown";
    public bool CanRequestDeletion => State is not (FileState.Deleting or FileState.Deleted);

    public static ManagementFileView FromRecord(FileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var presentation = record.State switch
        {
            FileState.Uploading => new FilePresentation(
                "uploading",
                "Uploading",
                "pending",
                "Upload in progress",
                "pending",
                "Pending storage",
                "The file is still uploading and is not available yet."),
            FileState.Pending => new FilePresentation(
                "pending",
                "Pending scan",
                "pending",
                "Awaiting malware scan",
                "pending",
                "Pending storage",
                "The file is waiting for malware scanning."),
            FileState.Promoting => new FilePresentation(
                "promoting",
                "Promoting clean copy",
                "clean",
                "Clean",
                "clean",
                "Copying to clean storage",
                "The clean result is recorded and the file is still being copied into clean storage."),
            FileState.Quarantining => new FilePresentation(
                "quarantining",
                "Quarantining copy",
                "malicious",
                "Malicious",
                "quarantine",
                "Copying to quarantine",
                "Malicious content was detected and the file is being copied into quarantine."),
            FileState.Available => new FilePresentation(
                "available",
                "Available",
                "clean",
                "Clean",
                "clean",
                "Clean storage",
                "The file passed scanning and is available in clean storage."),
            FileState.Rejected => new FilePresentation(
                "rejected",
                "Rejected",
                "malicious",
                "Malicious",
                "quarantine",
                "Quarantine storage",
                "Malicious content was detected and the file is isolated in quarantine."),
            FileState.ScanError => new FilePresentation(
                "scan-error",
                "Scan error",
                "scan-error",
                "Scan error",
                "none",
                "No clean or quarantine copy",
                "The scan did not complete successfully and the file is not available."),
            FileState.UploadFailed => new FilePresentation(
                "upload-failed",
                "Upload failed",
                "upload-failed",
                "Upload failed",
                "none",
                "Not stored",
                "The upload did not finish successfully."),
            FileState.Deleting => new FilePresentation(
                "deleting",
                "Deleting",
                "deleting",
                "Deletion requested",
                "deleting",
                "Removal in progress",
                "Deletion was requested and processor cleanup is still in progress."),
            FileState.Deleted => new FilePresentation(
                "deleted",
                "Deleted",
                "deleted",
                "Deleted",
                "deleted",
                "No active storage",
                "The file has been deleted and only the tombstone remains."),
            _ => throw new InvalidOperationException("Unsupported management file state.")
        };

        return new ManagementFileView
        {
            StableId = record.StableId,
            OriginalFileName = NormalizeFileName(record.OriginalFileName),
            MediaType = record.MediaType.Trim().ToLowerInvariant(),
            State = record.State,
            StatusKey = presentation.StatusKey,
            StatusLabel = presentation.StatusLabel,
            ScanResultKey = presentation.ScanResultKey,
            ScanResultLabel = presentation.ScanResultLabel,
            DestinationKey = presentation.DestinationKey,
            DestinationLabel = presentation.DestinationLabel,
            StateSummary = presentation.StateSummary,
            CanDownloadClean = record.State == FileState.Available && !string.IsNullOrWhiteSpace(record.TargetETag),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            UploadedAt = record.UploadedAt,
            ProcessingStartedAt = record.ProcessingStartedAt,
            ScanCompletedAt = record.ScanCompletedAt,
            SizeBytes = record.SizeBytes,
            DeletionRequestedAt = record.DeletionRequestedAt,
            DeletedAt = record.DeletedAt,
            DeletedBy = record.DeletedBy
        };
    }

    public static string NormalizeFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var leafName = Path.GetFileName(fileName.Replace('\0', '_')).Trim();
        var normalized = string.Concat(leafName.Where(character => !char.IsControl(character)));
        return normalized.Length <= 255 ? normalized : normalized[^255..];
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value is { } timestamp
            ? timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : "Not recorded";

    private sealed record FilePresentation(
        string StatusKey,
        string StatusLabel,
        string ScanResultKey,
        string ScanResultLabel,
        string DestinationKey,
        string DestinationLabel,
        string StateSummary);
}
