using System.Reflection;
using Azure;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Scanning;

namespace SecureUpload.Processor.Tests.Scanning;

public sealed class StalePendingTests
{
    private const string StableId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Younger_pending_record_remains_pending()
    {
        var store = new WatchdogStore(PendingRecord(Now.AddHours(-2)));
        var watchdog = CreateWatchdog(store);

        var result = await watchdog.DetectAsync(Now);

        Assert.Equal(0, result.MarkedScanError);
        Assert.Equal(FileState.Pending, store.Record.State);
        Assert.Equal(0, store.UpdateCalls);
    }

    [Fact]
    public async Task Older_pending_record_becomes_scan_error_once()
    {
        var store = new WatchdogStore(PendingRecord(Now.AddHours(-4)));
        var watchdog = CreateWatchdog(store);

        var first = await watchdog.DetectAsync(Now);
        var second = await watchdog.DetectAsync(Now.AddMinutes(1));

        Assert.Equal(1, first.MarkedScanError);
        Assert.Equal(0, second.MarkedScanError);
        Assert.Equal(FileState.ScanError, store.Record.State);
        Assert.Equal("scan-watchdog-expired", store.Record.FailureCode);
        Assert.Equal(1, store.UpdateCalls);
        Assert.Equal(0, store.GetCalls);
    }

    [Fact]
    public async Task Watchdog_concurrency_is_bounded_and_retryable()
    {
        var store = new WatchdogStore(PendingRecord(Now.AddHours(-4)))
        {
            ConflictsRemaining = 10
        };

        await Assert.ThrowsAsync<RetryableScanProcessingException>(
            () => CreateWatchdog(store, maximumAttempts: 2).DetectAsync(Now));

        Assert.Equal(2, store.UpdateCalls);
        Assert.Equal(2, store.GetCalls);
        Assert.Equal(FileState.Pending, store.Record.State);
    }

    private static StalePendingWatchdog CreateWatchdog(
        WatchdogStore store,
        int maximumAttempts = 5) =>
        new(
            store,
            new ScanProcessorOptions
            {
                ExpectedTopic = "topic",
                BlobServiceUri = new Uri("https://secureuploads.blob.core.windows.net"),
                ScanWatchdogThreshold = TimeSpan.FromHours(3),
                MaximumConcurrencyAttempts = maximumAttempts
            });

    private static FileRecord PendingRecord(DateTimeOffset updatedAt)
    {
        var uploading = FileRecord.CreateUploading(
            "report.pdf",
            "application/pdf",
            updatedAt.AddMinutes(-1),
            StableId);
        var pending = FileStateMachine.Transition(
            uploading,
            FileTransition.UploadCompleted("\"source\"", 10, updatedAt)).Record;
        typeof(FileRecord).GetProperty(nameof(FileRecord.StoreETag))!
            .SetValue(pending, new ETag("\"table-1\""));
        return pending;
    }

    private sealed class WatchdogStore(FileRecord record) : IFileStatusStore
    {
        public FileRecord Record { get; private set; } = record;
        public int ConflictsRemaining
        {
            init => _conflictsRemaining = value;
        }
        public int UpdateCalls { get; private set; }
        public int GetCalls { get; private set; }
        private int _conflictsRemaining;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<StatusWriteResult> CreateAsync(
            FileRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileRecord?> GetAsync(
            string stableId,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult<FileRecord?>(Record);
        }

        public Task<StatusWriteResult> UpdateAsync(
            FileRecord record,
            ETag expectedETag,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            if (_conflictsRemaining-- > 0)
            {
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
            }

            typeof(FileRecord).GetProperty(nameof(FileRecord.StoreETag))!
                .SetValue(record, new ETag($"\"table-{UpdateCalls + 1}\""));
            Record = record;
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.Succeeded, record));
        }

        public async IAsyncEnumerable<FileRecord> QueryAsync(
            FileStatusQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (Record.State == query.State &&
                (query.UpdatedBefore is null || Record.UpdatedAt < query.UpdatedBefore))
            {
                yield return Record;
            }
        }
    }
}
