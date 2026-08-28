using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Management.Telemetry;

namespace SecureUpload.Management.Files;

public enum FileDeletionDisposition
{
    Requested,
    AlreadyDeleting,
    AlreadyDeleted,
    InvalidId,
    NotFound,
    StorageError
}

public sealed record FileDeletionResult(
    FileDeletionDisposition Disposition,
    FileRecord? Record = null);

public sealed class FileDeletionService
{
    private readonly IFileStatusStore _statusStore;
    private readonly TimeProvider _timeProvider;
    private readonly ManagementTelemetry _telemetry;
    private readonly int _maximumConcurrencyAttempts;

    public FileDeletionService(
        IFileStatusStore statusStore,
        TimeProvider timeProvider,
        ManagementTelemetry telemetry,
        int maximumConcurrencyAttempts = 5)
    {
        _statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        if (maximumConcurrencyAttempts is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrencyAttempts),
                "Deletion concurrency attempts must be between 1 and 20.");
        }

        _maximumConcurrencyAttempts = maximumConcurrencyAttempts;
    }

    public async Task<FileDeletionResult> RequestAsync(
        string? fileId,
        string deletedBy,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeStableId(fileId, out var stableId))
        {
            return new(FileDeletionDisposition.InvalidId);
        }

        if (!Guid.TryParse(deletedBy, out _))
        {
            throw new ArgumentException(
                "DeletedBy must be a valid Entra object ID.",
                nameof(deletedBy));
        }

        try
        {
            var current = await _statusStore.GetAsync(stableId, cancellationToken);
            if (current is null)
            {
                return new(FileDeletionDisposition.NotFound);
            }

            for (var attempt = 0; attempt < _maximumConcurrencyAttempts; attempt++)
            {
                switch (current.State)
                {
                    case FileState.Deleting:
                        return new(FileDeletionDisposition.AlreadyDeleting, current);
                    case FileState.Deleted:
                        return new(FileDeletionDisposition.AlreadyDeleted, current);
                }

                if (current.StoreETag is not { } expectedETag)
                {
                    _telemetry.RecordActionStorageFailure(
                        "delete",
                        new InvalidOperationException("Deletion request requires a concrete Table ETag."));
                    return new(FileDeletionDisposition.StorageError, current);
                }

                var requestedAt = _timeProvider.GetUtcNow();
                if (requestedAt < current.UpdatedAt)
                {
                    requestedAt = current.UpdatedAt;
                }

                var transition = FileStateMachine.Transition(
                    current,
                    FileTransition.DeleteRequested(deletedBy, requestedAt));
                if (transition.Disposition == TransitionDisposition.Idempotent)
                {
                    return new(FileDeletionDisposition.AlreadyDeleting, transition.Record);
                }

                if (transition.Disposition != TransitionDisposition.Applied)
                {
                    _telemetry.RecordActionStorageFailure(
                        "delete",
                        new InvalidOperationException("Deletion transition was rejected."));
                    return new(FileDeletionDisposition.StorageError, current);
                }

                var write = await _statusStore.UpdateAsync(
                    transition.Record,
                    expectedETag,
                    cancellationToken);
                switch (write.Disposition)
                {
                    case StatusWriteDisposition.Succeeded:
                        return new(FileDeletionDisposition.Requested, write.Record);
                    case StatusWriteDisposition.NotFound:
                        return new(FileDeletionDisposition.NotFound);
                    case StatusWriteDisposition.ConcurrencyConflict:
                        current = await _statusStore.GetAsync(stableId, cancellationToken);
                        if (current is null)
                        {
                            return new(FileDeletionDisposition.NotFound);
                        }

                        break;
                    default:
                        _telemetry.RecordActionStorageFailure(
                            "delete",
                            new InvalidOperationException($"Unexpected status write disposition: {write.Disposition}."));
                        return new(FileDeletionDisposition.StorageError, current);
                }
            }

            _telemetry.RecordActionStorageFailure(
                "delete",
                new InvalidOperationException("Deletion request concurrency retry limit reached."));
            return new(FileDeletionDisposition.StorageError, current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _telemetry.RecordActionStorageFailure("delete", exception);
            return new(FileDeletionDisposition.StorageError);
        }
    }

    private static bool TryNormalizeStableId(string? candidate, out string stableId)
    {
        stableId = candidate?.Trim().ToLowerInvariant() ?? string.Empty;
        if (stableId.Length != 64 || stableId.Any(character =>
                character is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
        {
            stableId = string.Empty;
            return false;
        }

        return true;
    }
}
