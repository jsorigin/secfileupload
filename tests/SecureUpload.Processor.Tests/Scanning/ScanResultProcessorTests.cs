using System.Reflection;
using Azure;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Scanning;

namespace SecureUpload.Processor.Tests.Scanning;

public sealed class ScanResultProcessorTests
{
    private const string StableId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceETag = "\"source-v1\"";
    private const string DeletedBy = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 13, 0, 0, TimeSpan.Zero);
    private static readonly Uri PendingUri =
        new($"https://secureuploads.blob.core.windows.net/pending/{StableId}");

    [Theory]
    [InlineData(MalwareScanOutcome.Clean, FileState.Available, BlobArea.Clean)]
    [InlineData(MalwareScanOutcome.Malicious, FileState.Rejected, BlobArea.Quarantine)]
    public async Task Matching_result_moves_exact_source_and_commits_terminal_state(
        MalwareScanOutcome outcome,
        FileState terminalState,
        BlobArea targetArea)
    {
        var status = new FakeStatusStore(PendingRecord());
        var blobs = new FakeBlobStore(SourceETag);
        var processor = CreateProcessor(status, blobs);

        var result = await processor.ProcessAsync(Event(outcome));

        Assert.Equal(ScanProcessingDisposition.Completed, result.Disposition);
        Assert.Equal(terminalState, status.Record.State);
        Assert.Equal(1, blobs.CopyCalls);
        Assert.Equal(targetArea, blobs.LastCopyDestination);
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
        Assert.NotNull(await blobs.GetPropertiesAsync(StableId, targetArea));
    }

    [Fact]
    public async Task Scan_arriving_during_upload_finalization_is_reconciled()
    {
        var status = new FakeStatusStore(UploadingRecord());
        var blobs = new FakeBlobStore(SourceETag);

        var result = await CreateProcessor(status, blobs).ProcessAsync(Event(MalwareScanOutcome.Clean));

        Assert.Equal(ScanProcessingDisposition.Completed, result.Disposition);
        Assert.Equal(FileState.Available, status.Record.State);
    }

    [Fact]
    public async Task Duplicate_terminal_delivery_confirms_target_and_cleans_stranded_source_without_copying()
    {
        var available = MakeTerminal(FileState.Available);
        var status = new FakeStatusStore(available);
        var blobs = new FakeBlobStore(SourceETag);
        blobs.Seed(BlobArea.Clean, "\"clean-v1\"");

        var result = await CreateProcessor(status, blobs).ProcessAsync(Event(MalwareScanOutcome.Clean));

        Assert.Equal(ScanProcessingDisposition.Duplicate, result.Disposition);
        Assert.Equal(0, blobs.CopyCalls);
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
    }

    [Fact]
    public async Task Conflicting_terminal_result_is_acknowledged_as_an_operational_conflict()
    {
        var status = new FakeStatusStore(MakeTerminal(FileState.Available));
        var blobs = new FakeBlobStore(SourceETag);
        blobs.Seed(BlobArea.Clean, "\"clean-v1\"");

        var result = await CreateProcessor(status, blobs).ProcessAsync(Event(MalwareScanOutcome.Malicious));

        Assert.Equal(ScanProcessingDisposition.OperationalConflict, result.Disposition);
        Assert.Equal(FileState.Available, status.Record.State);
        Assert.Equal(0, blobs.CopyCalls);
    }

    [Fact]
    public async Task Conflicting_result_cannot_reverse_in_progress_clean_promotion()
    {
        var status = new FakeStatusStore(PromotingRecord(targetETag: null));
        var blobs = new FakeBlobStore(SourceETag);

        var result = await CreateProcessor(status, blobs).ProcessAsync(
            Event(MalwareScanOutcome.Malicious));

        Assert.Equal(ScanProcessingDisposition.OperationalConflict, result.Disposition);
        Assert.Equal(FileState.Promoting, status.Record.State);
        Assert.Equal(0, blobs.CopyCalls);
    }

    [Fact]
    public async Task Stale_source_etag_never_copies_or_changes_state()
    {
        var status = new FakeStatusStore(PendingRecord());
        var blobs = new FakeBlobStore(SourceETag);

        var result = await CreateProcessor(status, blobs).ProcessAsync(
            Event(MalwareScanOutcome.Clean) with { SourceETag = new ETag("\"source-v0\"") });

        Assert.Equal(ScanProcessingDisposition.PermanentRejection, result.Disposition);
        Assert.Equal(FileState.Pending, status.Record.State);
        Assert.Equal(0, blobs.CopyCalls);
    }

    [Fact]
    public async Task Not_scanned_and_unknown_results_fail_closed_without_blob_movement()
    {
        var status = new FakeStatusStore(PendingRecord());
        var blobs = new FakeBlobStore(SourceETag);

        var result = await CreateProcessor(status, blobs).ProcessAsync(
            Event(MalwareScanOutcome.ScanError) with { FailureCode = "sam-259206" });

        Assert.Equal(ScanProcessingDisposition.ScanErrorRecorded, result.Disposition);
        Assert.Equal(FileState.ScanError, status.Record.State);
        Assert.Equal("sam-259206", status.Record.FailureCode);
        Assert.Equal(0, blobs.CopyCalls);
        Assert.NotNull(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
    }

    [Fact]
    public async Task Uncertain_result_during_promotion_removes_clean_target_before_scan_error()
    {
        var status = new FakeStatusStore(PromotingRecord("\"clean-v1\""));
        var blobs = new FakeBlobStore(SourceETag);
        blobs.Seed(BlobArea.Clean, "\"clean-v1\"");

        var result = await CreateProcessor(status, blobs).ProcessAsync(
            Event(MalwareScanOutcome.ScanError) with { FailureCode = "sam-259207" });

        Assert.Equal(ScanProcessingDisposition.ScanErrorRecorded, result.Disposition);
        Assert.Equal(FileState.ScanError, status.Record.State);
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Clean));
        Assert.NotNull(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
    }

    [Fact]
    public async Task Scan_error_during_quarantine_removes_target_before_recording_scan_error()
    {
        var status = new FakeStatusStore(QuarantiningRecord("\"quarantine-v1\""));
        var blobs = new FakeBlobStore(SourceETag);
        blobs.Seed(BlobArea.Quarantine, "\"quarantine-v1\"");

        var result = await CreateProcessor(status, blobs).ProcessAsync(
            Event(MalwareScanOutcome.ScanError) with { FailureCode = "sam-259208" });

        Assert.Equal(ScanProcessingDisposition.ScanErrorRecorded, result.Disposition);
        Assert.Equal(FileState.ScanError, status.Record.State);
        Assert.Equal("sam-259208", status.Record.FailureCode);
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Quarantine));
        Assert.NotNull(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
    }

    [Fact]
    public async Task Service_delayed_result_remains_pending_until_the_watchdog()
    {
        var status = new FakeStatusStore(PendingRecord());
        var blobs = new FakeBlobStore(SourceETag);

        var result = await CreateProcessor(status, blobs).ProcessAsync(
            Event(MalwareScanOutcome.Delayed));

        Assert.Equal(ScanProcessingDisposition.Deferred, result.Disposition);
        Assert.Equal(FileState.Pending, status.Record.State);
        Assert.Equal(0, status.UpdateCalls);
        Assert.Equal(0, blobs.CopyCalls);
    }

    [Fact]
    public async Task Retry_after_copy_status_failure_removes_untracked_target_and_repeats_safe_copy()
    {
        var status = new FakeStatusStore(PromotingRecord(targetETag: null));
        status.ConcurrencyConflictsRemaining = 1;
        var blobs = new FakeBlobStore(SourceETag);
        blobs.Seed(BlobArea.Clean, "\"stranded-target\"");

        var result = await CreateProcessor(status, blobs).ProcessAsync(Event(MalwareScanOutcome.Clean));

        Assert.Equal(ScanProcessingDisposition.Completed, result.Disposition);
        Assert.Equal(2, blobs.CopyCalls);
        Assert.Contains(BlobArea.Clean, blobs.DeletedAreas);
        Assert.Equal(FileState.Available, status.Record.State);
    }

    [Fact]
    public async Task Retry_after_recorded_copy_resumes_at_conditional_source_delete()
    {
        var status = new FakeStatusStore(PromotingRecord("\"clean-v1\""));
        var blobs = new FakeBlobStore(SourceETag);
        blobs.Seed(BlobArea.Clean, "\"clean-v1\"");

        var result = await CreateProcessor(status, blobs).ProcessAsync(Event(MalwareScanOutcome.Clean));

        Assert.Equal(ScanProcessingDisposition.Completed, result.Disposition);
        Assert.Equal(0, blobs.CopyCalls);
        Assert.Equal(FileState.Available, status.Record.State);
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
    }

    [Fact]
    public async Task Delete_winning_after_target_copy_cleans_the_orphan_target_and_finishes_the_tombstone()
    {
        var status = new FakeStatusStore(PendingRecord());
        var blobs = new FakeBlobStore(SourceETag);
        status.BeforeUpdate = record =>
        {
            if (record.State != FileState.Promoting || record.TargetETag != "\"clean-v1\"")
            {
                return;
            }

            var deleting = FileStateMachine.Transition(
                status.Record,
                FileTransition.DeleteRequested(DeletedBy, record.UpdatedAt.AddSeconds(1))).Record;
            status.Overwrite(deleting, "\"table-delete\"");
            status.BeforeUpdate = null;
        };

        var result = await CreateProcessor(status, blobs).ProcessAsync(Event(MalwareScanOutcome.Clean));

        Assert.Equal(ScanProcessingDisposition.Duplicate, result.Disposition);
        Assert.Equal(FileState.Deleted, status.Record.State);
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Clean));
    }

    [Fact]
    public async Task Deleted_tombstones_absorb_delayed_events_and_clean_any_orphaned_blobs()
    {
        var status = new FakeStatusStore(DeletedRecord());
        var deletedAt = status.Record.DeletedAt;
        var requestedAt = status.Record.DeletionRequestedAt;
        var blobs = new FakeBlobStore(SourceETag);
        blobs.Seed(BlobArea.Pending, SourceETag);
        blobs.Seed(BlobArea.Clean, "\"orphan-clean\"");

        var result = await CreateProcessor(status, blobs).ProcessAsync(Event(MalwareScanOutcome.Clean));

        Assert.Equal(ScanProcessingDisposition.Duplicate, result.Disposition);
        Assert.Equal(FileState.Deleted, status.Record.State);
        Assert.Equal(requestedAt, status.Record.DeletionRequestedAt);
        Assert.Equal(deletedAt, status.Record.DeletedAt);
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Clean));
    }

    [Fact]
    public async Task Deleting_records_only_acknowledge_scan_events_after_cleanup_is_complete()
    {
        var status = new FakeStatusStore(DeletingRecord());
        var blobs = new FakeBlobStore(SourceETag);
        blobs.Seed(BlobArea.Clean, "\"clean-v1\"");
        blobs.ScheduleDeleteMismatch(
            BlobArea.Clean,
            "\"clean-v2\"",
            "\"clean-v3\"",
            "\"clean-v4\"");

        var exception = await Assert.ThrowsAsync<RetryableScanProcessingException>(
            () => CreateProcessor(status, blobs, maximumAttempts: 3).ProcessAsync(Event(MalwareScanOutcome.Clean)));

        Assert.Equal("Deletion cleanup incomplete.", exception.Message);
        Assert.Equal(FileState.Deleting, status.Record.State);
        Assert.NotNull(await blobs.GetPropertiesAsync(StableId, BlobArea.Clean));
    }

    [Fact]
    public async Task Transient_storage_failure_is_rethrown_for_event_grid_retry()
    {
        var status = new FakeStatusStore(PendingRecord());
        var blobs = new FakeBlobStore(SourceETag) { ThrowTransientOnCopy = true };

        var exception = await Assert.ThrowsAsync<RetryableScanProcessingException>(
            () => CreateProcessor(status, blobs).ProcessAsync(Event(MalwareScanOutcome.Clean)));

        Assert.Equal("Transient storage failure.", exception.Message);
        Assert.DoesNotContain(StableId, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(FileState.Promoting, status.Record.State);
        Assert.NotNull(await blobs.GetPropertiesAsync(StableId, BlobArea.Pending));
    }

    [Fact]
    public async Task Source_change_during_copy_cleanup_removes_target_and_fails_closed()
    {
        var status = new FakeStatusStore(PendingRecord());
        var blobs = new FakeBlobStore(SourceETag) { ReplaceSourceAfterCopy = true };

        var result = await CreateProcessor(status, blobs).ProcessAsync(Event(MalwareScanOutcome.Clean));

        Assert.Equal(ScanProcessingDisposition.ScanErrorRecorded, result.Disposition);
        Assert.Equal(FileState.ScanError, status.Record.State);
        Assert.Equal("blob-state-invalid", status.Record.FailureCode);
        Assert.Null(await blobs.GetPropertiesAsync(StableId, BlobArea.Clean));
    }

    [Fact]
    public async Task Concurrency_retries_are_bounded_then_rethrown_for_delivery_retry()
    {
        var status = new FakeStatusStore(PendingRecord())
        {
            ConcurrencyConflictsRemaining = 10
        };
        var blobs = new FakeBlobStore(SourceETag);

        await Assert.ThrowsAsync<RetryableScanProcessingException>(
            () => CreateProcessor(status, blobs, maximumAttempts: 3)
                .ProcessAsync(Event(MalwareScanOutcome.Clean)));

        Assert.Equal(3, status.UpdateCalls);
        Assert.Equal(0, blobs.CopyCalls);
    }

    private static ScanResultProcessor CreateProcessor(
        FakeStatusStore status,
        FakeBlobStore blobs,
        int maximumAttempts = 5) =>
        CreateProcessor(
            status,
            blobs,
            new ScanProcessorOptions
            {
                ExpectedTopic = "topic",
                BlobServiceUri = new Uri("https://secureuploads.blob.core.windows.net"),
                MaximumConcurrencyAttempts = maximumAttempts
            });

    private static ScanResultProcessor CreateProcessor(
        FakeStatusStore status,
        FakeBlobStore blobs,
        ScanProcessorOptions options) =>
        new(
            status,
            new BlobPromotionService(blobs),
            new DeletionProcessor(
                status,
                new FileDeletionCleanup(blobs, options.MaximumConcurrencyAttempts),
                options),
            options);

    private static MalwareScanEvent Event(MalwareScanOutcome outcome) =>
        new(
            "event-1",
            "correlation-1",
            StableId,
            PendingUri,
            new ETag(SourceETag),
            Now,
            outcome,
            outcome == MalwareScanOutcome.ScanError ? "scan-error" : null);

    private static FileRecord UploadingRecord() =>
        WithStoreETag(
            FileRecord.CreateUploading("report.pdf", "application/pdf", Now.AddHours(-1), StableId),
            "\"table-1\"");

    private static FileRecord PendingRecord()
    {
        var result = FileStateMachine.Transition(
            UploadingRecord(),
            FileTransition.UploadCompleted(SourceETag, 42, Now.AddMinutes(-30), PendingUri));
        return WithStoreETag(result.Record, "\"table-1\"");
    }

    private static FileRecord PromotingRecord(string? targetETag)
    {
        var processing = FileStateMachine.Transition(
            PendingRecord(),
            FileTransition.Clean("event-1", "correlation-1", SourceETag, Now.AddMinutes(-10))).Record;
        if (targetETag is null)
        {
            return WithStoreETag(processing, "\"table-2\"");
        }

        return WithStoreETag(
            FileStateMachine.Transition(
                processing,
                FileTransition.TargetCopyRecorded(targetETag, Now.AddMinutes(-9))).Record,
            "\"table-3\"");
    }

    private static FileRecord DeletingRecord() =>
        WithStoreETag(
            FileStateMachine.Transition(
                MakeTerminal(FileState.Available),
                FileTransition.DeleteRequested(DeletedBy, Now.AddMinutes(-8))).Record,
            "\"table-5\"");

    private static FileRecord DeletedRecord() =>
        WithStoreETag(
            FileStateMachine.Transition(
                DeletingRecord(),
                FileTransition.DeleteCompleted(Now.AddMinutes(-7))).Record,
            "\"table-6\"");

    private static FileRecord QuarantiningRecord(string? targetETag)
    {
        var processing = FileStateMachine.Transition(
            PendingRecord(),
            FileTransition.Malicious("event-1", "correlation-1", SourceETag, Now.AddMinutes(-10))).Record;
        if (targetETag is null)
        {
            return WithStoreETag(processing, "\"table-2\"");
        }

        return WithStoreETag(
            FileStateMachine.Transition(
                processing,
                FileTransition.TargetCopyRecorded(targetETag, Now.AddMinutes(-9))).Record,
            "\"table-3\"");
    }

    private static FileRecord MakeTerminal(FileState state)
    {
        var processing = state == FileState.Available
            ? FileStateMachine.Transition(
                PendingRecord(),
                FileTransition.Clean("event-1", "correlation-1", SourceETag, Now.AddMinutes(-10))).Record
            : FileStateMachine.Transition(
                PendingRecord(),
                FileTransition.Malicious("event-1", "correlation-1", SourceETag, Now.AddMinutes(-10))).Record;
        var terminal = FileStateMachine.Transition(
            processing,
            state == FileState.Available
                ? FileTransition.PromotionCompleted("\"clean-v1\"", Now.AddMinutes(-9))
                : FileTransition.QuarantineCompleted("\"quarantine-v1\"", Now.AddMinutes(-9))).Record;
        return WithStoreETag(terminal, "\"table-4\"");
    }

    private static FileRecord WithStoreETag(FileRecord record, string eTag)
    {
        typeof(FileRecord)
            .GetProperty(nameof(FileRecord.StoreETag), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(record, new ETag(eTag));
        return record;
    }

    private sealed class FakeStatusStore(FileRecord initial) : IFileStatusStore
    {
        public FileRecord Record { get; private set; } = initial;
        public Action<FileRecord>? BeforeUpdate { get; set; }
        public int ConcurrencyConflictsRemaining { get; set; }
        public int UpdateCalls { get; private set; }

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
            UpdateCalls++;
            BeforeUpdate?.Invoke(record);
            if (ConcurrencyConflictsRemaining > 0)
            {
                ConcurrencyConflictsRemaining--;
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
            }

            if (Record.StoreETag != expectedETag)
            {
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
            }

            Record = WithStoreETag(record, $"\"table-{UpdateCalls + 1}\"");
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

    private sealed class FakeBlobStore(string sourceETag) : IBlobFileStore
    {
        private readonly Dictionary<BlobArea, BlobWriteResult> _blobs = new()
        {
            [BlobArea.Pending] = new(PendingUri, new ETag(sourceETag), 42)
        };

        public int CopyCalls { get; private set; }
        public BlobArea? LastCopyDestination { get; private set; }
        public List<BlobArea> DeletedAreas { get; } = [];
        public bool ThrowTransientOnCopy { get; init; }
        public bool ReplaceSourceAfterCopy { get; init; }
        private readonly Dictionary<BlobArea, Queue<string>> _deleteMismatches = [];

        public void Seed(BlobArea area, string eTag) =>
            _blobs[area] = new(
                new Uri($"https://secureuploads.blob.core.windows.net/{area.ToString().ToLowerInvariant()}/{StableId}"),
                new ETag(eTag),
                42);

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
            CancellationToken cancellationToken = default)
        {
            CopyCalls++;
            LastCopyDestination = destination;
            if (ThrowTransientOnCopy)
            {
                throw new RequestFailedException(503, "sensitive provider message");
            }

            Assert.Equal(sourceETag, expectedSourceETag.ToString());
            var eTag = destination == BlobArea.Clean ? "\"clean-v1\"" : "\"quarantine-v1\"";
            Seed(destination, eTag);
            if (ReplaceSourceAfterCopy)
            {
                _blobs[BlobArea.Pending] = _blobs[BlobArea.Pending] with
                {
                    ETag = new ETag("\"changed-after-scan\"")
                };
            }

            return Task.FromResult(
                new BlobCopyResult(PendingUri, _blobs[destination].BlobUri, new ETag(eTag)));
        }

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
            DeletedAreas.Add(area);
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
