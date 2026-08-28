using System.Reflection;
using SecureUpload.Core.Files;

namespace SecureUpload.Core.Tests.Files;

public sealed class FileStateMachineTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private const string SourceETag = "\"source-v1\"";
    private const string DeletedBy = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void Stable_ids_are_cryptographically_random_and_storage_safe()
    {
        var ids = Enumerable.Range(0, 256).Select(_ => FileRecord.CreateStableId()).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-f0-9]{64}$", id));
    }

    [Fact]
    public void Uploading_advances_to_pending_then_clean_processing_and_available()
    {
        var uploading = NewUploading();
        var pendingAt = CreatedAt.AddMinutes(1);
        var processingAt = CreatedAt.AddMinutes(2);
        var availableAt = CreatedAt.AddMinutes(3);

        var pending = FileStateMachine.Transition(
            uploading,
            FileTransition.UploadCompleted(SourceETag, 42, pendingAt));
        var promoting = FileStateMachine.Transition(
            pending.Record,
            FileTransition.Clean("event-1", "correlation-1", SourceETag, processingAt));
        var available = FileStateMachine.Transition(
            promoting.Record,
            FileTransition.PromotionCompleted("\"clean-v1\"", availableAt));

        Assert.Equal(TransitionDisposition.Applied, pending.Disposition);
        Assert.Equal(FileState.Pending, pending.Record.State);
        Assert.Equal(pendingAt, pending.Record.UploadedAt);
        Assert.Equal(SourceETag, pending.Record.SourceETag);
        Assert.Equal(42, pending.Record.SizeBytes);
        Assert.Equal(FileState.Promoting, promoting.Record.State);
        Assert.Equal(FileState.Available, available.Record.State);
        Assert.Equal(availableAt, available.Record.ScanCompletedAt);
        Assert.Equal("\"clean-v1\"", available.Record.TargetETag);
    }

    [Fact]
    public void Uploading_advances_to_pending_then_malicious_processing_and_rejected()
    {
        var pending = FileStateMachine.Transition(
            NewUploading(),
            FileTransition.UploadCompleted(SourceETag, 42, CreatedAt.AddMinutes(1))).Record;

        var quarantining = FileStateMachine.Transition(
            pending,
            FileTransition.Malicious("event-2", "correlation-2", SourceETag, CreatedAt.AddMinutes(2)));
        var rejected = FileStateMachine.Transition(
            quarantining.Record,
            FileTransition.QuarantineCompleted("\"quarantine-v1\"", CreatedAt.AddMinutes(3)));

        Assert.Equal(FileState.Quarantining, quarantining.Record.State);
        Assert.Equal(FileState.Rejected, rejected.Record.State);
        Assert.Equal(CreatedAt.AddMinutes(3), rejected.Record.ScanCompletedAt);
    }

    [Fact]
    public void Duplicate_event_is_idempotent_and_preserves_terminal_data()
    {
        var available = MakeAvailable("event-1", "correlation-1");

        var duplicate = FileStateMachine.Transition(
            available,
            FileTransition.Clean("event-1", "correlation-1", SourceETag, CreatedAt.AddHours(1)));

        Assert.Equal(TransitionDisposition.Idempotent, duplicate.Disposition);
        Assert.Same(available, duplicate.Record);
        Assert.Equal(CreatedAt.AddMinutes(3), duplicate.Record.ScanCompletedAt);
        Assert.Equal("\"clean-v1\"", duplicate.Record.TargetETag);
    }

    [Fact]
    public void Scan_racing_upload_finalization_preserves_newer_processing_state()
    {
        var promoting = FileStateMachine.Transition(
            NewUploading(),
            FileTransition.Clean("event-1", "correlation-1", SourceETag, CreatedAt.AddMinutes(2)));

        var lateFinalization = FileStateMachine.Transition(
            promoting.Record,
            FileTransition.UploadCompleted(SourceETag, 42, CreatedAt.AddMinutes(1)));

        Assert.Equal(TransitionDisposition.Reconciled, lateFinalization.Disposition);
        Assert.Same(promoting.Record, lateFinalization.Record);
        Assert.Equal(FileState.Promoting, lateFinalization.Record.State);
    }

    [Fact]
    public void Older_blob_etag_is_rejected_even_when_identity_matches()
    {
        var pending = FileStateMachine.Transition(
            NewUploading(),
            FileTransition.UploadCompleted(SourceETag, 42, CreatedAt.AddMinutes(1))).Record;

        var stale = FileStateMachine.Transition(
            pending,
            FileTransition.Clean("event-old", "correlation-old", "\"source-v0\"", CreatedAt.AddMinutes(2)));

        Assert.Equal(TransitionDisposition.Rejected, stale.Disposition);
        Assert.Same(pending, stale.Record);
        Assert.Equal(TransitionRejection.SourceETagMismatch, stale.Rejection);
    }

    [Fact]
    public void Conflicting_terminal_result_is_rejected_without_regression()
    {
        var available = MakeAvailable("event-clean", "correlation-clean");

        var conflict = FileStateMachine.Transition(
            available,
            FileTransition.Malicious("event-malicious", "correlation-malicious", SourceETag, CreatedAt.AddHours(1)));

        Assert.Equal(TransitionDisposition.Rejected, conflict.Disposition);
        Assert.Equal(TransitionRejection.TerminalConflict, conflict.Rejection);
        Assert.Same(available, conflict.Record);
        Assert.Equal(FileState.Available, conflict.Record.State);
    }

    [Fact]
    public void Reused_event_identity_cannot_hide_a_conflicting_terminal_result()
    {
        var available = MakeAvailable("event-reused", "correlation-reused");

        var conflict = FileStateMachine.Transition(
            available,
            FileTransition.Malicious(
                "event-reused",
                "correlation-reused",
                SourceETag,
                CreatedAt.AddHours(1)));

        Assert.Equal(TransitionDisposition.Rejected, conflict.Disposition);
        Assert.Equal(TransitionRejection.TerminalConflict, conflict.Rejection);
        Assert.Same(available, conflict.Record);
    }

    [Fact]
    public void Scan_error_can_only_recover_through_a_valid_scanned_etag()
    {
        var pending = FileStateMachine.Transition(
            NewUploading(),
            FileTransition.UploadCompleted(SourceETag, 42, CreatedAt.AddMinutes(1))).Record;
        var scanError = FileStateMachine.Transition(
            pending,
            FileTransition.ScanFailed("event-error", "correlation-error", SourceETag, "scan-timeout", CreatedAt.AddHours(3))).Record;

        var recovery = FileStateMachine.Transition(
            scanError,
            FileTransition.Clean("event-clean", "correlation-clean", SourceETag, CreatedAt.AddHours(4)));

        Assert.Equal(FileState.ScanError, scanError.State);
        Assert.Equal("scan-timeout", scanError.FailureCode);
        Assert.Equal(TransitionDisposition.Applied, recovery.Disposition);
        Assert.Equal(FileState.Promoting, recovery.Record.State);
        Assert.Null(recovery.Record.FailureCode);
    }

    [Theory]
    [InlineData(FileState.Uploading)]
    [InlineData(FileState.Pending)]
    [InlineData(FileState.Promoting)]
    [InlineData(FileState.Quarantining)]
    [InlineData(FileState.Available)]
    [InlineData(FileState.Rejected)]
    [InlineData(FileState.ScanError)]
    [InlineData(FileState.UploadFailed)]
    public void Delete_request_moves_every_current_lifecycle_state_to_deleting(FileState currentState)
    {
        var record = CreateRecord(currentState);
        var requestedAt = record.UpdatedAt.AddMinutes(1);

        var transition = FileStateMachine.Transition(
            record,
            FileTransition.DeleteRequested(DeletedBy, requestedAt));

        Assert.Equal(TransitionDisposition.Applied, transition.Disposition);
        Assert.Equal(FileState.Deleting, transition.Record.State);
        Assert.Equal(DeletedBy, transition.Record.DeletedBy);
        Assert.Equal(requestedAt, transition.Record.DeletionRequestedAt);
        Assert.Null(transition.Record.DeletedAt);
        Assert.Equal(record.OriginalFileName, transition.Record.OriginalFileName);
        Assert.Equal(record.SizeBytes, transition.Record.SizeBytes);
        Assert.Equal(record.SourceETag, transition.Record.SourceETag);
        Assert.Equal(record.TargetETag, transition.Record.TargetETag);
        Assert.Equal(record.UploadedAt, transition.Record.UploadedAt);
        Assert.Equal(record.ProcessingStartedAt, transition.Record.ProcessingStartedAt);
        Assert.Equal(record.ScanCompletedAt, transition.Record.ScanCompletedAt);
        Assert.Equal(record.FailureCode, transition.Record.FailureCode);
    }

    [Fact]
    public void Delete_completion_moves_deleting_to_deleted_without_clearing_history()
    {
        var record = CreateRecord(FileState.Available);
        var requestedAt = record.UpdatedAt.AddMinutes(1);
        var deletedAt = requestedAt.AddMinutes(1);
        var deleting = FileStateMachine.Transition(
            record,
            FileTransition.DeleteRequested(DeletedBy, requestedAt));

        var transition = FileStateMachine.Transition(
            deleting.Record,
            FileTransition.DeleteCompleted(deletedAt));

        Assert.Equal(TransitionDisposition.Applied, transition.Disposition);
        Assert.Equal(FileState.Deleted, transition.Record.State);
        Assert.Equal(DeletedBy, transition.Record.DeletedBy);
        Assert.Equal(requestedAt, transition.Record.DeletionRequestedAt);
        Assert.Equal(deletedAt, transition.Record.DeletedAt);
        Assert.Equal(record.OriginalFileName, transition.Record.OriginalFileName);
        Assert.Equal(record.SizeBytes, transition.Record.SizeBytes);
        Assert.Equal(record.SourceETag, transition.Record.SourceETag);
        Assert.Equal(record.TargetETag, transition.Record.TargetETag);
        Assert.Equal(record.UploadedAt, transition.Record.UploadedAt);
        Assert.Equal(record.ProcessingStartedAt, transition.Record.ProcessingStartedAt);
        Assert.Equal(record.ScanCompletedAt, transition.Record.ScanCompletedAt);
    }

    [Fact]
    public void Repeated_delete_request_and_completion_are_idempotent()
    {
        var record = CreateRecord(FileState.Rejected);
        var requestedAt = record.UpdatedAt.AddMinutes(1);
        var deletedAt = requestedAt.AddMinutes(1);
        var deleting = FileStateMachine.Transition(
            record,
            FileTransition.DeleteRequested(DeletedBy, requestedAt)).Record;

        var repeatedRequest = FileStateMachine.Transition(
            deleting,
            FileTransition.DeleteRequested(
                "22222222-2222-2222-2222-222222222222",
                requestedAt.AddMinutes(1)));

        Assert.Equal(TransitionDisposition.Idempotent, repeatedRequest.Disposition);
        Assert.Same(deleting, repeatedRequest.Record);
        Assert.Equal(DeletedBy, repeatedRequest.Record.DeletedBy);
        Assert.Equal(requestedAt, repeatedRequest.Record.DeletionRequestedAt);

        var deleted = FileStateMachine.Transition(
            deleting,
            FileTransition.DeleteCompleted(deletedAt)).Record;

        var repeatedCompletion = FileStateMachine.Transition(
            deleted,
            FileTransition.DeleteCompleted(deletedAt.AddMinutes(1)));
        var repeatedAfterDeleted = FileStateMachine.Transition(
            deleted,
            FileTransition.DeleteRequested(
                "33333333-3333-3333-3333-333333333333",
                deletedAt.AddMinutes(2)));

        Assert.Equal(TransitionDisposition.Idempotent, repeatedCompletion.Disposition);
        Assert.Same(deleted, repeatedCompletion.Record);
        Assert.Equal(TransitionDisposition.Idempotent, repeatedAfterDeleted.Disposition);
        Assert.Same(deleted, repeatedAfterDeleted.Record);
        Assert.Equal(DeletedBy, repeatedAfterDeleted.Record.DeletedBy);
        Assert.Equal(requestedAt, repeatedAfterDeleted.Record.DeletionRequestedAt);
        Assert.Equal(deletedAt, repeatedAfterDeleted.Record.DeletedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("line\nbreak")]
    [InlineData("tab\tbreak")]
    public void Delete_request_rejects_empty_or_control_character_actor_ids(string deletedBy)
    {
        var record = CreateRecord(FileState.Pending);

        var transition = FileStateMachine.Transition(
            record,
            FileTransition.DeleteRequested(deletedBy, record.UpdatedAt.AddMinutes(1)));

        Assert.Equal(TransitionDisposition.Rejected, transition.Disposition);
        Assert.Equal(TransitionRejection.InvalidTransition, transition.Rejection);
        Assert.Same(record, transition.Record);
    }

    [Fact]
    public void Delete_request_rejects_oversized_actor_ids()
    {
        var record = CreateRecord(FileState.Pending);

        var transition = FileStateMachine.Transition(
            record,
            FileTransition.DeleteRequested(new string('a', 129), record.UpdatedAt.AddMinutes(1)));

        Assert.Equal(TransitionDisposition.Rejected, transition.Disposition);
        Assert.Equal(TransitionRejection.InvalidTransition, transition.Rejection);
        Assert.Same(record, transition.Record);
    }

    [Fact]
    public void Delete_request_and_completion_reject_invalid_timestamps()
    {
        var record = CreateRecord(FileState.Pending);

        var invalidRequest = FileStateMachine.Transition(
            record,
            FileTransition.DeleteRequested(DeletedBy, DateTimeOffset.MinValue));

        Assert.Equal(TransitionDisposition.Rejected, invalidRequest.Disposition);
        Assert.Equal(TransitionRejection.InvalidTransition, invalidRequest.Rejection);
        Assert.Same(record, invalidRequest.Record);

        var requestedAt = record.UpdatedAt.AddMinutes(1);
        var deleting = FileStateMachine.Transition(
            record,
            FileTransition.DeleteRequested(DeletedBy, requestedAt));

        var invalidCompletion = FileStateMachine.Transition(
            deleting.Record,
            FileTransition.DeleteCompleted(requestedAt.AddTicks(-1)));

        Assert.Equal(TransitionDisposition.Rejected, invalidCompletion.Disposition);
        Assert.Equal(TransitionRejection.InvalidTransition, invalidCompletion.Rejection);
        Assert.Same(deleting.Record, invalidCompletion.Record);
    }

    [Theory]
    [MemberData(nameof(NonDeletionTransitions))]
    public void Deleting_and_deleted_reject_non_deletion_transitions(FileTransition transition)
    {
        var requestedAt = CreatedAt.AddMinutes(5);
        var deletedAt = requestedAt.AddMinutes(1);
        var deleting = FileStateMachine.Transition(
            CreateRecord(FileState.Available),
            FileTransition.DeleteRequested(DeletedBy, requestedAt)).Record;
        var deleted = FileStateMachine.Transition(
            deleting,
            FileTransition.DeleteCompleted(deletedAt)).Record;

        var deletingResult = FileStateMachine.Transition(deleting, transition);
        var deletedResult = FileStateMachine.Transition(deleted, transition);

        Assert.Equal(TransitionDisposition.Rejected, deletingResult.Disposition);
        Assert.Same(deleting, deletingResult.Record);
        Assert.Equal(TransitionDisposition.Rejected, deletedResult.Disposition);
        Assert.Same(deleted, deletedResult.Record);
    }

    public static TheoryData<FileTransition> NonDeletionTransitions =>
    [
        FileTransition.UploadCompleted(SourceETag, 42, CreatedAt.AddMinutes(10)),
        FileTransition.UploadFailed("upload-failed", CreatedAt.AddMinutes(10)),
        FileTransition.Clean("event-clean", "correlation-clean", SourceETag, CreatedAt.AddMinutes(10)),
        FileTransition.Malicious("event-malicious", "correlation-malicious", SourceETag, CreatedAt.AddMinutes(10)),
        FileTransition.ScanFailed(
            "event-error",
            "correlation-error",
            SourceETag,
            "scan-error",
            CreatedAt.AddMinutes(10)),
        FileTransition.TargetCopyRecorded("\"target-v2\"", CreatedAt.AddMinutes(10)),
        FileTransition.PromotionCompleted("\"target-v2\"", CreatedAt.AddMinutes(10)),
        FileTransition.QuarantineCompleted("\"target-v2\"", CreatedAt.AddMinutes(10))
    ];

    private static FileRecord NewUploading() =>
        FileRecord.CreateUploading("report.pdf", "application/pdf", CreatedAt, stableId: new string('a', 64));

    private static FileRecord CreateRecord(FileState state)
    {
        var record = NewUploading();
        Set(record, nameof(FileRecord.State), state);

        if (state != FileState.Uploading)
        {
            Set(record, nameof(FileRecord.SizeBytes), 42L);
            Set(record, nameof(FileRecord.SourceETag), SourceETag);
            Set(record, nameof(FileRecord.PendingBlobUri), new Uri("https://storage.test/pending/report.pdf"));
            Set(record, nameof(FileRecord.UploadedAt), CreatedAt.AddMinutes(1));
            Set(record, nameof(FileRecord.UpdatedAt), CreatedAt.AddMinutes(1));
        }

        if (state is FileState.Promoting or FileState.Quarantining or FileState.Available or
            FileState.Rejected or FileState.ScanError)
        {
            Set(record, nameof(FileRecord.LastEventId), "event-1");
            Set(record, nameof(FileRecord.ScanCorrelationId), "correlation-1");
            Set(record, nameof(FileRecord.ProcessingStartedAt), CreatedAt.AddMinutes(2));
            Set(record, nameof(FileRecord.UpdatedAt), CreatedAt.AddMinutes(2));
        }

        if (state is FileState.Promoting or FileState.Quarantining or FileState.Available or FileState.Rejected)
        {
            Set(record, nameof(FileRecord.TargetETag), "\"target-v1\"");
        }

        if (state is FileState.Available or FileState.Rejected or FileState.ScanError)
        {
            Set(record, nameof(FileRecord.ScanCompletedAt), CreatedAt.AddMinutes(3));
            Set(record, nameof(FileRecord.UpdatedAt), CreatedAt.AddMinutes(3));
        }

        if (state is FileState.ScanError or FileState.UploadFailed)
        {
            Set(record, nameof(FileRecord.FailureCode), "existing-failure");
        }

        if (state == FileState.UploadFailed)
        {
            Set(record, nameof(FileRecord.UpdatedAt), CreatedAt.AddMinutes(2));
        }

        return record;
    }

    private static FileRecord MakeAvailable(string eventId, string correlationId)
    {
        var pending = FileStateMachine.Transition(
            NewUploading(),
            FileTransition.UploadCompleted(SourceETag, 42, CreatedAt.AddMinutes(1))).Record;
        var promoting = FileStateMachine.Transition(
            pending,
            FileTransition.Clean(eventId, correlationId, SourceETag, CreatedAt.AddMinutes(2))).Record;
        return FileStateMachine.Transition(
            promoting,
            FileTransition.PromotionCompleted("\"clean-v1\"", CreatedAt.AddMinutes(3))).Record;
    }

    private static void Set<T>(FileRecord record, string propertyName, T value) =>
        typeof(FileRecord).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(record, value);
}
