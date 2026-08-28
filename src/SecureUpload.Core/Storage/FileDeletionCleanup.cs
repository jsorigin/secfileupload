using SecureUpload.Core.Files;

namespace SecureUpload.Core.Storage;

public enum BlobAreaCleanupDisposition
{
    AlreadyAbsent,
    Deleted,
    Incomplete
}

public sealed record BlobAreaCleanupResult(
    BlobArea Area,
    BlobAreaCleanupDisposition Disposition,
    int Attempts);

public sealed record FileDeletionCleanupResult(IReadOnlyList<BlobAreaCleanupResult> Areas)
{
    public bool IsComplete =>
        Areas.All(area => area.Disposition is not BlobAreaCleanupDisposition.Incomplete);
}

public sealed class FileDeletionCleanup
{
    private readonly IBlobFileStore _blobs;
    private readonly int _maximumAttemptsPerArea;

    public FileDeletionCleanup(
        IBlobFileStore blobs,
        int maximumAttemptsPerArea)
    {
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        if (maximumAttemptsPerArea is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAttemptsPerArea),
                "Cleanup attempts per area must be between 1 and 20.");
        }

        _maximumAttemptsPerArea = maximumAttemptsPerArea;
    }

    public async Task<FileDeletionCleanupResult> CleanupAsync(
        string stableId,
        CancellationToken cancellationToken = default)
    {
        FileRecord.ValidateStableId(stableId);

        var results = new List<BlobAreaCleanupResult>();
        foreach (var area in Enum.GetValues<BlobArea>())
        {
            results.Add(await CleanupAreaAsync(stableId, area, cancellationToken));
        }

        return new(results);
    }

    private async Task<BlobAreaCleanupResult> CleanupAreaAsync(
        string stableId,
        BlobArea area,
        CancellationToken cancellationToken)
    {
        var deleted = false;
        var attempts = 0;
        for (var attempt = 1; attempt <= _maximumAttemptsPerArea; attempt++)
        {
            var properties = await _blobs.GetPropertiesAsync(stableId, area, cancellationToken);
            if (properties is null)
            {
                return new(
                    area,
                    deleted
                        ? BlobAreaCleanupDisposition.Deleted
                        : BlobAreaCleanupDisposition.AlreadyAbsent,
                    attempts);
            }

            attempts++;
            var disposition = await _blobs.DeleteIfMatchAsync(
                stableId,
                area,
                properties.ETag,
                cancellationToken);

            switch (disposition)
            {
                case ConditionalBlobDeleteDisposition.Deleted:
                    deleted = true;
                    if (await _blobs.GetPropertiesAsync(stableId, area, cancellationToken) is null)
                    {
                        return new(area, BlobAreaCleanupDisposition.Deleted, attempts);
                    }

                    continue;
                case ConditionalBlobDeleteDisposition.NotFound:
                    if (await _blobs.GetPropertiesAsync(stableId, area, cancellationToken) is null)
                    {
                        return new(
                            area,
                            deleted
                                ? BlobAreaCleanupDisposition.Deleted
                                : BlobAreaCleanupDisposition.AlreadyAbsent,
                            attempts);
                    }

                    continue;
                case ConditionalBlobDeleteDisposition.ETagMismatch:
                    continue;
                default:
                    throw new InvalidOperationException($"Unexpected blob cleanup result: {disposition}.");
            }
        }

        return new(area, BlobAreaCleanupDisposition.Incomplete, attempts);
    }
}
