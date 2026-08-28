using Azure;
using System.Text;
using Azure.Storage.Blobs;
using SecureUpload.Core.Storage;

namespace SecureUpload.Core.Tests.Storage;

public sealed class AzureBlobFileStoreTests
{
    [Fact]
    public async Task Conditional_delete_reports_not_found_mismatch_and_deleted_using_concrete_etags()
    {
        var connectionString = Environment.GetEnvironmentVariable("AZURITE_BLOB_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var options = new BlobStorageOptions
        {
            PendingContainerName = $"pending{suffix}",
            CleanContainerName = $"clean{suffix}",
            QuarantineContainerName = $"quarantine{suffix}"
        };
        var service = new BlobServiceClient(connectionString);
        foreach (var name in new[]
                 {
                     options.PendingContainerName,
                     options.CleanContainerName,
                     options.QuarantineContainerName
                 })
        {
            await service.CreateBlobContainerAsync(name);
        }

        try
        {
            var stableId = new string('d', 64);
            var store = new AzureBlobFileStore(service, options);
            var upload = await store.UploadPendingAsync(
                stableId,
                new MemoryStream("verified bytes"u8.ToArray()),
                new Dictionary<string, string>());

            Assert.Equal(
                ConditionalBlobDeleteDisposition.NotFound,
                await store.DeleteIfMatchAsync(
                    stableId,
                    BlobArea.Clean,
                    new ETag("\"clean-v1\"")));
            Assert.Equal(
                ConditionalBlobDeleteDisposition.ETagMismatch,
                await store.DeleteIfMatchAsync(
                    stableId,
                    BlobArea.Pending,
                    new ETag("\"stale\"")));
            Assert.Equal(
                ConditionalBlobDeleteDisposition.Deleted,
                await store.DeleteIfMatchAsync(
                    stableId,
                    BlobArea.Pending,
                    upload.ETag));
            Assert.Null(await store.GetPropertiesAsync(stableId, BlobArea.Pending));
        }
        finally
        {
            foreach (var name in new[]
                     {
                         options.PendingContainerName,
                         options.CleanContainerName,
                         options.QuarantineContainerName
                     })
            {
                await service.DeleteBlobContainerAsync(name);
            }
        }
    }

    [Fact]
    public async Task Conditional_clean_read_reports_not_found_mismatch_and_streamed_bytes_using_concrete_etags()
    {
        var connectionString = Environment.GetEnvironmentVariable("AZURITE_BLOB_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var options = new BlobStorageOptions
        {
            PendingContainerName = $"pending{suffix}",
            CleanContainerName = $"clean{suffix}",
            QuarantineContainerName = $"quarantine{suffix}"
        };
        var service = new BlobServiceClient(connectionString);
        foreach (var name in new[]
            {
                options.PendingContainerName,
                options.CleanContainerName,
                options.QuarantineContainerName
            })
        {
            await service.CreateBlobContainerAsync(name);
        }

        try
        {
            var stableId = new string('c', 64);
            var store = new AzureBlobFileStore(service, options);
            var upload = await store.UploadPendingAsync(
                stableId,
                new MemoryStream("verified bytes"u8.ToArray()),
                new Dictionary<string, string>());
            var copy = await store.CopyPendingAsync(stableId, BlobArea.Clean, upload.ETag);

            var missing = await store.OpenCleanReadIfMatchAsync(new string('e', 64), copy.DestinationETag);
            var mismatch = await store.OpenCleanReadIfMatchAsync(stableId, new ETag("\"stale\""));
            var success = await store.OpenCleanReadIfMatchAsync(stableId, copy.DestinationETag);

            Assert.Equal(ConditionalBlobReadDisposition.NotFound, missing.Disposition);
            Assert.Equal(ConditionalBlobReadDisposition.ETagMismatch, mismatch.Disposition);
            Assert.Equal(ConditionalBlobReadDisposition.Succeeded, success.Disposition);
            await using var content = success.Blob!.Content;
            using var reader = new StreamReader(content, Encoding.UTF8);
            Assert.Equal("verified bytes", await reader.ReadToEndAsync());
        }
        finally
        {
            foreach (var name in new[]
            {
                options.PendingContainerName,
                options.CleanContainerName,
                options.QuarantineContainerName
            })
            {
                await service.DeleteBlobContainerAsync(name);
            }
        }
    }
}
