using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Management.Tests;

namespace SecureUpload.Management.Tests.Accessibility;

public sealed class InventoryAccessibilityTests
{
    [Fact]
    public async Task InventoryPagesExposeAccessibleTableCardsAndLiveRegions()
    {
        var records = new AccessibilityStatusStore(
        [
            CreateRecord(FileState.Available, "accessible-report.pdf", 1),
            CreateRecord(FileState.Rejected, "malicious.txt", 2)
        ]);
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            records);
        using var client = factory.CreateManagementClient();

        var indexHtml = await client.GetStringAsync("/?pageSize=1");
        var detailsHtml = await client.GetStringAsync(
            "/Files/Details?fileId=0000000000000000000000000000000000000000000000000000000000000001");
        var css = await client.GetStringAsync("/css/site.css");

        Assert.Contains("<table", indexHtml);
        Assert.Contains("scope=\"col\"", indexHtml);
        Assert.Contains("role=\"status\"", indexHtml);
        Assert.Contains("aria-live=\"polite\"", indexHtml);
        Assert.Contains("Inventory results in a small-screen layout", indexHtml);
        Assert.Contains("aria-label=\"Inventory pages\"", indexHtml);
        Assert.Contains("Logical destination", indexHtml);
        Assert.Contains("File ID", detailsHtml);
        Assert.Contains("Current state", detailsHtml);
        Assert.Contains("@media (max-width: 48rem)", css);
        Assert.Contains(".inventory-cards", css);
    }

    private static FileRecord CreateRecord(FileState state, string fileName, int seed) =>
        ManagementFileTestData.CreateRecord(state, fileName, seed);

    private sealed class AccessibilityStatusStore(IEnumerable<FileRecord> records) : IFileStatusStore
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
