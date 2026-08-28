using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;

namespace SecureUpload.Web.Tests.Files;

public sealed class StatusEndpointTests
{
    [Theory]
    [InlineData(FileState.Uploading, "pending")]
    [InlineData(FileState.Pending, "pending")]
    [InlineData(FileState.Promoting, "pending")]
    [InlineData(FileState.Quarantining, "pending")]
    [InlineData(FileState.Available, "available")]
    [InlineData(FileState.Rejected, "rejected")]
    [InlineData(FileState.ScanError, "scan-error")]
    public async Task AllowedHostGetsOnlyPublicContract(FileState internalState, string expectedStatus)
    {
        await using var factory = new HostStatusFactory();
        var record = CreateRecord(internalState);
        await factory.Statuses.CreateAsync(record);

        using var request = factory.AuthorizedRequest($"/api/host/files/{record.StableId}/status");
        var response = await factory.Client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = document.RootElement;
        Assert.Equal(
            ["createdAt", "fileId", "fileName", "mediaType", "scanCompletedAt", "sizeBytes", "status", "updatedAt", "uploadedAt"],
            root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(record.StableId, root.GetProperty("fileId").GetString());
        Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
        Assert.Equal("report.pdf", root.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", root.GetProperty("mediaType").GetString());
        Assert.Equal(42, root.GetProperty("sizeBytes").GetInt64());

        var body = root.GetRawText();
        Assert.DoesNotContain("pendingBlobUri", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceETag", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("targetETag", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failureCode", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processingStartedAt", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("malware", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownAndMalformedIdsReturnSameNonSensitiveResponse()
    {
        await using var factory = new HostStatusFactory();

        using var unknownRequest = factory.AuthorizedRequest(
            $"/api/host/files/{new string('f', 64)}/status");
        using var malformedRequest = factory.AuthorizedRequest("/api/host/files/not-an-id/status");
        var unknown = await factory.Client.SendAsync(unknownRequest);
        var malformed = await factory.Client.SendAsync(malformedRequest);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await malformed.Content.ReadAsStringAsync());
        Assert.Equal(1, factory.Statuses.GetCalls);
    }

    [Fact]
    public async Task UploadFailureIsNotExposedAsAHostStatus()
    {
        await using var factory = new HostStatusFactory();
        var record = CreateRecord(FileState.UploadFailed);
        await factory.Statuses.CreateAsync(record);

        using var request = factory.AuthorizedRequest($"/api/host/files/{record.StableId}/status");
        var response = await factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(FileState.Deleting)]
    [InlineData(FileState.Deleted)]
    public async Task Deletion_states_are_hidden_from_polling_and_host_routes(FileState internalState)
    {
        await using var factory = new HostStatusFactory();
        var record = CreateRecord(internalState);
        await factory.Statuses.CreateAsync(record);

        var polling = await factory.Client.GetAsync($"/api/uploads/{record.StableId}/status");
        using var hostRequest = factory.AuthorizedRequest($"/api/host/files/{record.StableId}/status");
        var host = await factory.Client.SendAsync(hostRequest);
        var missingPolling = await factory.Client.GetAsync($"/api/uploads/{new string('f', 64)}/status");
        using var missingHostRequest = factory.AuthorizedRequest(
            $"/api/host/files/{new string('f', 64)}/status");
        var missingHost = await factory.Client.SendAsync(missingHostRequest);

        Assert.Equal(HttpStatusCode.NotFound, polling.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, host.StatusCode);
        Assert.Equal(
            await missingPolling.Content.ReadAsStringAsync(),
            await polling.Content.ReadAsStringAsync());
        Assert.Equal(
            await missingHost.Content.ReadAsStringAsync(),
            await host.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProcessorTransitionIsVisibleToIframeAndHostRoutes()
    {
        await using var factory = new HostStatusFactory();
        var record = CreateRecord(FileState.Available);
        await factory.Statuses.CreateAsync(record);

        var iframe = await factory.Client.GetFromJsonAsync<StatusResponse>(
            $"/api/uploads/{record.StableId}/status");
        using var hostRequest = factory.AuthorizedRequest($"/api/host/files/{record.StableId}/status");
        var host = await (await factory.Client.SendAsync(hostRequest))
            .Content.ReadFromJsonAsync<StatusResponse>();

        Assert.Equal("available", iframe!.Status);
        Assert.Equal(iframe.Status, host!.Status);
        Assert.Equal(iframe.FileId, host.FileId);
    }

    internal static FileRecord CreateRecord(FileState state)
    {
        var createdAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var record = FileRecord.CreateUploading(
            "report.pdf",
            "application/pdf",
            createdAt,
            new string('a', 64));
        Set(record, nameof(FileRecord.State), state);
        Set(record, nameof(FileRecord.UpdatedAt), createdAt.AddMinutes(4));
        Set(record, nameof(FileRecord.SizeBytes), 42L);
        Set(record, nameof(FileRecord.SourceETag), "\"source-secret\"");
        Set(record, nameof(FileRecord.TargetETag), "\"target-secret\"");
        Set(record, nameof(FileRecord.PendingBlobUri), new Uri("https://storage.test/pending/secret"));
        Set(record, nameof(FileRecord.UploadedAt), createdAt.AddMinutes(1));
        Set(record, nameof(FileRecord.ProcessingStartedAt), createdAt.AddMinutes(2));
        Set(record, nameof(FileRecord.ScanCompletedAt), createdAt.AddMinutes(3));
        Set(record, nameof(FileRecord.FailureCode), "malware-secret-detail");

        if (state is FileState.Deleting or FileState.Deleted)
        {
            Set(record, nameof(FileRecord.DeletionRequestedAt), createdAt.AddMinutes(5));
            Set(record, nameof(FileRecord.DeletedBy), "11111111-1111-1111-1111-111111111111");
        }

        if (state == FileState.Deleted)
        {
            Set(record, nameof(FileRecord.DeletedAt), createdAt.AddMinutes(6));
        }

        return record;
    }

    private static void Set<T>(FileRecord record, string propertyName, T value) =>
        typeof(FileRecord).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(record, value);

    private sealed record StatusResponse(string FileId, string Status);
}

internal sealed class HostStatusFactory : WebApplicationFactory<Program>
{
    internal const string TenantId = "11111111-1111-1111-1111-111111111111";
    internal const string Audience = "api://secure-upload";
    internal const string ClientId = "22222222-2222-2222-2222-222222222222";
    internal const string RequiredRole = "SecureUpload.Status.Read";
    private static readonly byte[] SigningKey =
        Encoding.UTF8.GetBytes("unit-test-signing-key-must-be-at-least-32-bytes-long");

    public HostStatusFactory()
    {
        Client = CreateClient();
    }

    public HttpClient Client { get; }
    public TrackingStatusStore Statuses { get; } = new();

    public HttpRequestMessage AuthorizedRequest(string path, TokenClaims? claims = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(claims ?? new TokenClaims()));
        return request;
    }

    public string CreateToken(TokenClaims claims)
    {
        var now = DateTimeOffset.UtcNow;
        var version = claims.Version;
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = claims.Issuer ?? VersionIssuer(version, claims.TenantId),
            ["aud"] = claims.Audience,
            ["tid"] = claims.TenantId,
            ["ver"] = version,
            ["idtyp"] = claims.IdentityType,
            ["roles"] = claims.Roles,
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = (claims.ExpiresAt ?? now.AddMinutes(10)).ToUnixTimeSeconds()
        };

        if (claims.Scope is not null)
        {
            payload["scp"] = claims.Scope;
        }

        payload[claims.ApplicationIdentityClaim ??
            (version == "1.0" ? "appid" : "azp")] = claims.ClientId;
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        using var hmac = new HMACSHA256(SigningKey);
        var signature = Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes($"{header}.{body}")));
        return $"{header}.{body}.{signature}";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("HostWorkloadAuthorization:TenantId", TenantId);
        builder.UseSetting("HostWorkloadAuthorization:Audience", Audience);
        builder.UseSetting("HostWorkloadAuthorization:AllowedClientApplicationId", ClientId);
        builder.UseSetting("HostWorkloadAuthorization:RequiredRole", RequiredRole);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileStatusStore>();
            services.AddSingleton<IFileStatusStore>(Statuses);
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options => options.TokenValidationParameters.IssuerSigningKey =
                    new SymmetricSecurityKey(SigningKey));
        });
    }

    private static string VersionIssuer(string version, string tenantId) =>
        version == "1.0"
            ? $"https://sts.windows.net/{tenantId}/"
            : $"https://login.microsoftonline.com/{tenantId}/v2.0";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record TokenClaims
{
    public string TenantId { get; init; } = HostStatusFactory.TenantId;
    public string Audience { get; init; } = HostStatusFactory.Audience;
    public string ClientId { get; init; } = HostStatusFactory.ClientId;
    public string[] Roles { get; init; } = [HostStatusFactory.RequiredRole];
    public string Version { get; init; } = "2.0";
    public string IdentityType { get; init; } = "app";
    public string? Scope { get; init; }
    public string? Issuer { get; init; }
    public string? ApplicationIdentityClaim { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

internal sealed class TrackingStatusStore : IFileStatusStore
{
    private readonly Dictionary<string, FileRecord> _records = [];

    public int GetCalls { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<StatusWriteResult> CreateAsync(
        FileRecord record,
        CancellationToken cancellationToken = default)
    {
        _records[record.StableId] = record;
        return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.Succeeded, record));
    }

    public Task<FileRecord?> GetAsync(
        string stableId,
        CancellationToken cancellationToken = default)
    {
        GetCalls++;
        return Task.FromResult(_records.GetValueOrDefault(stableId));
    }

    public Task<StatusWriteResult> UpdateAsync(
        FileRecord record,
        Azure.ETag expectedETag,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<FileRecord> QueryAsync(
        FileStatusQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
