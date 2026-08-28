using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;

namespace SecureUpload.Web.Tests.Uploads;

public sealed class UploadEndpointTests
{
    [Fact]
    public async Task StreamsOneAllowedFileAndReturnsPendingStableId()
    {
        await using var factory = new UploadFactory();
        using var content = OneFile("report.PDF", "Application/Pdf", new byte[32]);

        var response = await factory.Client.PostAsync("/api/uploads", content);
        var result = await response.Content.ReadFromJsonAsync<UploadResponse>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(result);
        Assert.Matches("^[0-9a-f]{64}$", result.FileId);
        Assert.Equal("pending", result.Status);
        Assert.Single(factory.Blobs.Pending);
        Assert.Equal(FileState.Pending, Assert.Single(factory.Statuses.Records).State);
    }

    [Theory]
    [InlineData("empty.pdf", "application/pdf", 0, HttpStatusCode.BadRequest)]
    [InlineData("bad.exe", "application/pdf", 1, HttpStatusCode.UnsupportedMediaType)]
    [InlineData("bad.pdf", "application/x-msdownload", 1, HttpStatusCode.UnsupportedMediaType)]
    public async Task RejectsInvalidFiles(
        string fileName,
        string mediaType,
        int size,
        HttpStatusCode expected)
    {
        await using var factory = new UploadFactory();
        using var content = OneFile(fileName, mediaType, new byte[size]);

        var response = await factory.Client.PostAsync("/api/uploads", content);

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(factory.Blobs.Pending);
    }

    [Fact]
    public async Task AcceptsExactlyAtLimitAndRejectsActualBytesOverLimit()
    {
        await using var factory = new UploadFactory(maximumBytes: 8);
        using var exact = OneFile("a.txt", "text/plain", new byte[8]);
        using var over = OneFile("b.txt", "text/plain", new byte[9]);

        Assert.Equal(HttpStatusCode.Accepted, (await factory.Client.PostAsync("/api/uploads", exact)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await factory.Client.PostAsync("/api/uploads", over)).StatusCode);
        Assert.Single(factory.Blobs.Pending);
        Assert.Equal(FileState.UploadFailed, factory.Statuses.Records.Single(record => record.OriginalFileName == "b.txt").State);
    }

    [Fact]
    public async Task RejectsMissingOrSecondFileWithoutWritingBlob()
    {
        await using var factory = new UploadFactory();
        using var missing = new MultipartFormDataContent();
        missing.Add(new StringContent("value"), "note");
        using var two = OneFile("a.txt", "text/plain", [1]);
        two.Add(new ByteArrayContent([2]), "file2", "b.txt");
        two.Last().Headers.ContentType = new("text/plain");

        Assert.Equal(HttpStatusCode.BadRequest, (await factory.Client.PostAsync("/api/uploads", missing)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.Client.PostAsync("/api/uploads", two)).StatusCode);
        Assert.Empty(factory.Blobs.Pending);
    }

    [Fact]
    public async Task BlobFailureCleansPartialAndMarksUploadFailed()
    {
        await using var factory = new UploadFactory();
        factory.Blobs.FailUpload = true;
        using var content = OneFile("a.txt", "text/plain", new byte[16]);

        var response = await factory.Client.PostAsync("/api/uploads", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(factory.Blobs.Pending);
        Assert.Equal(1, factory.Blobs.DeleteAttempts);
        Assert.Equal(FileState.UploadFailed, Assert.Single(factory.Statuses.Records).State);
    }

    [Fact]
    public async Task CleanupFailureStillReturnsSafeErrorAndMarksStatusFailed()
    {
        await using var factory = new UploadFactory();
        factory.Blobs.FailUpload = true;
        factory.Blobs.FailDelete = true;
        using var content = OneFile("a.txt", "text/plain", new byte[16]);

        var response = await factory.Client.PostAsync("/api/uploads", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(FileState.UploadFailed, Assert.Single(factory.Statuses.Records).State);
    }

    [Fact]
    public async Task StatusCreationFailurePreventsBlobWrite()
    {
        await using var factory = new UploadFactory();
        factory.Statuses.FailCreate = true;
        using var content = OneFile("a.txt", "text/plain", [1]);

        var response = await factory.Client.PostAsync("/api/uploads", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, factory.Blobs.UploadAttempts);
    }

    [Fact]
    public async Task FinalizationConflictFailsClosedAndDoesNotReturnFileId()
    {
        await using var factory = new UploadFactory();
        factory.Statuses.FailFinalize = true;
        using var content = OneFile("a.txt", "text/plain", [1]);

        var response = await factory.Client.PostAsync("/api/uploads", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(Assert.Single(factory.Statuses.Records).StableId, body);
        Assert.Empty(factory.Blobs.Pending);
        Assert.Equal(FileState.UploadFailed, Assert.Single(factory.Statuses.Records).State);
    }

    [Fact]
    public async Task CleanupReconcilesStatusConcurrencyConflictAndMarksUploadFailed()
    {
        await using var factory = new UploadFactory();
        factory.Statuses.FailFinalize = true;
        factory.Statuses.CleanupConflictsRemaining = 1;
        using var content = OneFile("a.txt", "text/plain", [1]);

        var response = await factory.Client.PostAsync("/api/uploads", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(factory.Blobs.Pending);
        Assert.Equal(FileState.UploadFailed, Assert.Single(factory.Statuses.Records).State);
    }

    [Fact]
    public async Task FinalizationExceptionRetainsCommittedPendingBlobForRecovery()
    {
        await using var factory = new UploadFactory();
        factory.Statuses.ThrowFinalize = true;
        using var content = OneFile("a.txt", "text/plain", [1]);

        var response = await factory.Client.PostAsync("/api/uploads", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(factory.Blobs.Pending);
        Assert.Equal(FileState.Uploading, Assert.Single(factory.Statuses.Records).State);
    }

    [Fact]
    public async Task FinalizationRaceReconcilesWithoutRegressingProcessingState()
    {
        await using var factory = new UploadFactory();
        factory.Statuses.RaceFinalizeWithCleanScan = true;
        using var content = OneFile("a.txt", "text/plain", [1]);

        var response = await factory.Client.PostAsync("/api/uploads", content);
        var result = await response.Content.ReadFromJsonAsync<UploadResponse>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("pending", result!.Status);
        Assert.Equal(FileState.Promoting, Assert.Single(factory.Statuses.Records).State);
    }

    [Fact]
    public async Task KillSwitchRejectsBeforeStateOrBlobCreation()
    {
        await using var factory = new UploadFactory(admissionEnabled: false);
        using var content = OneFile("a.txt", "text/plain", [1]);

        var response = await factory.Client.PostAsync("/api/uploads", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(factory.Statuses.Records);
        Assert.Equal(0, factory.Blobs.UploadAttempts);
    }

    [Fact]
    public async Task PerIpLimitRejectsBeforeSecondBlobWrite()
    {
        await using var factory = new UploadFactory(requestsPerIp: 1);
        using var first = OneFile("a.txt", "text/plain", [1]);
        using var second = OneFile("b.txt", "text/plain", [2]);

        Assert.Equal(HttpStatusCode.Accepted, (await factory.Client.PostAsync("/api/uploads", first)).StatusCode);
        var limited = await factory.Client.PostAsync("/api/uploads", second);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(1, factory.Blobs.UploadAttempts);
    }

    [Fact]
    public async Task OriginalPathOrMarkupIsNeverBlobNameOrReflected()
    {
        await using var factory = new UploadFactory();
        using var content = OneFile(@"C:\fakepath\<img src=x>.txt", "text/plain", [1]);

        var response = await factory.Client.PostAsync("/api/uploads", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.DoesNotContain("img", body, StringComparison.OrdinalIgnoreCase);
        Assert.All(factory.Blobs.Pending.Keys, id => Assert.Matches("^[0-9a-f]{64}$", id));
    }

    [Fact]
    public async Task UnicodeOriginalNameIsEncodedForBlobMetadata()
    {
        await using var factory = new UploadFactory();
        using var content = OneFile("résumé.txt", "text/plain", [1]);

        var response = await factory.Client.PostAsync("/api/uploads", content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var encoded = factory.Blobs.LastMetadata!["originalFileNameBase64"];
        Assert.Equal("résumé.txt", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.DoesNotContain("originalFileName", factory.Blobs.LastMetadata.Keys);
    }

    private static MultipartFormDataContent OneFile(
        string fileName,
        string mediaType,
        byte[] bytes)
    {
        var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new(mediaType);
        multipart.Add(file, "file", fileName);
        return multipart;
    }

    private sealed record UploadResponse(string FileId, string Status);

    private sealed class UploadFactory : WebApplicationFactory<Program>
    {
        private readonly long _maximumBytes;

        public UploadFactory(
            long maximumBytes = 1024,
            bool admissionEnabled = true,
            int requestsPerIp = 10)
        {
            _maximumBytes = maximumBytes;
            AdmissionEnabled = admissionEnabled;
            RequestsPerIp = requestsPerIp;
            Client = CreateClient();
        }

        public HttpClient Client { get; }
        public RecordingBlobStore Blobs { get; } = new();
        public InMemoryStatusStore Statuses { get; } = new();
        private bool AdmissionEnabled { get; }
        private int RequestsPerIp { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBlobFileStore>();
                services.RemoveAll<IFileStatusStore>();
                services.RemoveAll<SecureUpload.Web.Security.IUploadAdmissionStore>();
                services.AddSingleton<IBlobFileStore>(Blobs);
                services.AddSingleton<IFileStatusStore>(Statuses);
                services.AddSingleton<SecureUpload.Web.Security.IUploadAdmissionStore>(
                    new InMemoryUploadAdmissionStore());
                services.RemoveAll<IOptions<FilePolicyOptions>>();
                services.AddSingleton<IOptions<FilePolicyOptions>>(
                    Options.Create(new FilePolicyOptions { MaximumFileSizeBytes = _maximumBytes }));
                services.RemoveAll<IOptions<SecureUpload.Web.Security.UploadAdmissionOptions>>();
                services.AddSingleton<IOptions<SecureUpload.Web.Security.UploadAdmissionOptions>>(
                    Options.Create(new SecureUpload.Web.Security.UploadAdmissionOptions
                    {
                        Enabled = AdmissionEnabled,
                        MaximumConcurrentUploads = 4,
                        RequestsPerWindow = 100,
                        BytesPerWindow = 1024 * 1024
                    }));
                services.RemoveAll<IOptions<SecureUpload.Web.Security.UploadRateLimitOptions>>();
                services.AddSingleton<IOptions<SecureUpload.Web.Security.UploadRateLimitOptions>>(
                    Options.Create(new SecureUpload.Web.Security.UploadRateLimitOptions
                    {
                        RequestsPerIpPerWindow = RequestsPerIp
                    }));
                services.Configure<SecureUpload.Web.Security.AllowedOriginOptions>(
                    options => options.Origins = ["https://host.example"]);
            });
        }
    }
}
