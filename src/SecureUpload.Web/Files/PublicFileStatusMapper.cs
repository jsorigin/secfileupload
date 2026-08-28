using System.Diagnostics.CodeAnalysis;
using SecureUpload.Core.Files;

namespace SecureUpload.Web.Files;

public sealed record PollingFileStatus(string FileId, string Status);

public sealed record HostFileStatus(
    string FileId,
    string Status,
    string FileName,
    string MediaType,
    long? SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? UploadedAt,
    DateTimeOffset? ScanCompletedAt);

public static class PublicFileStatusMapper
{
    public static bool IsPubliclyVisible([NotNullWhen(true)] FileRecord? record) =>
        record is not null &&
        record.State is not (FileState.UploadFailed or FileState.Deleting or FileState.Deleted);

    public static PollingFileStatus ForPolling(FileRecord record) =>
        new(record.StableId, ToStatus(record.State));

    public static HostFileStatus ForHost(FileRecord record) =>
        new(
            record.StableId,
            ToStatus(record.State),
            NormalizeFileName(record.OriginalFileName),
            record.MediaType.Trim().ToLowerInvariant(),
            record.SizeBytes,
            record.CreatedAt,
            record.UpdatedAt,
            record.UploadedAt,
            record.ScanCompletedAt);

    private static string ToStatus(FileState state) =>
        state.ToPublicState() switch
        {
            PublicFileState.Pending => "pending",
            PublicFileState.Available => "available",
            PublicFileState.Rejected => "rejected",
            PublicFileState.ScanError => "scan-error",
            _ => throw new InvalidOperationException("Unsupported public file status.")
        };

    private static string NormalizeFileName(string fileName)
    {
        var leafName = Path.GetFileName(fileName.Replace('\0', '_')).Trim();
        var normalized = string.Concat(leafName.Where(character => !char.IsControl(character)));
        return normalized.Length <= 255 ? normalized : normalized[^255..];
    }
}
