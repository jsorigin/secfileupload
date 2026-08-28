using System.Net;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Scanning;

namespace SecureUpload.EndToEnd.Tests;

public sealed class ManagementLifecycleTests
{
    [Fact]
    public async Task AuthorizedAdministratorCanBrowseDownloadDeleteAndObserveTimerCompletedTombstone()
    {
        await using var host = new EndToEndTestHost();
        var availableUpload = await host.UploadAsync(
            "verified clean bytes"u8.ToArray(),
            "available-fixture.txt");
        var pendingUpload = await host.UploadAsync(
            "still pending bytes"u8.ToArray(),
            "pending-fixture.txt");
        await host.CreateProcessor().ProcessAsync(
            host.EventFor(availableUpload.FileId, MalwareScanOutcome.Clean));

        using var client = host.CreateManagementClient(ManagementTestPrincipal.Primary);

        var inventoryHtml = await client.GetStringAsync("/");
        var filteredHtml = await client.GetStringAsync("/?filter=available");
        var detailsHtml = await client.GetStringAsync(
            $"/Files/Details?fileId={availableUpload.FileId}&returnUrl=%2F%3Ffilter%3Davailable");
        var download = await client.GetAsync(
            $"/Files/Details?handler=Download&fileId={availableUpload.FileId}&returnUrl=%2F");
        var downloadBody = await download.Content.ReadAsByteArrayAsync();
        var deleteResponse = await PostDeleteAsync(
            client,
            availableUpload.FileId,
            "available-fixture.txt",
            detailsHtml,
            "/?filter=available");
        var sweep = await host.CreateDeletionProcessor().ProcessPendingAsync(DateTimeOffset.UtcNow);
        var completedHtml = await client.GetStringAsync(deleteResponse.Headers.Location);
        var tombstone = host.Statuses.Required(availableUpload.FileId);

        Assert.Contains("available-fixture.txt", inventoryHtml);
        Assert.Contains("pending-fixture.txt", inventoryHtml);
        Assert.Contains("available-fixture.txt", filteredHtml);
        Assert.DoesNotContain("pending-fixture.txt", filteredHtml, StringComparison.Ordinal);
        Assert.Contains("Download clean file", detailsHtml);
        Assert.Contains("Request permanent deletion", detailsHtml);
        Assert.Contains("Clean storage", detailsHtml);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.Contains("available-fixture.txt", download.Content.Headers.ContentDisposition?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("private", download.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", download.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("verified clean bytes"u8.ToArray(), downloadBody);
        Assert.Equal(HttpStatusCode.Found, deleteResponse.StatusCode);
        Assert.Equal(1, sweep.Finalized);
        Assert.Equal(0, sweep.AlreadyDeleted);
        Assert.Equal(0, sweep.Incomplete);
        Assert.Contains("Deletion completed", completedHtml);
        Assert.Contains("Only the audit tombstone remains.", completedHtml);
        Assert.DoesNotContain("Download clean file", completedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Request permanent deletion", completedHtml, StringComparison.Ordinal);
        Assert.Equal(FileState.Deleted, tombstone.State);
        Assert.Equal(EndToEndTestHost.PrimaryManagementObjectId, tombstone.DeletedBy);
        Assert.Null(host.Blobs.Get(availableUpload.FileId, BlobArea.Pending));
        Assert.Null(host.Blobs.Get(availableUpload.FileId, BlobArea.Clean));
        Assert.Null(host.Blobs.Get(availableUpload.FileId, BlobArea.Quarantine));
        Assert.Equal(FileState.Pending, host.Statuses.Required(pendingUpload.FileId).State);
    }

    [Fact]
    public async Task DeleteRequestWhileCleanCopyIsInFlightConvergesToDeletedAndEmptyContainers()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync(
            "race fixture bytes"u8.ToArray(),
            "race-fixture.txt");
        var scanEvent = host.EventFor(upload.FileId, MalwareScanOutcome.Clean, "race-clean");
        var pause = host.Blobs.PauseNextCopy();

        using var client = host.CreateManagementClient(ManagementTestPrincipal.Primary);

        var processingTask = host.CreateProcessor().ProcessAsync(scanEvent);
        await pause.WaitForCopyAsync();
        var detailsHtml = await client.GetStringAsync(
            $"/Files/Details?fileId={upload.FileId}&returnUrl=%2F");
        var deleteResponse = await PostDeleteAsync(
            client,
            upload.FileId,
            "race-fixture.txt",
            detailsHtml,
            "/");

        Assert.Equal(HttpStatusCode.Found, deleteResponse.StatusCode);
        Assert.Equal(FileState.Deleting, host.Statuses.Required(upload.FileId).State);
        Assert.NotNull(host.Blobs.Get(upload.FileId, BlobArea.Pending));
        Assert.NotNull(host.Blobs.Get(upload.FileId, BlobArea.Clean));

        pause.Release();
        var result = await processingTask;
        var tombstone = host.Statuses.Required(upload.FileId);

        Assert.Equal(ScanProcessingDisposition.Duplicate, result.Disposition);
        Assert.Equal(FileState.Deleted, tombstone.State);
        Assert.Equal(EndToEndTestHost.PrimaryManagementObjectId, tombstone.DeletedBy);
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Pending));
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Clean));
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Quarantine));
    }

    [Fact]
    public async Task CleanupFailureLeavesDeletingAndAuthorizedRetryPreservesTheFirstActor()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync(
            "retry fixture bytes"u8.ToArray(),
            "retry-fixture.txt");
        await host.CreateProcessor().ProcessAsync(
            host.EventFor(upload.FileId, MalwareScanOutcome.Clean));

        using var primaryClient = host.CreateManagementClient(ManagementTestPrincipal.Primary);
        using var secondaryClient = host.CreateManagementClient(ManagementTestPrincipal.Secondary);

        var primaryDetails = await primaryClient.GetStringAsync(
            $"/Files/Details?fileId={upload.FileId}&returnUrl=%2F");
        var secondaryDetails = await secondaryClient.GetStringAsync(
            $"/Files/Details?fileId={upload.FileId}&returnUrl=%2F");
        var deleteResponse = await PostDeleteAsync(
            primaryClient,
            upload.FileId,
            "retry-fixture.txt",
            primaryDetails,
            "/");
        var firstRequest = host.Statuses.Required(upload.FileId);
        host.Blobs.FailNextDeleteArea = BlobArea.Clean;

        await Assert.ThrowsAsync<RetryableScanProcessingException>(
            () => host.CreateDeletionProcessor().ProcessPendingAsync(DateTimeOffset.UtcNow));

        var deleting = host.Statuses.Required(upload.FileId);
        var stuckHtml = await primaryClient.GetStringAsync(
            $"/Files/Details?fileId={upload.FileId}&returnUrl=%2F&action=delete-in-progress&refresh=5");
        var retryResponse = await PostDeleteAsync(
            secondaryClient,
            upload.FileId,
            "retry-fixture.txt",
            secondaryDetails,
            "/");
        var sweep = await host.CreateDeletionProcessor().ProcessPendingAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        var tombstone = host.Statuses.Required(upload.FileId);

        Assert.Equal(HttpStatusCode.Found, deleteResponse.StatusCode);
        Assert.Equal(FileState.Deleting, deleting.State);
        Assert.Equal(EndToEndTestHost.PrimaryManagementObjectId, deleting.DeletedBy);
        Assert.Equal(firstRequest.DeletionRequestedAt, deleting.DeletionRequestedAt);
        Assert.Contains("Deletion is taking longer than expected", stuckHtml);
        Assert.Contains("Refresh now", stuckHtml);
        Assert.Equal(HttpStatusCode.Found, retryResponse.StatusCode);
        Assert.Contains("action=delete-in-progress", retryResponse.Headers.Location?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, sweep.Finalized);
        Assert.Equal(FileState.Deleted, tombstone.State);
        Assert.Equal(EndToEndTestHost.PrimaryManagementObjectId, tombstone.DeletedBy);
        Assert.Equal(firstRequest.DeletionRequestedAt, tombstone.DeletionRequestedAt);
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Clean));
    }

    [Fact]
    public async Task DelayedDuplicateEventsCannotRestoreDeletedContent()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync(
            "duplicate fixture bytes"u8.ToArray(),
            "duplicate-fixture.txt");
        var scanEvent = host.EventFor(upload.FileId, MalwareScanOutcome.Clean, "duplicate-clean");
        await host.CreateProcessor().ProcessAsync(scanEvent);

        using var client = host.CreateManagementClient(ManagementTestPrincipal.Primary);

        var detailsHtml = await client.GetStringAsync(
            $"/Files/Details?fileId={upload.FileId}&returnUrl=%2F");
        await PostDeleteAsync(
            client,
            upload.FileId,
            "duplicate-fixture.txt",
            detailsHtml,
            "/");
        await host.CreateDeletionProcessor().ProcessPendingAsync(DateTimeOffset.UtcNow);
        var replay = await host.CreateProcessor().ProcessAsync(scanEvent);
        var tombstone = host.Statuses.Required(upload.FileId);

        Assert.Equal(ScanProcessingDisposition.Duplicate, replay.Disposition);
        Assert.Equal(FileState.Deleted, tombstone.State);
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Pending));
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Clean));
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Quarantine));
    }

    [Fact]
    public async Task OverCapacityInventoryShowsOnlyTheCapacityFailureAndRetainsExactIdLookup()
    {
        await using var host = new EndToEndTestHost(managementInventoryCapacity: 1);
        var alpha = await host.UploadAsync(
            "alpha bytes"u8.ToArray(),
            "alpha-fixture.txt");
        await host.UploadAsync(
            "beta bytes"u8.ToArray(),
            "beta-fixture.txt");
        await host.CreateProcessor().ProcessAsync(
            host.EventFor(alpha.FileId, MalwareScanOutcome.Clean));

        using var client = host.CreateManagementClient(ManagementTestPrincipal.Primary);

        var inventoryHtml = await client.GetStringAsync("/");
        var detailsHtml = await client.GetStringAsync(
            $"/Files/Details?fileId={alpha.FileId}&returnUrl=%2F");

        Assert.Contains("Inventory browsing is paused", inventoryHtml);
        Assert.DoesNotContain("alpha-fixture.txt", inventoryHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("beta-fixture.txt", inventoryHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Inventory results in a table layout", inventoryHtml, StringComparison.Ordinal);
        Assert.Contains("Open file details", inventoryHtml);
        Assert.Contains("alpha-fixture.txt", detailsHtml);
        Assert.Contains("File details are loaded directly from the status table", detailsHtml);
    }

    private static async Task<HttpResponseMessage> PostDeleteAsync(
        HttpClient client,
        string fileId,
        string fileName,
        string detailsHtml,
        string returnUrl)
    {
        var antiforgery = ExtractHiddenInputValue(detailsHtml, "__RequestVerificationToken");
        return await client.PostAsync(
            "/Files/Details?handler=Delete",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", antiforgery),
                new("fileId", fileId),
                new("returnUrl", returnUrl),
                new("DeleteConfirmation", fileName)
            ]));
    }

    private static string ExtractHiddenInputValue(string html, string fieldName)
    {
        var marker = $"name=\"{fieldName}\"";
        var nameIndex = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Missing hidden field '{fieldName}'.");
        var valueMarker = "value=\"";
        var valueIndex = html.IndexOf(valueMarker, nameIndex, StringComparison.Ordinal);
        Assert.True(valueIndex >= 0, $"Missing value for hidden field '{fieldName}'.");
        valueIndex += valueMarker.Length;
        var valueEnd = html.IndexOf('"', valueIndex);
        Assert.True(valueEnd > valueIndex, $"Missing closing quote for hidden field '{fieldName}'.");
        return html[valueIndex..valueEnd];
    }
}
