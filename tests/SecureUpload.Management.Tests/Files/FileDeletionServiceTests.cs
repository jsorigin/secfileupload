using Azure;
using Microsoft.Extensions.Logging.Abstractions;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Management.Files;
using SecureUpload.Management.Telemetry;

namespace SecureUpload.Management.Tests.Files;

public sealed class FileDeletionServiceTests
{
    private static readonly DateTimeOffset RequestTime = new(2026, 8, 14, 18, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(FileState.Uploading)]
    [InlineData(FileState.Pending)]
    [InlineData(FileState.Promoting)]
    [InlineData(FileState.Quarantining)]
    [InlineData(FileState.Available)]
    [InlineData(FileState.Rejected)]
    [InlineData(FileState.ScanError)]
    [InlineData(FileState.UploadFailed)]
    public async Task RequestAsync_TransitionsEveryLifecycleStateToDeleting(FileState state)
    {
        var current = ManagementFileTestData.WithStoreETag(
            ManagementFileTestData.CreateRecord(state, $"{state}.pdf", 10),
            "\"table-1\"");
        var store = new MutableStatusStore(current);
        var service = CreateService(store);

        var result = await service.RequestAsync(current.StableId, ManagementWebApplicationFactory.ObjectId);

        Assert.Equal(FileDeletionDisposition.Requested, result.Disposition);
        Assert.NotNull(result.Record);
        Assert.Equal(FileState.Deleting, result.Record!.State);
        Assert.Equal(ManagementWebApplicationFactory.ObjectId, result.Record.DeletedBy);
        Assert.Equal(RequestTime, result.Record.DeletionRequestedAt);
        Assert.Equal("\"table-1\"", store.ExpectedEtags.Single().ToString());
        Assert.DoesNotContain(ETag.All.ToString(), store.ExpectedEtags.Select(etag => etag.ToString()));
    }

    [Fact]
    public async Task RequestAsync_RefreshesAfterAConcurrencyConflictAndRetriesWithoutWildcardUpdates()
    {
        var pending = ManagementFileTestData.WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Pending, "race.pdf", 11),
            "\"table-1\"");
        var available = ManagementFileTestData.WithStoreETag(
            FileStateMachine.Transition(
                pending,
                FileTransition.Clean("event-clean", "corr-clean", pending.SourceETag!, pending.UpdatedAt.AddMinutes(1))).Record,
            "\"table-2\"");
        available = ManagementFileTestData.WithStoreETag(
            FileStateMachine.Transition(
                available,
                FileTransition.PromotionCompleted("\"clean-v1\"", available.UpdatedAt.AddMinutes(1))).Record,
            "\"table-3\"");

        var store = new MutableStatusStore(pending);
        store.BeforeUpdate = _ =>
        {
            store.Overwrite(available);
            store.BeforeUpdate = null;
        };
        var service = CreateService(store);

        var result = await service.RequestAsync(pending.StableId, ManagementWebApplicationFactory.ObjectId);

        Assert.Equal(FileDeletionDisposition.Requested, result.Disposition);
        Assert.NotNull(result.Record);
        Assert.Equal(FileState.Deleting, result.Record!.State);
        Assert.Equal(["\"table-1\"", "\"table-3\""], store.ExpectedEtags.Select(etag => etag.ToString()).ToArray());
        Assert.DoesNotContain(ETag.All.ToString(), store.ExpectedEtags.Select(etag => etag.ToString()));
    }

    [Fact]
    public async Task RequestAsync_RepeatedAndConcurrentRequestsPreserveTheFirstActorAndTimestamp()
    {
        var deleting = ManagementFileTestData.WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Deleting, "delete-me.pdf", 12),
            "\"table-2\"");
        var deleted = ManagementFileTestData.WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Deleted, "delete-me.pdf", 13),
            "\"table-3\"");
        var deletingService = CreateService(new MutableStatusStore(deleting));
        var deletedService = CreateService(new MutableStatusStore(deleted));

        var inProgress = await deletingService.RequestAsync(
            deleting.StableId,
            "33333333-3333-3333-3333-333333333333");
        var completed = await deletedService.RequestAsync(
            deleted.StableId,
            "44444444-4444-4444-4444-444444444444");

        Assert.Equal(FileDeletionDisposition.AlreadyDeleting, inProgress.Disposition);
        Assert.Equal(deleting.DeletedBy, inProgress.Record?.DeletedBy);
        Assert.Equal(deleting.DeletionRequestedAt, inProgress.Record?.DeletionRequestedAt);
        Assert.Equal(FileDeletionDisposition.AlreadyDeleted, completed.Disposition);
        Assert.Equal(deleted.DeletedBy, completed.Record?.DeletedBy);
        Assert.Equal(deleted.DeletionRequestedAt, completed.Record?.DeletionRequestedAt);
        Assert.Equal(deleted.DeletedAt, completed.Record?.DeletedAt);
    }

    [Fact]
    public async Task RequestAsync_BoundedConflictsReturnAStorageErrorWithoutChangingTheWinner()
    {
        var current = ManagementFileTestData.WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Available, "report.pdf", 14),
            "\"table-1\"");
        var store = new MutableStatusStore(current)
        {
            ConflictsRemaining = 10
        };
        var service = new FileDeletionService(
            store,
            new FixedTimeProvider(RequestTime),
            new ManagementTelemetry(NullLogger<ManagementTelemetry>.Instance),
            maximumConcurrencyAttempts: 3);

        var result = await service.RequestAsync(current.StableId, ManagementWebApplicationFactory.ObjectId);

        Assert.Equal(FileDeletionDisposition.StorageError, result.Disposition);
        Assert.Equal(FileState.Available, store.Record!.State);
        Assert.Equal(3, store.ExpectedEtags.Count);
    }

    [Fact]
    public async Task RequestAsync_InvalidAndMissingIdsFailClosed()
    {
        var service = CreateService(new MutableStatusStore(null));

        var invalid = await service.RequestAsync("not-a-stable-id", ManagementWebApplicationFactory.ObjectId);
        var missing = await service.RequestAsync(
            ManagementFileTestData.CreateStableId(404),
            ManagementWebApplicationFactory.ObjectId);

        Assert.Equal(FileDeletionDisposition.InvalidId, invalid.Disposition);
        Assert.Equal(FileDeletionDisposition.NotFound, missing.Disposition);
    }

    private static FileDeletionService CreateService(MutableStatusStore store) =>
        new(
            store,
            new FixedTimeProvider(RequestTime),
            new ManagementTelemetry(NullLogger<ManagementTelemetry>.Instance),
            maximumConcurrencyAttempts: 5);

    private sealed class MutableStatusStore(FileRecord? record) : IFileStatusStore
    {
        private int _version = 3;

        public FileRecord? Record { get; private set; } = record;
        public Action<FileRecord>? BeforeUpdate { get; set; }
        public int ConflictsRemaining { get; set; }
        public List<ETag> ExpectedEtags { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<StatusWriteResult> CreateAsync(
            FileRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileRecord?> GetAsync(
            string stableId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Record);

        public Task<StatusWriteResult> UpdateAsync(
            FileRecord record,
            ETag expectedETag,
            CancellationToken cancellationToken = default)
        {
            ExpectedEtags.Add(expectedETag);
            BeforeUpdate?.Invoke(record);

            if (ConflictsRemaining > 0)
            {
                ConflictsRemaining--;
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
            }

            if (Record is null)
            {
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.NotFound));
            }

            if (Record.StoreETag != expectedETag)
            {
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
            }

            var stored = ManagementFileTestData.WithStoreETag(
                record,
                $"\"table-{Interlocked.Increment(ref _version)}\"");
            Record = stored;
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.Succeeded, stored));
        }

        public void Overwrite(FileRecord record) => Record = record;

        public async IAsyncEnumerable<FileRecord> QueryAsync(
            FileStatusQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Record is not null)
            {
                yield return Record;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
