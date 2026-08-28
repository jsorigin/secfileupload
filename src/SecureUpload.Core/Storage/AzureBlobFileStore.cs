using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using SecureUpload.Core.Files;

namespace SecureUpload.Core.Storage;

public sealed class BlobStorageOptions
{
    public string PendingContainerName { get; init; } = "pending";
    public string CleanContainerName { get; init; } = "clean";
    public string QuarantineContainerName { get; init; } = "quarantine";
}

public sealed class AzureBlobFileStore : IBlobFileStore
{
    private readonly BlobServiceClient _serviceClient;
    private readonly BlobStorageOptions _options;

    public AzureBlobFileStore(BlobServiceClient serviceClient, BlobStorageOptions? options = null)
    {
        _serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        _options = options ?? new BlobStorageOptions();
    }

    public AzureBlobFileStore(
        Uri serviceUri,
        BlobStorageOptions? options = null,
        TokenCredential? credential = null)
        : this(
            new BlobServiceClient(
                serviceUri,
                credential ?? new DefaultAzureCredential()),
            options)
    {
    }

    public async Task<BlobWriteResult> UploadPendingAsync(
        string stableId,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        FileRecord.ValidateStableId(stableId);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        var blob = GetBlob(BlobArea.Pending, stableId);
        var countedContent = new CountingReadStream(content);
        var response = await blob.UploadAsync(
            countedContent,
            new BlobUploadOptions
            {
                Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            },
            cancellationToken);

        return new(blob.Uri, response.Value.ETag, countedContent.BytesRead);
    }

    public async Task<BlobCopyResult> CopyPendingAsync(
        string stableId,
        BlobArea destination,
        ETag expectedSourceETag,
        CancellationToken cancellationToken = default)
    {
        FileRecord.ValidateStableId(stableId);
        if (destination is not (BlobArea.Clean or BlobArea.Quarantine))
        {
            throw new ArgumentOutOfRangeException(nameof(destination), "Pending blobs can only move to clean or quarantine.");
        }

        if (expectedSourceETag == default || expectedSourceETag == ETag.All)
        {
            throw new ArgumentException("A concrete source ETag is required.", nameof(expectedSourceETag));
        }

        var source = GetBlob(BlobArea.Pending, stableId);
        var target = GetBlob(destination, stableId);
        var operation = await target.StartCopyFromUriAsync(
            source.Uri,
            new BlobCopyFromUriOptions
            {
                SourceConditions = new BlobRequestConditions { IfMatch = expectedSourceETag },
                DestinationConditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            },
            cancellationToken);
        await operation.WaitForCompletionAsync(cancellationToken);

        var properties = await target.GetPropertiesAsync(cancellationToken: cancellationToken);
        if (properties.Value.CopyStatus != CopyStatus.Success)
        {
            throw new InvalidOperationException($"Blob copy did not complete successfully: {properties.Value.CopyStatus}.");
        }

        return new(source.Uri, target.Uri, properties.Value.ETag);
    }

    public async Task<ConditionalBlobDeleteDisposition> DeleteIfMatchAsync(
        string stableId,
        BlobArea area,
        ETag expectedETag,
        CancellationToken cancellationToken = default)
    {
        FileRecord.ValidateStableId(stableId);
        if (expectedETag == default || expectedETag == ETag.All)
        {
            throw new ArgumentException("A concrete blob ETag is required.", nameof(expectedETag));
        }

        try
        {
            var response = await GetBlob(area, stableId).DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                new BlobRequestConditions { IfMatch = expectedETag },
                cancellationToken);
            return response.Value
                ? ConditionalBlobDeleteDisposition.Deleted
                : ConditionalBlobDeleteDisposition.NotFound;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return ConditionalBlobDeleteDisposition.NotFound;
        }
        catch (RequestFailedException exception) when (exception.Status == 412)
        {
            return ConditionalBlobDeleteDisposition.ETagMismatch;
        }
    }

    public async Task<ConditionalBlobReadResult> OpenCleanReadIfMatchAsync(
        string stableId,
        ETag expectedETag,
        CancellationToken cancellationToken = default)
    {
        FileRecord.ValidateStableId(stableId);
        if (expectedETag == default || expectedETag == ETag.All)
        {
            throw new ArgumentException("A concrete blob ETag is required.", nameof(expectedETag));
        }

        try
        {
            var response = await GetBlob(BlobArea.Clean, stableId).DownloadStreamingAsync(
                new BlobDownloadOptions
                {
                    Conditions = new BlobRequestConditions { IfMatch = expectedETag }
                },
                cancellationToken);
            if (response.Value.Details.ETag != expectedETag)
            {
                await response.Value.Content.DisposeAsync();
                return new(ConditionalBlobReadDisposition.ETagMismatch);
            }

            return new(
                ConditionalBlobReadDisposition.Succeeded,
                new BlobReadResult(
                    response.Value.Content,
                    response.Value.Details.ETag));
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return new(ConditionalBlobReadDisposition.NotFound);
        }
        catch (RequestFailedException exception) when (exception.Status == 412)
        {
            return new(ConditionalBlobReadDisposition.ETagMismatch);
        }
    }

    public async Task<BlobWriteResult?> GetPropertiesAsync(
        string stableId,
        BlobArea area,
        CancellationToken cancellationToken = default)
    {
        FileRecord.ValidateStableId(stableId);
        var blob = GetBlob(area, stableId);

        try
        {
            var response = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            return new(blob.Uri, response.Value.ETag, response.Value.ContentLength);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    private BlockBlobClient GetBlob(BlobArea area, string stableId) =>
        _serviceClient
            .GetBlobContainerClient(area switch
            {
                BlobArea.Pending => _options.PendingContainerName,
                BlobArea.Clean => _options.CleanContainerName,
                BlobArea.Quarantine => _options.QuarantineContainerName,
                _ => throw new ArgumentOutOfRangeException(nameof(area))
            })
            .GetBlockBlobClient(stableId);

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            BytesRead += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    }
}
