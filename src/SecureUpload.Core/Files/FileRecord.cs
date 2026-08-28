using System.Security.Cryptography;
using Azure;

namespace SecureUpload.Core.Files;

public sealed record FileRecord
{
    internal const int MaximumDeletedByLength = 128;
    internal static readonly DateTimeOffset MinimumTableTimestamp =
        new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string StableId { get; internal init; } = null!;
    public string OriginalFileName { get; internal init; } = null!;
    public string MediaType { get; internal init; } = null!;
    public FileState State { get; internal init; }
    public DateTimeOffset CreatedAt { get; internal init; }
    public DateTimeOffset UpdatedAt { get; internal init; }
    public long? SizeBytes { get; internal init; }
    public string? SourceETag { get; internal init; }
    public string? TargetETag { get; internal init; }
    public Uri? PendingBlobUri { get; internal init; }
    public DateTimeOffset? UploadedAt { get; internal init; }
    public DateTimeOffset? ProcessingStartedAt { get; internal init; }
    public DateTimeOffset? ScanCompletedAt { get; internal init; }
    public string? LastEventId { get; internal init; }
    public string? ScanCorrelationId { get; internal init; }
    public string? FailureCode { get; internal init; }
    public DateTimeOffset? DeletionRequestedAt { get; internal init; }
    public DateTimeOffset? DeletedAt { get; internal init; }
    public string? DeletedBy { get; internal init; }
    public ETag? StoreETag { get; internal init; }

    public static FileRecord CreateUploading(
        string originalFileName,
        string mediaType,
        DateTimeOffset createdAt,
        string? stableId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        var id = stableId ?? CreateStableId();
        ValidateStableId(id);

        return new FileRecord
        {
            StableId = id,
            OriginalFileName = originalFileName,
            MediaType = mediaType,
            State = FileState.Uploading,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
    }

    public static string CreateStableId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static void ValidateStableId(string stableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        if (stableId.Length != 64 || stableId.Any(character =>
                character is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
        {
            throw new ArgumentException("Stable IDs must be 64 lowercase hexadecimal characters.", nameof(stableId));
        }
    }

    internal static bool HasValidDeletionActor(string? deletedBy) =>
        !string.IsNullOrWhiteSpace(deletedBy) &&
        deletedBy.Length <= MaximumDeletedByLength &&
        deletedBy.All(character => !char.IsControl(character));

    internal static bool HasValidTableTimestamp(DateTimeOffset timestamp) =>
        timestamp >= MinimumTableTimestamp;

    internal void ValidateDeletionAudit()
    {
        if (DeletionRequestedAt is { } requestedAt && !HasValidTableTimestamp(requestedAt))
        {
            throw new ArgumentException(
                $"Deletion timestamps must be on or after {MinimumTableTimestamp:O}.",
                nameof(DeletionRequestedAt));
        }

        if (DeletedAt is { } deletedAt && !HasValidTableTimestamp(deletedAt))
        {
            throw new ArgumentException(
                $"Deletion timestamps must be on or after {MinimumTableTimestamp:O}.",
                nameof(DeletedAt));
        }

        if (DeletedBy is not null && !HasValidDeletionActor(DeletedBy))
        {
            throw new ArgumentException(
                $"DeletedBy must be non-empty, at most {MaximumDeletedByLength} characters, and free of control characters.",
                nameof(DeletedBy));
        }

        if (State is FileState.Deleting or FileState.Deleted)
        {
            if (DeletionRequestedAt is null)
            {
                throw new ArgumentException(
                    "Deleting and Deleted records require a deletion request timestamp.",
                    nameof(DeletionRequestedAt));
            }

            if (!HasValidDeletionActor(DeletedBy))
            {
                throw new ArgumentException(
                    "Deleting and Deleted records require a valid deleted-by actor.",
                    nameof(DeletedBy));
            }
        }
        else if (DeletionRequestedAt is not null || DeletedAt is not null || DeletedBy is not null)
        {
            throw new ArgumentException(
                "Deletion audit fields are only valid for Deleting and Deleted records.");
        }

        if (State == FileState.Deleting && DeletedAt is not null)
        {
            throw new ArgumentException(
                "Deleting records cannot include a completion timestamp.",
                nameof(DeletedAt));
        }

        if (State == FileState.Deleted && DeletedAt is null)
        {
            throw new ArgumentException(
                "Deleted records require a completion timestamp.",
                nameof(DeletedAt));
        }

        if (DeletedAt is not null && DeletionRequestedAt is null)
        {
            throw new ArgumentException(
                "DeletedAt cannot be set without DeletionRequestedAt.",
                nameof(DeletedAt));
        }

        if (DeletionRequestedAt is { } requestTime &&
            DeletedAt is { } completedAt &&
            completedAt < requestTime)
        {
            throw new ArgumentException(
                "DeletedAt cannot precede DeletionRequestedAt.",
                nameof(DeletedAt));
        }
    }
}
