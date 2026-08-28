using Azure;
using SecureUpload.Core.Files;

namespace SecureUpload.Core.Storage;

public enum StatusWriteDisposition
{
    Succeeded,
    AlreadyExists,
    ConcurrencyConflict,
    NotFound
}

public sealed record StatusWriteResult(
    StatusWriteDisposition Disposition,
    FileRecord? Record = null);

public sealed record FileStatusQuery(
    FileState? State = null,
    DateTimeOffset? UpdatedBefore = null);

public interface IFileStatusStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<StatusWriteResult> CreateAsync(
        FileRecord record,
        CancellationToken cancellationToken = default);

    Task<FileRecord?> GetAsync(
        string stableId,
        CancellationToken cancellationToken = default);

    Task<StatusWriteResult> UpdateAsync(
        FileRecord record,
        ETag expectedETag,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<FileRecord> QueryAsync(
        FileStatusQuery query,
        CancellationToken cancellationToken = default);
}
