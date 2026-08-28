namespace SecureUpload.Core.Files;

public sealed class FilePolicyOptions
{
    public const long DefaultMaximumFileSizeBytes = 100L * 1024 * 1024;

    public long MaximumFileSizeBytes { get; init; } = DefaultMaximumFileSizeBytes;

    public IReadOnlySet<string> AllowedExtensions { get; init; } = new HashSet<string>(
        [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".jpg", ".jpeg", ".png", ".gif"],
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> AllowedMediaTypes { get; init; } = new HashSet<string>(
        [
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.ms-powerpoint",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "text/plain",
            "text/csv",
            "image/jpeg",
            "image/png",
            "image/gif"
        ],
        StringComparer.OrdinalIgnoreCase);

    public TimeSpan ScanWatchdogThreshold { get; init; } = TimeSpan.FromHours(3);

    public void Validate()
    {
        if (MaximumFileSizeBytes <= 0)
        {
            throw new InvalidOperationException("Maximum file size must be positive.");
        }

        if (AllowedExtensions.Count == 0 || AllowedExtensions.Any(extension =>
                string.IsNullOrWhiteSpace(extension) || !extension.StartsWith('.')))
        {
            throw new InvalidOperationException("Allowed extensions must contain normalized extension values.");
        }

        if (AllowedMediaTypes.Count == 0 || AllowedMediaTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("At least one non-empty media type is required.");
        }

        if (ScanWatchdogThreshold <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Scan watchdog threshold must be positive.");
        }
    }
}
