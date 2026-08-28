using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Management.Tests;

namespace SecureUpload.Management.Tests.Pages;

public sealed class InventoryPageTests
{
    [Fact]
    public async Task IndexPageRendersFiltersPagingAndReturnLinks()
    {
        var store = new PageStatusStore(
        [
            CreateRecord(FileState.Available, "report-two.pdf", 2),
            CreateRecord(FileState.Available, "report-one.pdf", 1)
        ]);
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            store);
        using var client = factory.CreateManagementClient();

        var html = await client.GetStringAsync("/?search=report&filter=available&page=2&pageSize=1");

        Assert.Contains("value=\"report\"", html);
        Assert.Contains("<option value=\"available\" selected=\"selected\">Available</option>", html);
        Assert.Contains("Page 2 of 2", html);
        Assert.Contains("href=\"/Files/Details?fileId=0000000000000000000000000000000000000000000000000000000000000001&amp;returnUrl=%2F%3Fsearch%3Dreport%26filter%3Davailable%26page%3D2%26pageSize%3D1\"", html);
        Assert.Contains("Previous page", html);
    }

    [Fact]
    public async Task CapacityFailureStillAllowsExactIdLookup()
    {
        var overCapacity = new PageStatusStore(
        [
            CreateRecord(FileState.Available, "alpha.pdf", 1),
            CreateRecord(FileState.Pending, "beta.pdf", 2)
        ]);
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            overCapacity,
            inventoryCapacity: 1);
        using var client = factory.CreateManagementClient();

        var indexHtml = await client.GetStringAsync("/");
        var detailsHtml = await client.GetStringAsync(
            "/Files/Details?fileId=0000000000000000000000000000000000000000000000000000000000000001");

        Assert.Contains("Inventory browsing is paused", indexHtml);
        Assert.DoesNotContain("alpha.pdf", indexHtml, StringComparison.Ordinal);
        Assert.Contains("Open file details", indexHtml);
        Assert.Contains("alpha.pdf", detailsHtml);
        Assert.Contains("File details are loaded directly from the status table", detailsHtml);
    }

    [Fact]
    public async Task DetailsPageUsesSafeReturnLinkAndShowsDeletingState()
    {
        var deleting = CreateDeletingRecord("delete-me.pdf", 4);
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            new PageStatusStore([deleting]));
        using var client = factory.CreateManagementClient();

        var html = await client.GetStringAsync(
            $"/Files/Details?fileId={deleting.StableId}&returnUrl=https%3A%2F%2Fevil.example%2Fphish");

        Assert.Contains("href=\"/\"", html);
        Assert.DoesNotContain("evil.example", html, StringComparison.Ordinal);
        Assert.Contains("Deleting", html);
        Assert.Contains("Removal in progress", html);
        Assert.Contains("Deleted by object ID", html);
    }

    private static FileRecord CreateRecord(FileState state, string fileName, int seed) =>
        ManagementFileTestData.CreateRecord(state, fileName, seed);

    private static FileRecord CreateDeletingRecord(string fileName, int seed) =>
        ManagementFileTestData.CreateRecord(FileState.Deleting, fileName, seed);

    private sealed class PageStatusStore(IEnumerable<FileRecord> records) : IFileStatusStore
    {
        private readonly IReadOnlyDictionary<string, FileRecord> _records =
            records.ToDictionary(record => record.StableId, record => record, StringComparer.Ordinal);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<StatusWriteResult> CreateAsync(FileRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StatusWriteResult> UpdateAsync(FileRecord record, Azure.ETag expectedETag, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FileRecord?> GetAsync(string stableId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_records.GetValueOrDefault(stableId));

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
    }
}
