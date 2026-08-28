namespace SecureUpload.Core.Files;

public enum TransitionDisposition
{
    Applied,
    Idempotent,
    Reconciled,
    Rejected
}

public enum TransitionRejection
{
    None,
    InvalidTransition,
    SourceETagMismatch,
    TerminalConflict
}

public enum FileTransitionKind
{
    UploadCompleted,
    UploadFailed,
    Clean,
    Malicious,
    ScanFailed,
    TargetCopyRecorded,
    PromotionCompleted,
    QuarantineCompleted,
    DeleteRequested,
    DeleteCompleted
}

public sealed record FileTransition
{
    public required FileTransitionKind Kind { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public string? SourceETag { get; init; }
    public string? TargetETag { get; init; }
    public long? SizeBytes { get; init; }
    public string? EventId { get; init; }
    public string? CorrelationId { get; init; }
    public string? FailureCode { get; init; }
    public Uri? PendingBlobUri { get; init; }
    public string? DeletedBy { get; init; }

    public static FileTransition UploadCompleted(
        string sourceETag,
        long sizeBytes,
        DateTimeOffset occurredAt,
        Uri? pendingBlobUri = null) =>
        new()
        {
            Kind = FileTransitionKind.UploadCompleted,
            SourceETag = sourceETag,
            SizeBytes = sizeBytes,
            PendingBlobUri = pendingBlobUri,
            OccurredAt = occurredAt
        };

    public static FileTransition UploadFailed(string failureCode, DateTimeOffset occurredAt) =>
        new() { Kind = FileTransitionKind.UploadFailed, FailureCode = failureCode, OccurredAt = occurredAt };

    public static FileTransition Clean(string eventId, string correlationId, string sourceETag, DateTimeOffset occurredAt) =>
        Scanned(FileTransitionKind.Clean, eventId, correlationId, sourceETag, null, occurredAt);

    public static FileTransition Malicious(string eventId, string correlationId, string sourceETag, DateTimeOffset occurredAt) =>
        Scanned(FileTransitionKind.Malicious, eventId, correlationId, sourceETag, null, occurredAt);

    public static FileTransition ScanFailed(
        string eventId,
        string correlationId,
        string sourceETag,
        string failureCode,
        DateTimeOffset occurredAt) =>
        Scanned(FileTransitionKind.ScanFailed, eventId, correlationId, sourceETag, failureCode, occurredAt);

    public static FileTransition PromotionCompleted(string targetETag, DateTimeOffset occurredAt) =>
        new() { Kind = FileTransitionKind.PromotionCompleted, TargetETag = targetETag, OccurredAt = occurredAt };

    public static FileTransition TargetCopyRecorded(string targetETag, DateTimeOffset occurredAt) =>
        new() { Kind = FileTransitionKind.TargetCopyRecorded, TargetETag = targetETag, OccurredAt = occurredAt };

    public static FileTransition QuarantineCompleted(string targetETag, DateTimeOffset occurredAt) =>
        new() { Kind = FileTransitionKind.QuarantineCompleted, TargetETag = targetETag, OccurredAt = occurredAt };

    public static FileTransition DeleteRequested(string deletedBy, DateTimeOffset occurredAt) =>
        new() { Kind = FileTransitionKind.DeleteRequested, DeletedBy = deletedBy, OccurredAt = occurredAt };

    public static FileTransition DeleteCompleted(DateTimeOffset occurredAt) =>
        new() { Kind = FileTransitionKind.DeleteCompleted, OccurredAt = occurredAt };

    private static FileTransition Scanned(
        FileTransitionKind kind,
        string eventId,
        string correlationId,
        string sourceETag,
        string? failureCode,
        DateTimeOffset occurredAt) =>
        new()
        {
            Kind = kind,
            EventId = eventId,
            CorrelationId = correlationId,
            SourceETag = sourceETag,
            FailureCode = failureCode,
            OccurredAt = occurredAt
        };
}

public sealed record FileTransitionResult(
    TransitionDisposition Disposition,
    FileRecord Record,
    TransitionRejection Rejection = TransitionRejection.None);

public static class FileStateMachine
{
    public static FileTransitionResult Transition(FileRecord current, FileTransition transition)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(transition);

        if (current.State is FileState.Deleting or FileState.Deleted &&
            transition.Kind is not (FileTransitionKind.DeleteRequested or FileTransitionKind.DeleteCompleted))
        {
            return Reject(current, TransitionRejection.InvalidTransition);
        }

        return transition.Kind switch
        {
            FileTransitionKind.UploadCompleted => CompleteUpload(current, transition),
            FileTransitionKind.UploadFailed => FailUpload(current, transition),
            FileTransitionKind.Clean => BeginProcessing(current, transition, FileState.Promoting),
            FileTransitionKind.Malicious => BeginProcessing(current, transition, FileState.Quarantining),
            FileTransitionKind.ScanFailed => RecordScanFailure(current, transition),
            FileTransitionKind.TargetCopyRecorded => RecordTargetCopy(current, transition),
            FileTransitionKind.PromotionCompleted => CompleteProcessing(current, transition, FileState.Promoting, FileState.Available),
            FileTransitionKind.QuarantineCompleted => CompleteProcessing(current, transition, FileState.Quarantining, FileState.Rejected),
            FileTransitionKind.DeleteRequested => RequestDeletion(current, transition),
            FileTransitionKind.DeleteCompleted => CompleteDeletion(current, transition),
            _ => Reject(current, TransitionRejection.InvalidTransition)
        };
    }

    private static FileTransitionResult RequestDeletion(FileRecord current, FileTransition transition)
    {
        if (current.State is FileState.Deleting or FileState.Deleted)
        {
            return Idempotent(current);
        }

        if (!FileRecord.HasValidDeletionActor(transition.DeletedBy) ||
            !FileRecord.HasValidTableTimestamp(transition.OccurredAt) ||
            transition.OccurredAt < current.UpdatedAt)
        {
            return Reject(current, TransitionRejection.InvalidTransition);
        }

        return Applied(current with
        {
            State = FileState.Deleting,
            DeletedBy = transition.DeletedBy,
            DeletionRequestedAt = transition.OccurredAt,
            DeletedAt = null,
            UpdatedAt = transition.OccurredAt
        });
    }

    private static FileTransitionResult CompleteDeletion(FileRecord current, FileTransition transition)
    {
        if (current.State == FileState.Deleted)
        {
            return Idempotent(current);
        }

        if (current.State != FileState.Deleting ||
            current.DeletionRequestedAt is null ||
            !FileRecord.HasValidDeletionActor(current.DeletedBy) ||
            !FileRecord.HasValidTableTimestamp(transition.OccurredAt) ||
            transition.OccurredAt < current.DeletionRequestedAt.Value ||
            transition.OccurredAt < current.UpdatedAt)
        {
            return Reject(current, TransitionRejection.InvalidTransition);
        }

        return Applied(current with
        {
            State = FileState.Deleted,
            DeletedAt = transition.OccurredAt,
            UpdatedAt = transition.OccurredAt
        });
    }

    private static FileTransitionResult CompleteUpload(FileRecord current, FileTransition transition)
    {
        if (string.IsNullOrWhiteSpace(transition.SourceETag) || transition.SizeBytes is null or < 0)
        {
            return Reject(current, TransitionRejection.InvalidTransition);
        }

        if (current.SourceETag is not null &&
            !StringComparer.Ordinal.Equals(current.SourceETag, transition.SourceETag))
        {
            return Reject(current, TransitionRejection.SourceETagMismatch);
        }

        if (current.State == FileState.Pending)
        {
            return Idempotent(current);
        }

        if (current.State != FileState.Uploading)
        {
            return current.State is FileState.Promoting or FileState.Quarantining or
                FileState.Available or FileState.Rejected or FileState.ScanError
                ? new(TransitionDisposition.Reconciled, current)
                : Reject(current, TransitionRejection.InvalidTransition);
        }

        return Applied(current with
        {
            State = FileState.Pending,
            SourceETag = transition.SourceETag,
            SizeBytes = transition.SizeBytes,
            PendingBlobUri = transition.PendingBlobUri,
            UploadedAt = transition.OccurredAt,
            UpdatedAt = transition.OccurredAt,
            FailureCode = null,
            StoreETag = current.StoreETag
        });
    }

    private static FileTransitionResult FailUpload(FileRecord current, FileTransition transition)
    {
        if (current.State == FileState.UploadFailed)
        {
            return Idempotent(current);
        }

        if (current.State != FileState.Uploading || string.IsNullOrWhiteSpace(transition.FailureCode))
        {
            return Reject(current, TransitionRejection.InvalidTransition);
        }

        return Applied(current with
        {
            State = FileState.UploadFailed,
            FailureCode = transition.FailureCode,
            UpdatedAt = transition.OccurredAt
        });
    }

    private static FileTransitionResult BeginProcessing(
        FileRecord current,
        FileTransition transition,
        FileState processingState)
    {
        var terminalState = processingState == FileState.Promoting ? FileState.Available : FileState.Rejected;
        var conflictingTerminal = processingState == FileState.Promoting ? FileState.Rejected : FileState.Available;

        if (current.State == conflictingTerminal)
        {
            return Reject(current, TransitionRejection.TerminalConflict);
        }

        if (current.State == terminalState || current.State == processingState)
        {
            return SourceMatches(current, transition)
                ? Idempotent(current)
                : Reject(current, TransitionRejection.SourceETagMismatch);
        }

        if (current.State is not (FileState.Uploading or FileState.Pending or FileState.ScanError))
        {
            return Reject(current, TransitionRejection.InvalidTransition);
        }

        if (!SourceMatches(current, transition))
        {
            return Reject(current, TransitionRejection.SourceETagMismatch);
        }

        if (string.IsNullOrWhiteSpace(transition.EventId) ||
            string.IsNullOrWhiteSpace(transition.CorrelationId) ||
            string.IsNullOrWhiteSpace(transition.SourceETag))
        {
            return Reject(current, TransitionRejection.InvalidTransition);
        }

        return Applied(current with
        {
            State = processingState,
            SourceETag = transition.SourceETag,
            LastEventId = transition.EventId,
            ScanCorrelationId = transition.CorrelationId,
            ProcessingStartedAt = transition.OccurredAt,
            UpdatedAt = transition.OccurredAt,
            FailureCode = null,
            TargetETag = null
        });
    }

    private static FileTransitionResult RecordScanFailure(FileRecord current, FileTransition transition)
    {
        if (current.State is FileState.Available or FileState.Rejected)
        {
            return Reject(current, TransitionRejection.TerminalConflict);
        }

        if (current.State == FileState.ScanError)
        {
            return SourceMatches(current, transition)
                ? Idempotent(current)
                : Reject(current, TransitionRejection.SourceETagMismatch);
        }

        if (current.State is not (FileState.Uploading or FileState.Pending or FileState.Promoting or FileState.Quarantining) ||
            !SourceMatches(current, transition) ||
            string.IsNullOrWhiteSpace(transition.SourceETag) ||
            string.IsNullOrWhiteSpace(transition.EventId) ||
            string.IsNullOrWhiteSpace(transition.CorrelationId) ||
            string.IsNullOrWhiteSpace(transition.FailureCode))
        {
            return Reject(current, current.SourceETag is not null && !SourceMatches(current, transition)
                ? TransitionRejection.SourceETagMismatch
                : TransitionRejection.InvalidTransition);
        }

        return Applied(current with
        {
            State = FileState.ScanError,
            SourceETag = transition.SourceETag,
            LastEventId = transition.EventId,
            ScanCorrelationId = transition.CorrelationId,
            FailureCode = transition.FailureCode,
            ScanCompletedAt = transition.OccurredAt,
            UpdatedAt = transition.OccurredAt
        });
    }

    private static FileTransitionResult CompleteProcessing(
        FileRecord current,
        FileTransition transition,
        FileState requiredState,
        FileState terminalState)
    {
        if (current.State == terminalState)
        {
            return Idempotent(current);
        }

        if (current.State != requiredState ||
            string.IsNullOrWhiteSpace(transition.TargetETag) ||
            current.TargetETag is not null &&
            !StringComparer.Ordinal.Equals(current.TargetETag, transition.TargetETag))
        {
            return Reject(current, current.State is FileState.Available or FileState.Rejected
                ? TransitionRejection.TerminalConflict
                : TransitionRejection.InvalidTransition);
        }

        return Applied(current with
        {
            State = terminalState,
            TargetETag = transition.TargetETag,
            ScanCompletedAt = transition.OccurredAt,
            UpdatedAt = transition.OccurredAt
        });
    }

    private static FileTransitionResult RecordTargetCopy(FileRecord current, FileTransition transition)
    {
        if (current.State is not (FileState.Promoting or FileState.Quarantining) ||
            string.IsNullOrWhiteSpace(transition.TargetETag))
        {
            return Reject(current, TransitionRejection.InvalidTransition);
        }

        if (current.TargetETag is not null)
        {
            return StringComparer.Ordinal.Equals(current.TargetETag, transition.TargetETag)
                ? Idempotent(current)
                : Reject(current, TransitionRejection.InvalidTransition);
        }

        return Applied(current with
        {
            TargetETag = transition.TargetETag,
            UpdatedAt = transition.OccurredAt
        });
    }

    private static bool SourceMatches(FileRecord current, FileTransition transition) =>
        !string.IsNullOrWhiteSpace(transition.SourceETag) &&
        (current.SourceETag is null || StringComparer.Ordinal.Equals(current.SourceETag, transition.SourceETag));

    private static FileTransitionResult Applied(FileRecord record) =>
        new(TransitionDisposition.Applied, record);

    private static FileTransitionResult Idempotent(FileRecord record) =>
        new(TransitionDisposition.Idempotent, record);

    private static FileTransitionResult Reject(FileRecord record, TransitionRejection rejection) =>
        new(TransitionDisposition.Rejected, record, rejection);
}
