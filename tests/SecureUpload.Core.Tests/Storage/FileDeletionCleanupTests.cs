using Azure;
using SecureUpload.Core.Storage;

namespace SecureUpload.Core.Tests.Storage;

public sealed class FileDeletionCleanupTests
{
    private const string StableId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(BlobArea.Pending)]
    [InlineData(BlobArea.Clean)]
    [InlineData(BlobArea.Quarantine)]
    public async Task Existing_single_area_copies_are_removed_idempotently(BlobArea area)
    {
        var blobs = new FakeBlobStore();
        blobs.Seed(area, "\"blob-v1\"");
        var cleanup = new FileDeletionCleanup(blobs, maximumAttemptsPerArea: 3);

        var result = await cleanup.CleanupAsync(StableId);

        Assert.True(result.IsComplete);
        Assert.Null(await blobs.GetPropertiesAsync(StableId, area));
        Assert.Equal(
            BlobAreaCleanupDisposition.Deleted,
            result.Areas.Single(outcome => outcome.Area == area).Disposition);
    }

    [Fact]
    public async Task Missing_blobs_across_every_area_still_complete_successfully()
    {
        var blobs = new FakeBlobStore();
        var cleanup = new FileDeletionCleanup(blobs, maximumAttemptsPerArea: 3);

        var result = await cleanup.CleanupAsync(StableId);

        Assert.True(result.IsComplete);
        Assert.All(
            result.Areas,
            area =>
            {
                Assert.Equal(BlobAreaCleanupDisposition.AlreadyAbsent, area.Disposition);
                Assert.Equal(0, area.Attempts);
            });
    }

    [Fact]
    public async Task Partial_promotions_remove_every_remaining_copy_before_reporting_complete()
    {
        var blobs = new FakeBlobStore();
        blobs.Seed(BlobArea.Pending, "\"pending-v1\"");
        blobs.Seed(BlobArea.Clean, "\"clean-v1\"");
        blobs.Seed(BlobArea.Quarantine, "\"quarantine-v1\"");
        var cleanup = new FileDeletionCleanup(blobs, maximumAttemptsPerArea: 3);

        var result = await cleanup.CleanupAsync(StableId);

        Assert.True(result.IsComplete);
        Assert.All(Enum.GetValues<BlobArea>(), area => Assert.Null(blobs.Get(area)));
        Assert.Equal(
            3,
            result.Areas.Count(area => area.Disposition == BlobAreaCleanupDisposition.Deleted));
    }

    [Fact]
    public async Task Etag_mismatches_refresh_and_retry_with_concrete_etags()
    {
        var blobs = new FakeBlobStore();
        blobs.Seed(BlobArea.Clean, "\"clean-v1\"");
        blobs.ScheduleDeleteMismatch(BlobArea.Clean, "\"clean-v2\"");
        var cleanup = new FileDeletionCleanup(blobs, maximumAttemptsPerArea: 3);

        var result = await cleanup.CleanupAsync(StableId);
        var area = result.Areas.Single(outcome => outcome.Area == BlobArea.Clean);

        Assert.True(result.IsComplete);
        Assert.Equal(BlobAreaCleanupDisposition.Deleted, area.Disposition);
        Assert.Equal(2, area.Attempts);
        Assert.Equal(
            new[] { "\"clean-v1\"", "\"clean-v2\"" },
            blobs.DeleteExpectedEtags.Select(etag => etag.ToString()).ToArray());
        Assert.DoesNotContain(ETag.All.ToString(), blobs.DeleteExpectedEtags.Select(etag => etag.ToString()));
    }

    [Fact]
    public async Task Exhausted_mismatches_report_incomplete_and_leave_the_blob_for_retry()
    {
        var blobs = new FakeBlobStore();
        blobs.Seed(BlobArea.Pending, "\"pending-v1\"");
        blobs.ScheduleDeleteMismatch(
            BlobArea.Pending,
            "\"pending-v2\"",
            "\"pending-v3\"",
            "\"pending-v4\"");
        var cleanup = new FileDeletionCleanup(blobs, maximumAttemptsPerArea: 3);

        var result = await cleanup.CleanupAsync(StableId);
        var area = result.Areas.Single(outcome => outcome.Area == BlobArea.Pending);

        Assert.False(result.IsComplete);
        Assert.Equal(BlobAreaCleanupDisposition.Incomplete, area.Disposition);
        Assert.Equal(3, area.Attempts);
        Assert.NotNull(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
        Assert.DoesNotContain(ETag.All.ToString(), blobs.DeleteExpectedEtags.Select(etag => etag.ToString()));
    }

    private sealed class FakeBlobStore : IBlobFileStore
    {
        private readonly Dictionary<BlobArea, BlobWriteResult> _blobs = [];
        private readonly Dictionary<BlobArea, Queue<string>> _deleteMismatches = [];

        public List<ETag> DeleteExpectedEtags { get; } = [];

        public void Seed(BlobArea area, string etag) =>
            _blobs[area] = new(
                new Uri($"https://storage.test/{area.ToString().ToLowerInvariant()}/{StableId}"),
                new ETag(etag),
                42);

        public BlobWriteResult? Get(BlobArea area) => _blobs.GetValueOrDefault(area);

        public void ScheduleDeleteMismatch(BlobArea area, params string[] replacementETags) =>
            _deleteMismatches[area] = new Queue<string>(replacementETags);

        public Task<BlobWriteResult> UploadPendingAsync(
            string stableId,
            Stream content,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BlobCopyResult> CopyPendingAsync(
            string stableId,
            BlobArea destination,
            ETag expectedSourceETag,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConditionalBlobDeleteDisposition> DeleteIfMatchAsync(
            string stableId,
            BlobArea area,
            ETag expectedETag,
            CancellationToken cancellationToken = default)
        {
            DeleteExpectedEtags.Add(expectedETag);
            if (!_blobs.TryGetValue(area, out var blob))
            {
                return Task.FromResult(ConditionalBlobDeleteDisposition.NotFound);
            }

            if (_deleteMismatches.TryGetValue(area, out var mismatches) && mismatches.Count > 0)
            {
                blob = blob with { ETag = new ETag(mismatches.Dequeue()) };
                _blobs[area] = blob;
            }

            if (blob.ETag != expectedETag)
            {
                return Task.FromResult(ConditionalBlobDeleteDisposition.ETagMismatch);
            }

            _blobs.Remove(area);
            return Task.FromResult(ConditionalBlobDeleteDisposition.Deleted);
        }

        public Task<ConditionalBlobReadResult> OpenCleanReadIfMatchAsync(
            string stableId,
            ETag expectedETag,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BlobWriteResult?> GetPropertiesAsync(
            string stableId,
            BlobArea area,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.GetValueOrDefault(area));
    }
}
