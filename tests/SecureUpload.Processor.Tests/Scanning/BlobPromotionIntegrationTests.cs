using Azure;
using Azure.Storage.Blobs;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Scanning;

namespace SecureUpload.Processor.Tests.Scanning;

public sealed class BlobPromotionIntegrationTests
{
    [Theory]
    [InlineData(BlobArea.Clean)]
    [InlineData(BlobArea.Quarantine)]
    public async Task Conditional_copy_verification_and_source_cleanup_follow_azurite_semantics(
        BlobArea destination)
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
            var store = new AzureBlobFileStore(service, options);
            var stableId = new string('c', 64);
            var payload = "verified bytes"u8.ToArray();
            var upload = await store.UploadPendingAsync(
                stableId,
                new MemoryStream(payload),
                new Dictionary<string, string>());

            var copy = await store.CopyPendingAsync(stableId, destination, upload.ETag);
            var target = await store.GetPropertiesAsync(stableId, destination);

            Assert.NotNull(target);
            Assert.Equal(copy.DestinationETag, target.ETag);
            Assert.NotNull(await store.GetPropertiesAsync(stableId, BlobArea.Pending));
            Assert.Equal(
                ConditionalBlobDeleteDisposition.ETagMismatch,
                await store.DeleteIfMatchAsync(
                    stableId,
                    BlobArea.Pending,
                    new ETag("\"stale\"")));
            Assert.Equal(
                ConditionalBlobDeleteDisposition.Deleted,
                await store.DeleteIfMatchAsync(stableId, BlobArea.Pending, upload.ETag));
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
}
