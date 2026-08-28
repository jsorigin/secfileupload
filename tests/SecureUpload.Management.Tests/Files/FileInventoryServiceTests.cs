using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Core.Telemetry;
using SecureUpload.Management.Files;
using SecureUpload.Management.Telemetry;

namespace SecureUpload.Management.Tests.Files;

public sealed class FileInventoryServiceTests
{
    private const string DeletedBy = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoadAsync_MapsStatesAndSortsNewestFirstWithStableTieOrdering()
    {
        var newer = CreateRecord(FileState.Available, "QuarterlyReport.PDF", BaseTime.AddMinutes(5), 2);
        var tieA = CreateRecord(FileState.Rejected, "Alert.txt", BaseTime, 1);
        var tieB = CreateRecord(FileState.Pending, "Pending.txt", BaseTime, 3);
        var store = new FakeStatusStore([tieB, newer, tieA]);
        var service = CreateService(store);

        var result = await service.LoadAsync(null, null, null, null);

        Assert.Equal(InventoryLoadState.Ready, result.State);
        Assert.Equal([newer.StableId, tieA.StableId, tieB.StableId], result.Files.Select(file => file.StableId));
        Assert.Equal("Available", result.Files[0].StatusLabel);
        Assert.Equal("Clean", result.Files[0].ScanResultLabel);
        Assert.Equal("Clean storage", result.Files[0].DestinationLabel);
        Assert.Equal("Rejected", result.Files[1].StatusLabel);
        Assert.Equal("Malicious", result.Files[1].ScanResultLabel);
        Assert.Equal("Quarantine storage", result.Files[1].DestinationLabel);
        Assert.Equal("Pending scan", result.Files[2].StatusLabel);
        Assert.Equal("Awaiting malware scan", result.Files[2].ScanResultLabel);
    }

    [Fact]
    public async Task LoadAsync_NormalizesSearchFilterPageAndPageSize()
    {
        var matching = CreateRecord(FileState.Available, "C:\\unsafe\\Quarterly\x0001.pdf", BaseTime.AddMinutes(2), 1);
        var otherMatch = CreateRecord(FileState.Available, "quarterly-summary.pdf", BaseTime.AddMinutes(1), 2);
        var nonMatch = CreateRecord(FileState.Pending, "not-matching.pdf", BaseTime, 3);
        var service = CreateService(new FakeStatusStore([matching, otherMatch, nonMatch]));

        var result = await service.LoadAsync(
            "  C:\\unsafe\\Quarterly\x0001.pdf  ",
            "invalid-filter",
            0,
            500);

        Assert.Equal(InventoryLoadState.Ready, result.State);
        Assert.Equal("Quarterly.pdf", result.Query.Search);
        Assert.Equal("all", result.Query.Filter);
        Assert.Equal(1, result.Query.PageNumber);
        Assert.Equal(100, result.Query.PageSize);
        Assert.Single(result.Files);
        Assert.Equal("Quarterly.pdf", result.Files[0].OriginalFileName);
    }

    [Fact]
    public async Task LoadAsync_DistinguishesEmptyAndNoMatchStates()
    {
        var emptyService = CreateService(new FakeStatusStore([]));
        var noMatchService = CreateService(
            new FakeStatusStore([CreateRecord(FileState.Available, "report.pdf", BaseTime, 1)]));

        var empty = await emptyService.LoadAsync(null, null, null, null);
        var noMatch = await noMatchService.LoadAsync("missing", "available", null, null);

        Assert.Equal(InventoryLoadState.Empty, empty.State);
        Assert.Equal(InventoryLoadState.NoMatch, noMatch.State);
        Assert.Empty(noMatch.Files);
        Assert.Equal(1, noMatch.SnapshotCount);
    }

    [Fact]
    public async Task LoadAsync_AcceptsCapacityAndRejectsCapacityPlusOneWithoutPartialRows()
    {
        var measurements = new ConcurrentBag<string>();
        using var listener = Listen(measurements);
        var logger = new CapturingLogger<ManagementTelemetry>();
        var underLimit = CreateService(new FakeStatusStore(CreateRows(10_000)), capacity: 10_000, logger: logger);
        var overLimitStore = new FakeStatusStore(CreateRows(10_001));
        var overLimit = CreateService(overLimitStore, capacity: 10_000, logger: logger);

        var accepted = await underLimit.LoadAsync(null, null, null, null);
        var rejected = await overLimit.LoadAsync(null, null, null, null);
        var exact = await overLimit.GetFileAsync(CreateStableId(4));

        Assert.Equal(InventoryLoadState.Ready, accepted.State);
        Assert.Equal(InventoryLoadState.CapacityExceeded, rejected.State);
        Assert.Empty(rejected.Files);
        Assert.Contains(TelemetryNames.ManagementInventoryCapacityExceeded, measurements);
        Assert.True(exact.Found);
        Assert.Equal(1, overLimitStore.QueryCalls);
        Assert.Equal(1, overLimitStore.GetCalls);
    }

    [Fact]
    public async Task LoadAsync_StorageFailureIsRecoverableAndDoesNotReuseStaleRows()
    {
        var measurements = new ConcurrentBag<string>();
        using var listener = Listen(measurements);
        var logger = new CapturingLogger<ManagementTelemetry>();
        var stableId = CreateStableId(7);
        var store = new FakeStatusStore([CreateRecord(FileState.Available, "report.pdf", BaseTime, 7)]);
        var service = CreateService(store, logger: logger);

        var first = await service.LoadAsync(null, null, null, null);
        store.QueryException = new TimeoutException($"report.pdf {stableId}");
        var second = await service.LoadAsync("report.pdf", "available", null, null);

        Assert.Equal(InventoryLoadState.Ready, first.State);
        Assert.Equal(InventoryLoadState.StorageError, second.State);
        Assert.Empty(second.Files);
        Assert.Contains(TelemetryNames.ManagementInventoryStorageFailure, measurements);
        Assert.DoesNotContain("report.pdf", logger.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(stableId, logger.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(TimeoutException), logger.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFileAsync_UsesPointLookupAndHandlesInvalidAndMissingIds()
    {
        var record = CreateRecord(FileState.Deleting, "pending-delete.pdf", BaseTime, 11);
        var store = new FakeStatusStore([record]) { ThrowIfQueryCalled = true };
        var service = CreateService(store);

        var found = await service.GetFileAsync(record.StableId.ToUpperInvariant());
        var missing = await service.GetFileAsync(CreateStableId(999));
        var invalid = await service.GetFileAsync("not-a-stable-id");

        Assert.Equal(FileLookupState.Found, found.State);
        Assert.Equal("Deleting", found.File?.StatusLabel);
        Assert.Equal("Removal in progress", found.File?.DestinationLabel);
        Assert.Equal(FileLookupState.NotFound, missing.State);
        Assert.Equal(FileLookupState.InvalidId, invalid.State);
        Assert.Equal(0, store.QueryCalls);
        Assert.Equal(2, store.GetCalls);
    }

    [Fact]
    public async Task LoadAsync_NormalizesPageToLastAvailablePage()
    {
        var service = CreateService(
            new FakeStatusStore(
            [
                CreateRecord(FileState.Available, "one.pdf", BaseTime.AddMinutes(3), 1),
                CreateRecord(FileState.Available, "two.pdf", BaseTime.AddMinutes(2), 2),
                CreateRecord(FileState.Available, "three.pdf", BaseTime.AddMinutes(1), 3)
            ]));

        var result = await service.LoadAsync(null, "available", 9, 2);

        Assert.Equal(2, result.Query.PageNumber);
        Assert.Equal(2, result.TotalPages);
        Assert.Single(result.Files);
        Assert.Equal("three.pdf", result.Files[0].OriginalFileName);
    }

    private static FileInventoryService CreateService(
        FakeStatusStore store,
        int capacity = 10_000,
        CapturingLogger<ManagementTelemetry>? logger = null)
    {
        var options = Options.Create(new FileInventoryOptions
        {
            Capacity = capacity,
            DefaultPageSize = 25,
            MaximumPageSize = 100,
            MaximumSearchLength = 255
        });
        var telemetry = new ManagementTelemetry(logger ?? new CapturingLogger<ManagementTelemetry>());
        return new FileInventoryService(store, options, telemetry);
    }

    private static IEnumerable<FileRecord> CreateRows(int count)
    {
        for (var index = 1; index <= count; index++)
        {
            yield return CreateRecord(FileState.Available, $"file-{index}.pdf", BaseTime.AddMinutes(index % 60), index);
        }
    }

    private static FileRecord CreateRecord(
        FileState state,
        string fileName,
        DateTimeOffset createdAt,
        int seed)
    {
        var stableId = CreateStableId(seed);
        var uploading = FileRecord.CreateUploading(fileName, "application/pdf", createdAt, stableId);

        return state switch
        {
            FileState.Uploading => uploading,
            FileState.Pending => FileStateMachine.Transition(
                uploading,
                FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
            FileState.Promoting => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    uploading,
                    FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
                FileTransition.Clean("event-clean", "correlation-clean", "\"source-v1\"", createdAt.AddMinutes(2))).Record,
            FileState.Quarantining => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    uploading,
                    FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
                FileTransition.Malicious("event-mal", "correlation-mal", "\"source-v1\"", createdAt.AddMinutes(2))).Record,
            FileState.Available => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    FileStateMachine.Transition(
                        uploading,
                        FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
                    FileTransition.Clean("event-clean", "correlation-clean", "\"source-v1\"", createdAt.AddMinutes(2))).Record,
                FileTransition.PromotionCompleted("\"clean-v1\"", createdAt.AddMinutes(3))).Record,
            FileState.Rejected => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    FileStateMachine.Transition(
                        uploading,
                        FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
                    FileTransition.Malicious("event-mal", "correlation-mal", "\"source-v1\"", createdAt.AddMinutes(2))).Record,
                FileTransition.QuarantineCompleted("\"quarantine-v1\"", createdAt.AddMinutes(3))).Record,
            FileState.ScanError => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    uploading,
                    FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
                FileTransition.ScanFailed(
                    "event-error",
                    "correlation-error",
                    "\"source-v1\"",
                    "scan-error",
                    createdAt.AddMinutes(2))).Record,
            FileState.UploadFailed => FileStateMachine.Transition(
                uploading,
                FileTransition.UploadFailed("upload-failed", createdAt.AddMinutes(1))).Record,
            FileState.Deleting => FileStateMachine.Transition(
                CreateRecord(FileState.Available, fileName, createdAt, seed + 10_000),
                FileTransition.DeleteRequested(DeletedBy, createdAt.AddMinutes(4))).Record,
            FileState.Deleted => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    CreateRecord(FileState.Available, fileName, createdAt, seed + 20_000),
                    FileTransition.DeleteRequested(DeletedBy, createdAt.AddMinutes(4))).Record,
                FileTransition.DeleteCompleted(createdAt.AddMinutes(5))).Record,
            _ => throw new InvalidOperationException("Unsupported test file state.")
        };
    }

    private static string CreateStableId(int seed) =>
        seed.ToString("x").PadLeft(64, '0');

    private static MeterListener Listen(ConcurrentBag<string> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TelemetryNames.Meter)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => measurements.Add(instrument.Name));
        listener.Start();
        return listener;
    }

    private sealed class FakeStatusStore(IEnumerable<FileRecord> records) : IFileStatusStore
    {
        private readonly Dictionary<string, FileRecord> _records =
            records.ToDictionary(record => record.StableId, record => record, StringComparer.Ordinal);

        public Exception? QueryException { get; set; }
        public bool ThrowIfQueryCalled { get; set; }
        public int QueryCalls { get; private set; }
        public int GetCalls { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<StatusWriteResult> CreateAsync(FileRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileRecord?> GetAsync(string stableId, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(_records.GetValueOrDefault(stableId));
        }

        public Task<StatusWriteResult> UpdateAsync(
            FileRecord record,
            ETag expectedETag,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<FileRecord> QueryAsync(
            FileStatusQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            if (ThrowIfQueryCalled)
            {
                throw new InvalidOperationException("QueryAsync should not be called for point lookups.");
            }

            if (QueryException is not null)
            {
                throw QueryException;
            }

            foreach (var record in _records.Values)
            {
                if (query.State is { } state && record.State != state)
                {
                    continue;
                }

                yield return record;
                await Task.Yield();
            }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];
        public string Text => string.Join(Environment.NewLine, _messages);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _messages.Add(formatter(state, exception));
    }
}
