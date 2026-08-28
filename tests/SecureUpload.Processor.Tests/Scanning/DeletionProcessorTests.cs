using System.Reflection;
using Azure;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Scanning;

namespace SecureUpload.Processor.Tests.Scanning;

public sealed class DeletionProcessorTests
{
    private const string StableId = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string DeletedBy = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RequestedAt = CreatedAt.AddMinutes(5);
    private static readonly DateTimeOffset DeletedAt = RequestedAt.AddMinutes(1);

    [Fact]
    public async Task Timer_sweep_finalizes_deleting_records_once_every_blob_copy_is_gone()
    {
        var store = new DeletionStore(DeletingRecord());
        var blobs = new CleanupBlobStore();
        blobs.Seed(BlobArea.Pending, "\"pending-v1\"");
        blobs.Seed(BlobArea.Clean, "\"clean-v1\"");
        var processor = CreateProcessor(store, blobs);

        var result = await processor.ProcessPendingAsync(DeletedAt);

        Assert.Equal(1, result.Finalized);
        Assert.Equal(0, result.AlreadyDeleted);
        Assert.Equal(0, result.Incomplete);
        Assert.Equal(FileState.Deleted, store.Record.State);
        Assert.Equal(DeletedAt, store.Record.DeletedAt);
        Assert.All(Enum.GetValues<BlobArea>(), area => Assert.Null(blobs.Get(area)));
    }

    [Fact]
    public async Task Exhausted_cleanup_leaves_the_record_deleting_for_a_future_retry()
    {
        var store = new DeletionStore(DeletingRecord());
        var blobs = new CleanupBlobStore();
        blobs.Seed(BlobArea.Clean, "\"clean-v1\"");
        blobs.ScheduleDeleteMismatch(
            BlobArea.Clean,
            "\"clean-v2\"",
            "\"clean-v3\"",
            "\"clean-v4\"");
        var processor = CreateProcessor(store, blobs, maximumAttempts: 3);

        var result = await processor.ProcessPendingAsync(DeletedAt);

        Assert.Equal(0, result.Finalized);
        Assert.Equal(0, result.AlreadyDeleted);
        Assert.Equal(1, result.Incomplete);
        Assert.Equal(FileState.Deleting, store.Record.State);
        Assert.NotNull(await blobs.GetPropertiesAsync(StableId, BlobArea.Clean));
    }

    [Fact]
    public async Task Concurrent_completion_refreshes_to_the_existing_deleted_tombstone()
    {
        var store = new DeletionStore(DeletingRecord());
        var blobs = new CleanupBlobStore();
        blobs.Seed(BlobArea.Pending, "\"pending-v1\"");
        store.BeforeUpdate = record =>
        {
            if (record.State != FileState.Deleted)
            {
                return;
            }

            store.Overwrite(
                FileStateMachine.Transition(
                    store.Record,
                    FileTransition.DeleteCompleted(DeletedAt)).Record,
                "\"table-race\"");
            store.BeforeUpdate = null;
        };
        var processor = CreateProcessor(store, blobs);

        var result = await processor.ProcessPendingAsync(DeletedAt);

        Assert.Equal(0, result.Finalized);
        Assert.Equal(1, result.AlreadyDeleted);
        Assert.Equal(0, result.Incomplete);
        Assert.Equal(FileState.Deleted, store.Record.State);
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
    }

    private static DeletionProcessor CreateProcessor(
        DeletionStore store,
        CleanupBlobStore blobs,
        int maximumAttempts = 5) =>
        new(
            store,
            new FileDeletionCleanup(blobs, maximumAttempts),
            new ScanProcessorOptions
            {
                ExpectedTopic = "topic",
                BlobServiceUri = new Uri("https://secureuploads.blob.core.windows.net"),
                MaximumConcurrencyAttempts = maximumAttempts
            });

    private static FileRecord AvailableRecord()
    {
        var uploading = FileRecord.CreateUploading(
            "report.pdf",
            "application/pdf",
            CreatedAt,
            StableId);
        var pending = FileStateMachine.Transition(
            uploading,
            FileTransition.UploadCompleted("\"source-v1\"", 42, CreatedAt.AddMinutes(1))).Record;
        var promoting = FileStateMachine.Transition(
            pending,
            FileTransition.Clean("event-1", "correlation-1", "\"source-v1\"", CreatedAt.AddMinutes(2))).Record;
        var available = FileStateMachine.Transition(
            promoting,
            FileTransition.PromotionCompleted("\"clean-v1\"", CreatedAt.AddMinutes(3))).Record;
        return WithStoreETag(available, "\"table-available\"");
    }

    private static FileRecord DeletingRecord() =>
        WithStoreETag(
            FileStateMachine.Transition(
                AvailableRecord(),
                FileTransition.DeleteRequested(DeletedBy, RequestedAt)).Record,
            "\"table-deleting\"");

    private static FileRecord WithStoreETag(FileRecord record, string eTag)
    {
        typeof(FileRecord).GetProperty(nameof(FileRecord.StoreETag), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(record, new ETag(eTag));
        return record;
    }

    private sealed class DeletionStore(FileRecord record) : IFileStatusStore
    {
        public FileRecord Record { get; private set; } = record;
        public Action<FileRecord>? BeforeUpdate { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<StatusWriteResult> CreateAsync(
            FileRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileRecord?> GetAsync(
            string stableId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FileRecord?>(Record);

        public Task<StatusWriteResult> UpdateAsync(
            FileRecord record,
            ETag expectedETag,
            CancellationToken cancellationToken = default)
        {
            BeforeUpdate?.Invoke(record);
            if (Record.StoreETag != expectedETag)
            {
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
            }

            Record = WithStoreETag(record, "\"table-updated\"");
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.Succeeded, Record));
        }

        public void Overwrite(FileRecord record, string eTag) =>
            Record = WithStoreETag(record, eTag);

        public async IAsyncEnumerable<FileRecord> QueryAsync(
            FileStatusQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if ((query.State is null || query.State == Record.State) &&
                (query.UpdatedBefore is null || Record.UpdatedAt < query.UpdatedBefore))
            {
                yield return Record;
            }
        }
    }

    private sealed class CleanupBlobStore : IBlobFileStore
    {
        private readonly Dictionary<BlobArea, BlobWriteResult> _blobs = [];
        private readonly Dictionary<BlobArea, Queue<string>> _deleteMismatches = [];

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
