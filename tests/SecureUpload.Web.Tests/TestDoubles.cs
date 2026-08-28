using Azure;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;

namespace SecureUpload.Web.Tests;

internal sealed class InMemoryStatusStore : IFileStatusStore
{
    private readonly Dictionary<string, FileRecord> _records = [];
    private int _version;

    public bool FailCreate { get; set; }
    public bool FailFinalize { get; set; }
    public bool ThrowFinalize { get; set; }
    public bool RaceFinalizeWithCleanScan { get; set; }
    public int CleanupConflictsRemaining { get; set; }
    public IReadOnlyCollection<FileRecord> Records => _records.Values;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<StatusWriteResult> CreateAsync(FileRecord record, CancellationToken cancellationToken = default)
    {
        if (FailCreate)
        {
            throw new InvalidOperationException("create failed");
        }

        if (_records.ContainsKey(record.StableId))
        {
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.AlreadyExists));
        }

        var stored = WithStoreETag(record, NextETag());
        _records.Add(record.StableId, stored);
        return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.Succeeded, stored));
    }

    public Task<FileRecord?> GetAsync(string stableId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.GetValueOrDefault(stableId));

    public Task<StatusWriteResult> UpdateAsync(
        FileRecord record,
        ETag expectedETag,
        CancellationToken cancellationToken = default)
    {
        if (ThrowFinalize && record.State == FileState.Pending)
        {
            throw new InvalidOperationException("finalize failed");
        }

        if (FailFinalize && record.State == FileState.Pending)
        {
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
        }

        if (record.State == FileState.UploadFailed && CleanupConflictsRemaining-- > 0)
        {
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
        }

        if (!_records.TryGetValue(record.StableId, out var current))
        {
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.NotFound));
        }

        if (RaceFinalizeWithCleanScan && record.State == FileState.Pending && current.State == FileState.Uploading)
        {
            var raced = FileStateMachine.Transition(
                current,
                FileTransition.Clean(
                    "event",
                    "correlation",
                    record.SourceETag!,
                    DateTimeOffset.UtcNow));
            _records[record.StableId] = WithStoreETag(raced.Record, NextETag());
            RaceFinalizeWithCleanScan = false;
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
        }

        if (current.StoreETag != expectedETag)
        {
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
        }

        var stored = WithStoreETag(record, NextETag());
        _records[record.StableId] = stored;
        return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.Succeeded, stored));
    }

    public async IAsyncEnumerable<FileRecord> QueryAsync(
        FileStatusQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var record in _records.Values)
        {
            yield return record;
            await Task.Yield();
        }
    }

    private ETag NextETag() => new($"\"{Interlocked.Increment(ref _version)}\"");

    private static FileRecord WithStoreETag(FileRecord record, ETag etag)
    {
        var stored = record with { };
        typeof(FileRecord).GetProperty(nameof(FileRecord.StoreETag))!.SetValue(stored, etag);
        return stored;
    }
}

internal sealed class RecordingBlobStore : IBlobFileStore
{
    private readonly Dictionary<string, byte[]> _pending = [];

    public bool FailUpload { get; set; }
    public bool FailDelete { get; set; }
    public int UploadAttempts { get; private set; }
    public int DeleteAttempts { get; private set; }
    public IReadOnlyDictionary<string, byte[]> Pending => _pending;
    public IReadOnlyDictionary<string, string>? LastMetadata { get; private set; }

    public async Task<BlobWriteResult> UploadPendingAsync(
        string stableId,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        UploadAttempts++;
        LastMetadata = metadata;
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (FailUpload)
            {
                _pending[stableId] = bytes.ToArray();
                throw new IOException("blob failed");
            }
        }

        _pending[stableId] = bytes.ToArray();
        return new(new Uri($"https://storage.test/pending/{stableId}"), new ETag("\"blob\""), bytes.Length);
    }

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
        DeleteAttempts++;
        if (FailDelete)
        {
            throw new IOException("delete failed");
        }

        return Task.FromResult(_pending.Remove(stableId)
            ? ConditionalBlobDeleteDisposition.Deleted
            : ConditionalBlobDeleteDisposition.NotFound);
    }

    public Task<ConditionalBlobReadResult> OpenCleanReadIfMatchAsync(
        string stableId,
        ETag expectedETag,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<BlobWriteResult?> GetPropertiesAsync(
        string stableId,
        BlobArea area,
        CancellationToken cancellationToken = default)
    {
        if (!_pending.TryGetValue(stableId, out var bytes))
        {
            return Task.FromResult<BlobWriteResult?>(null);
        }

        return Task.FromResult<BlobWriteResult?>(
            new(new Uri($"https://storage.test/pending/{stableId}"), new ETag("\"blob\""), bytes.Length));
    }
}
