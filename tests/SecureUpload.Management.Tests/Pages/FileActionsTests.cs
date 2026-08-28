using System.Net;
using System.Text;
using Azure;
using Microsoft.AspNetCore.WebUtilities;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;

namespace SecureUpload.Management.Tests.Pages;

public sealed class FileActionsTests
{
    private static readonly DateTimeOffset RequestTime = new(2026, 8, 14, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task DetailsPageRendersDownloadAndDeletionControlsWithAccessibleConfirmation()
    {
        var file = WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Available, "Quarterly report.pdf", 20),
            "\"table-1\"");
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            new ActionStatusStore(file),
            blobStore: new ActionBlobStore(file.TargetETag!, "clean bytes"u8.ToArray()));
        using var client = factory.CreateManagementClient();

        var html = await client.GetStringAsync(
            $"/Files/Details?fileId={file.StableId}&returnUrl=%2F%3Ffilter%3Davailable");

        Assert.Contains("Download clean file", html);
        Assert.Contains("Request permanent deletion", html);
        Assert.Contains("Type <span class=\"mono\">Quarterly report.pdf</span> to confirm permanent deletion", html);
        Assert.Contains("Deletion removes file content from storage and keeps only the audit tombstone.", html);
        Assert.Contains("data-prevent-double-submit=\"true\"", html);
        Assert.Contains("data-busy-text=\"Requesting deletion...\"", html);
        Assert.Contains(
            $"href=\"/Files/Details?fileId={file.StableId}&amp;returnUrl=%2F%3Ffilter%3Davailable\"",
            html);
    }

    [Fact]
    public async Task DownloadHandlerStreamsTheCleanBlobWithSafeAttachmentAndPrivateNoStoreHeaders()
    {
        var file = WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Available, "C:\\unsafe\\Quarterly\x0001.pdf", 21),
            "\"table-1\"");
        var bytes = "verified clean bytes"u8.ToArray();
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            new ActionStatusStore(file),
            blobStore: new ActionBlobStore(file.TargetETag!, bytes));
        using var client = factory.CreateManagementClient();

        var response = await client.GetAsync($"/Files/Details?handler=Download&fileId={file.StableId}&returnUrl=%2F");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.DispositionType ?? string.Empty);
        Assert.Contains("Quarterly.pdf", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("private", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsafe", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, body);
    }

    [Theory]
    [InlineData(ConditionalBlobReadDisposition.NotFound)]
    [InlineData(ConditionalBlobReadDisposition.ETagMismatch)]
    public async Task DownloadHandlerIntegrityFailuresRedirectBackBeforeSendingAttachmentHeaders(
        ConditionalBlobReadDisposition blobDisposition)
    {
        var file = WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Available, "report.pdf", 22),
            "\"table-1\"");
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            new ActionStatusStore(file),
            blobStore: new ActionBlobStore(file.TargetETag!, "ignored"u8.ToArray(), blobDisposition));
        using var client = factory.CreateManagementClient();

        var response = await client.GetAsync(
            $"/Files/Details?handler=Download&fileId={file.StableId}&returnUrl=%2F%3Ffilter%3Davailable");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("action=download-integrity", location, StringComparison.Ordinal);
        Assert.DoesNotContain("report.pdf", location, StringComparison.OrdinalIgnoreCase);

        var html = await client.GetStringAsync(location);
        Assert.Contains("Clean download is unavailable", html);
        Assert.Contains("could not be verified against clean storage", html);
    }

    [Fact]
    public async Task UnauthorizedCallersCannotDownloadOrRequestDeletion()
    {
        var file = WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Available, "report.pdf", 23),
            "\"table-1\"");
        var store = new ActionStatusStore(file);
        var blobs = new ActionBlobStore(file.TargetETag!, "clean bytes"u8.ToArray());

        await using var anonymousFactory = new ManagementWebApplicationFactory(
            null,
            store,
            blobStore: blobs);
        using var anonymousClient = anonymousFactory.CreateManagementClient();
        var anonymousDownload = await anonymousClient.GetAsync($"/Files/Details?handler=Download&fileId={file.StableId}");

        Assert.Equal(HttpStatusCode.Found, anonymousDownload.StatusCode);
        Assert.StartsWith("/.auth/login/aad", anonymousDownload.Headers.Location?.ToString() ?? string.Empty, StringComparison.Ordinal);

        await using var noRoleFactory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(roles: []),
            store,
            blobStore: blobs);
        using var noRoleClient = noRoleFactory.CreateManagementClient();
        var forbiddenDelete = await noRoleClient.PostAsync(
            $"/Files/Details?handler=Delete&fileId={file.StableId}",
            new FormUrlEncodedContent(
            [
                new("fileId", file.StableId),
                new("returnUrl", "/"),
                new("DeleteConfirmation", file.OriginalFileName)
            ]));
        var forbiddenBody = await forbiddenDelete.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);
        Assert.Equal(string.Empty, forbiddenBody);
        Assert.Equal(FileState.Available, store.Record!.State);
    }

    [Fact]
    public async Task DeletePostRequiresAnExactFilenameConfirmationAndPreservesTheCurrentPageState()
    {
        var file = WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Available, "report.pdf", 24),
            "\"table-1\"");
        var store = new ActionStatusStore(file);
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            store,
            blobStore: new ActionBlobStore(file.TargetETag!, "clean bytes"u8.ToArray()),
            timeProvider: new FixedTimeProvider(RequestTime));
        using var client = factory.CreateManagementClient();

        var page = await client.GetStringAsync($"/Files/Details?fileId={file.StableId}&returnUrl=%2F");
        var antiforgery = ExtractHiddenInputValue(page, "__RequestVerificationToken");

        var response = await client.PostAsync(
            "/Files/Details?handler=Delete",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", antiforgery),
                new("fileId", file.StableId),
                new("returnUrl", "/"),
                new("DeleteConfirmation", "wrong.pdf")
            ]));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Type the current file name exactly to confirm permanent deletion.", html);
        Assert.Contains("report.pdf", html);
        Assert.Equal(FileState.Available, store.Record!.State);
    }

    [Fact]
    public async Task DeletePostUsesTheAuthenticatedOidAndShowsBoundedProgressThenTheDeletedTombstone()
    {
        var file = WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Available, "report.pdf", 25),
            "\"table-1\"");
        var store = new ActionStatusStore(file);
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            store,
            blobStore: new ActionBlobStore(file.TargetETag!, "clean bytes"u8.ToArray()),
            timeProvider: new FixedTimeProvider(RequestTime));
        using var client = factory.CreateManagementClient();

        var page = await client.GetStringAsync($"/Files/Details?fileId={file.StableId}&returnUrl=%2F");
        var antiforgery = ExtractHiddenInputValue(page, "__RequestVerificationToken");
        var response = await client.PostAsync(
            "/Files/Details?handler=Delete",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", antiforgery),
                new("fileId", file.StableId),
                new("returnUrl", "/"),
                new("DeleteConfirmation", file.OriginalFileName),
                new("DeletedBy", "malicious-form-value")
            ]));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(FileState.Deleting, store.Record!.State);
        Assert.Equal(ManagementWebApplicationFactory.ObjectId, store.Record.DeletedBy);
        Assert.Equal(RequestTime, store.Record.DeletionRequestedAt);

        var progressHtml = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains("Deletion requested", progressHtml);
        Assert.Contains("http-equiv=\"refresh\"", progressHtml);
        Assert.Contains("Refresh now", progressHtml);
        Assert.Contains("tabindex=\"-1\"", progressHtml);
        Assert.Contains("data-focus-target=\"true\"", progressHtml);
        Assert.DoesNotContain("Request permanent deletion", progressHtml);

        store.Overwrite(WithStoreETag(
            FileStateMachine.Transition(
                store.Record,
                FileTransition.DeleteCompleted(RequestTime.AddMinutes(1))).Record,
            "\"table-2\""));

        var deletedHtml = await client.GetStringAsync(
            $"/Files/Details?fileId={file.StableId}&returnUrl=%2F&action=delete-complete");

        Assert.Contains("Deletion completed", deletedHtml);
        Assert.Contains("Deleted by object ID", deletedHtml);
        Assert.Contains(ManagementWebApplicationFactory.ObjectId, deletedHtml);
        Assert.DoesNotContain("http-equiv=\"refresh\"", deletedHtml);
    }

    [Fact]
    public async Task DeletingPageShowsAStuckStateAfterTheBoundedRefreshWindow()
    {
        var file = WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Deleting, "report.pdf", 26),
            "\"table-1\"");
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            new ActionStatusStore(file),
            blobStore: new ActionBlobStore("\"clean-v1\"", "clean bytes"u8.ToArray()));
        using var client = factory.CreateManagementClient();

        var html = await client.GetStringAsync(
            $"/Files/Details?fileId={file.StableId}&returnUrl=%2F&action=delete-in-progress&refresh=5");

        Assert.Contains("Deletion is taking longer than expected", html);
        Assert.Contains("Refresh now", html);
        Assert.DoesNotContain("http-equiv=\"refresh\"", html);
    }

    [Fact]
    public async Task DeletePostWithoutAntiforgeryTokenFails()
    {
        var file = WithStoreETag(
            ManagementFileTestData.CreateRecord(FileState.Available, "report.pdf", 27),
            "\"table-1\"");
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            new ActionStatusStore(file),
            blobStore: new ActionBlobStore(file.TargetETag!, "clean bytes"u8.ToArray()));
        using var client = factory.CreateManagementClient();

        var response = await client.PostAsync(
            "/Files/Details?handler=Delete",
            new FormUrlEncodedContent(
            [
                new("fileId", file.StableId),
                new("returnUrl", "/"),
                new("DeleteConfirmation", file.OriginalFileName)
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DetailsPageShowsTheExplicitNotFoundState()
    {
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(),
            new ActionStatusStore(null),
            blobStore: new ActionBlobStore("\"clean-v1\"", "clean bytes"u8.ToArray()));
        using var client = factory.CreateManagementClient();

        var html = await client.GetStringAsync(
            $"/Files/Details?fileId={ManagementFileTestData.CreateStableId(999)}&returnUrl=%2F");

        Assert.Contains("File not found", html);
        Assert.Contains("No retained status row matched the requested file ID.", html);
    }

    private static FileRecord WithStoreETag(FileRecord record, string eTag) =>
        ManagementFileTestData.WithStoreETag(record, eTag);

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

    private sealed class ActionStatusStore(FileRecord? record) : IFileStatusStore
    {
        private int _version = 1;

        public FileRecord? Record { get; private set; } = record;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<StatusWriteResult> CreateAsync(
            FileRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileRecord?> GetAsync(
            string stableId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Record);

        public Task<StatusWriteResult> UpdateAsync(
            FileRecord record,
            ETag expectedETag,
            CancellationToken cancellationToken = default)
        {
            if (Record is null)
            {
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.NotFound));
            }

            if (Record.StoreETag != expectedETag)
            {
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
            }

            Record = WithStoreETag(record, $"\"table-{Interlocked.Increment(ref _version)}\"");
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.Succeeded, Record));
        }

        public void Overwrite(FileRecord record) => Record = record;

        public async IAsyncEnumerable<FileRecord> QueryAsync(
            FileStatusQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Record is not null)
            {
                yield return Record;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class ActionBlobStore(
        string expectedETag,
        byte[] cleanBytes,
        ConditionalBlobReadDisposition readDisposition = ConditionalBlobReadDisposition.Succeeded)
        : IBlobFileStore
    {
        public Task<BlobWriteResult> UploadPendingAsync(
            string stableId,
            Stream content,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BlobWriteResult?> GetPropertiesAsync(
            string stableId,
            BlobArea area,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConditionalBlobReadResult> OpenCleanReadIfMatchAsync(
            string stableId,
            ETag expectedETagValue,
            CancellationToken cancellationToken = default)
        {
            if (readDisposition == ConditionalBlobReadDisposition.NotFound)
            {
                return Task.FromResult(new ConditionalBlobReadResult(ConditionalBlobReadDisposition.NotFound));
            }

            if (readDisposition == ConditionalBlobReadDisposition.ETagMismatch ||
                !StringComparer.Ordinal.Equals(expectedETag, expectedETagValue.ToString()))
            {
                return Task.FromResult(new ConditionalBlobReadResult(ConditionalBlobReadDisposition.ETagMismatch));
            }

            return Task.FromResult(new ConditionalBlobReadResult(
                ConditionalBlobReadDisposition.Succeeded,
                new BlobReadResult(
                    new MemoryStream(cleanBytes, writable: false),
                    new ETag(expectedETag))));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
