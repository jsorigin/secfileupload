using Azure;

namespace SecureUpload.Core.Storage;

public enum BlobArea
{
    Pending,
    Clean,
    Quarantine
}

public sealed record BlobWriteResult(
    Uri BlobUri,
    ETag ETag,
    long SizeBytes);

public sealed record BlobCopyResult(
    Uri SourceUri,
    Uri DestinationUri,
    ETag DestinationETag);

public sealed record BlobReadResult(
    Stream Content,
    ETag ETag);

public enum ConditionalBlobDeleteDisposition
{
    Deleted,
    NotFound,
    ETagMismatch
}

public enum ConditionalBlobReadDisposition
{
    Succeeded,
    NotFound,
    ETagMismatch
}

public sealed record ConditionalBlobReadResult(
    ConditionalBlobReadDisposition Disposition,
    BlobReadResult? Blob = null);

public interface IBlobFileStore
{
    Task<BlobWriteResult> UploadPendingAsync(
        string stableId,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default);

    Task<BlobCopyResult> CopyPendingAsync(
        string stableId,
        BlobArea destination,
        ETag expectedSourceETag,
        CancellationToken cancellationToken = default);

    Task<ConditionalBlobDeleteDisposition> DeleteIfMatchAsync(
        string stableId,
        BlobArea area,
        ETag expectedETag,
        CancellationToken cancellationToken = default);

    Task<ConditionalBlobReadResult> OpenCleanReadIfMatchAsync(
        string stableId,
        ETag expectedETag,
        CancellationToken cancellationToken = default);

    Task<BlobWriteResult?> GetPropertiesAsync(
        string stableId,
        BlobArea area,
        CancellationToken cancellationToken = default);
}
