extern alias management;
extern alias web;

using System.Globalization;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Scanning;
using ManagementAuthorization = management::SecureUpload.Management.Security.ManagementAuthorization;
using ManagementAuthorizationOptions = management::SecureUpload.Management.Security.ManagementAuthorizationOptions;
using ManagementTelemetry = management::SecureUpload.Management.Telemetry.ManagementTelemetry;

namespace SecureUpload.EndToEnd.Tests;

internal sealed class EndToEndTestHost : WebApplicationFactory<web::Program>
{
    public const string ManagementTenantId = "11111111-1111-1111-1111-111111111111";
    public const string ManagementRequiredRole = "SecureUpload.Management";
    public const string PrimaryManagementObjectId = "22222222-2222-2222-2222-222222222222";
    public const string SecondaryManagementObjectId = "33333333-3333-3333-3333-333333333333";

    private readonly ManagementApplicationFactory _managementFactory;

    public EndToEndTestHost(int managementInventoryCapacity = 10_000)
    {
        Client = CreateClient();
        _managementFactory = new ManagementApplicationFactory(Statuses, Blobs, managementInventoryCapacity);
    }

    public HttpClient Client { get; }
    public DeterministicStatusStore Statuses { get; } = new();
    public DeterministicBlobStore Blobs { get; } = new();

    public HttpClient CreateManagementClient(
        ManagementTestPrincipal? principal = null,
        bool allowAutoRedirect = false)
    {
        var client = _managementFactory.CreateManagementClient(allowAutoRedirect);
        if (principal is not null)
        {
            client.DefaultRequestHeaders.Add(
                ManagementApplicationFactory.TestPrincipalHeader,
                principal.ToHeaderValue());
        }

        return client;
    }

    public DeletionProcessor CreateDeletionProcessor(int maximumAttempts = 5) =>
        new(
            Statuses,
            new FileDeletionCleanup(Blobs, maximumAttempts),
            CreateOptions(maximumAttempts));

    public ScanResultProcessor CreateProcessor(int maximumAttempts = 5) =>
        CreateProcessor(
            CreateOptions(maximumAttempts));

    public ScanResultProcessor CreateProcessor(ScanProcessorOptions options) =>
        new(
            Statuses,
            new BlobPromotionService(Blobs),
            new DeletionProcessor(
                Statuses,
                new FileDeletionCleanup(Blobs, options.MaximumConcurrencyAttempts),
                options),
            options);

    public async Task<UploadedFile> UploadAsync(
        byte[]? content = null,
        string fileName = "fixture.txt",
        string mediaType = "text/plain")
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(content ?? "benign fixture"u8.ToArray());
        file.Headers.ContentType = new(mediaType);
        multipart.Add(file, "file", fileName);
        var response = await Client.PostAsync("/api/uploads", multipart);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadedFile>())!;
    }

    public MalwareScanEvent EventFor(string stableId, MalwareScanOutcome outcome, string eventId = "event-1")
    {
        var pending = Blobs.Get(stableId, BlobArea.Pending)!;
        return new(
            eventId,
            $"correlation-{eventId}",
            stableId,
            pending.BlobUri,
            pending.ETag,
            DateTimeOffset.UtcNow,
            outcome,
            outcome == MalwareScanOutcome.ScanError ? "scan-error" : null);
    }

    public new async ValueTask DisposeAsync()
    {
        _managementFactory.Dispose();
        Client.Dispose();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("AllowedOrigins:Origins:0", "https://host.example");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileStatusStore>();
            services.RemoveAll<IBlobFileStore>();
            services.RemoveAll<web::SecureUpload.Web.Security.IUploadAdmissionStore>();
            services.AddSingleton<IFileStatusStore>(Statuses);
            services.AddSingleton<IBlobFileStore>(Blobs);
            services.AddSingleton<web::SecureUpload.Web.Security.IUploadAdmissionStore>(
                new AlwaysAllowAdmissionStore());
        });
    }

    private static ScanProcessorOptions CreateOptions(int maximumAttempts) =>
        new()
        {
            ExpectedTopic = "test-topic",
            BlobServiceUri = DeterministicBlobStore.ServiceUri,
            MaximumConcurrencyAttempts = maximumAttempts
        };

    private sealed class AlwaysAllowAdmissionStore
        : web::SecureUpload.Web.Security.IUploadAdmissionStore
    {
        public Task<web::SecureUpload.Web.Security.UploadAdmissionStoreResult> TryReserveAsync(
            long bytes,
            web::SecureUpload.Web.Security.UploadAdmissionBudget budget,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                web::SecureUpload.Web.Security.UploadAdmissionStoreResult.Acquired(
                    Guid.NewGuid().ToString("N")));

        public Task CompleteAsync(
            string reservationId,
            bool uploadCommitted,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ManagementApplicationFactory(
        IFileStatusStore statusStore,
        IBlobFileStore blobStore,
        int inventoryCapacity)
        : WebApplicationFactory<management::Program>
    {
        public const string TestPrincipalHeader = "X-E2E-Management-Principal";

        public HttpClient CreateManagementClient(bool allowAutoRedirect) =>
            CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = allowAutoRedirect,
                BaseAddress = new Uri("https://localhost")
            });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ManagementAuthorization:TenantId", ManagementTenantId);
            builder.UseSetting("ManagementAuthorization:RequiredRole", ManagementRequiredRole);
            builder.UseSetting("Storage:BlobServiceUri", DeterministicBlobStore.ServiceUri.ToString());
            builder.UseSetting("Storage:TableServiceUri", "https://secureuploads.table.core.windows.net");
            builder.UseSetting("Storage:StatusTableName", "filestatus");
            builder.UseSetting("Inventory:Capacity", inventoryCapacity.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Inventory:DefaultPageSize", "25");
            builder.UseSetting("Inventory:MaximumPageSize", "100");
            builder.UseSetting("Inventory:MaximumSearchLength", "255");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ManagementTelemetry>();
                services.AddSingleton(new ManagementTelemetry(NullLogger<ManagementTelemetry>.Instance));
                services.RemoveAll<IFileStatusStore>();
                services.RemoveAll<IBlobFileStore>();
                services.AddSingleton(statusStore);
                services.AddSingleton(blobStore);
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = ManagementTestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = ManagementTestAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = ManagementTestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ManagementTestAuthenticationHandler>(
                        ManagementTestAuthenticationHandler.SchemeName,
                        _ => { });
            });
        }
    }

    private sealed class ManagementTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<ManagementAuthorizationOptions> authorizationOptions)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "management-e2e";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(ManagementApplicationFactory.TestPrincipalHeader, out var header) ||
                header.Count == 0 ||
                string.IsNullOrWhiteSpace(header[0]))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            return ManagementTestPrincipal.TryParse(header[0], out var principal)
                ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)))
                : Task.FromResult(AuthenticateResult.Fail("Malformed management test principal header."));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            var returnUrl = ManagementAuthorization.BuildCurrentLocalPath(Request);
            var target = QueryHelpers.AddQueryString(
                authorizationOptions.Value.LoginPath,
                "post_login_redirect_uri",
                returnUrl);

            Response.StatusCode = StatusCodes.Status302Found;
            Response.Headers.Location = target;
            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
    }
}

internal sealed record UploadedFile(string FileId, string Status);

internal sealed record ManagementTestPrincipal(
    string TenantId,
    string ObjectId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string? IdentityType = null)
{
    public static ManagementTestPrincipal Primary { get; } = new(
        EndToEndTestHost.ManagementTenantId,
        EndToEndTestHost.PrimaryManagementObjectId,
        "primary-admin@example.com",
        [EndToEndTestHost.ManagementRequiredRole]);

    public static ManagementTestPrincipal Secondary { get; } = new(
        EndToEndTestHost.ManagementTenantId,
        EndToEndTestHost.SecondaryManagementObjectId,
        "secondary-admin@example.com",
        [EndToEndTestHost.ManagementRequiredRole]);

    public static ManagementTestPrincipal WrongTenant { get; } = new(
        "44444444-4444-4444-4444-444444444444",
        "55555555-5555-5555-5555-555555555555",
        "wrong-tenant@example.com",
        [EndToEndTestHost.ManagementRequiredRole]);

    public static ManagementTestPrincipal NoRole { get; } = new(
        EndToEndTestHost.ManagementTenantId,
        "66666666-6666-6666-6666-666666666666",
        "no-role@example.com",
        []);

    public string ToHeaderValue()
    {
        var payload = JsonSerializer.Serialize(new PrincipalEnvelope
        {
            TenantId = TenantId,
            ObjectId = ObjectId,
            DisplayName = DisplayName,
            Roles = [.. Roles],
            IdentityType = IdentityType
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    public static bool TryParse(string? headerValue, out ClaimsPrincipal principal)
    {
        principal = new ClaimsPrincipal();
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        PrincipalEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<PrincipalEnvelope>(
                Convert.FromBase64String(headerValue));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        if (envelope is null ||
            string.IsNullOrWhiteSpace(envelope.TenantId) ||
            string.IsNullOrWhiteSpace(envelope.ObjectId))
        {
            return false;
        }

        var claims = new List<Claim>
        {
            new("tid", envelope.TenantId),
            new("oid", envelope.ObjectId)
        };

        if (!string.IsNullOrWhiteSpace(envelope.DisplayName))
        {
            claims.Add(new Claim(ClaimTypes.Name, envelope.DisplayName));
        }

        if (!string.IsNullOrWhiteSpace(envelope.IdentityType))
        {
            claims.Add(new Claim("idtyp", envelope.IdentityType));
        }

        if (envelope.Roles is not null)
        {
            claims.AddRange(envelope.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        }

        principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "aad", ClaimTypes.Name, ClaimTypes.Role));
        return true;
    }

    private sealed class PrincipalEnvelope
    {
        public string TenantId { get; init; } = string.Empty;
        public string ObjectId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string[]? Roles { get; init; }
        public string? IdentityType { get; init; }
    }
}

internal sealed class DeterministicStatusStore : IFileStatusStore
{
    private readonly Dictionary<string, FileRecord> _records = [];
    private readonly object _gate = new();
    private int _version;

    public int ConflictsRemaining { get; set; }
    public Action<FileRecord>? BeforeUpdate { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<StatusWriteResult> CreateAsync(FileRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_records.ContainsKey(record.StableId))
            {
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.AlreadyExists));
            }

            var stored = WithStoreETag(record, NextETag());
            _records.Add(record.StableId, stored);
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.Succeeded, stored));
        }
    }

    public Task<FileRecord?> GetAsync(string stableId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_records.GetValueOrDefault(stableId));
        }
    }

    public Task<StatusWriteResult> UpdateAsync(
        FileRecord record,
        ETag expectedETag,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            BeforeUpdate?.Invoke(record);
            if (ConflictsRemaining > 0)
            {
                ConflictsRemaining--;
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
            }

            if (!_records.TryGetValue(record.StableId, out var current))
            {
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.NotFound));
            }

            if (current.StoreETag != expectedETag)
            {
                return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.ConcurrencyConflict));
            }

            var stored = WithStoreETag(record, NextETag());
            _records[record.StableId] = stored;
            return Task.FromResult(new StatusWriteResult(StatusWriteDisposition.Succeeded, stored));
        }
    }

    public async IAsyncEnumerable<FileRecord> QueryAsync(
        FileStatusQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        FileRecord[] records;
        lock (_gate)
        {
            records = _records.Values.ToArray();
        }

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((query.State is null || record.State == query.State) &&
                (query.UpdatedBefore is null || record.UpdatedAt < query.UpdatedBefore))
            {
                yield return record;
            }

            await Task.Yield();
        }
    }

    public FileRecord Required(string stableId) =>
        GetAsync(stableId).GetAwaiter().GetResult() ?? throw new InvalidOperationException("Missing record.");

    public void Overwrite(string stableId, FileRecord record, ETag eTag)
    {
        lock (_gate)
        {
            _records[stableId] = WithStoreETag(record, eTag);
        }
    }

    private ETag NextETag() => new($"\"table-{Interlocked.Increment(ref _version)}\"");

    internal static FileRecord WithStoreETag(FileRecord record, ETag etag)
    {
        var stored = record with { };
        typeof(FileRecord).GetProperty(nameof(FileRecord.StoreETag), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(stored, etag);
        return stored;
    }
}

internal sealed class DeterministicBlobStore : IBlobFileStore
{
    public static readonly Uri ServiceUri = new("https://secureuploads.blob.core.windows.net");
    private readonly Dictionary<(string StableId, BlobArea Area), StoredBlob> _blobs = [];
    private int _version;

    public BlobArea? FailNextDeleteArea { get; set; }
    public bool FailNextCopy { get; set; }
    public CopyPause? NextCopyPause { get; set; }

    public async Task<BlobWriteResult> UploadPendingAsync(
        string stableId,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        using var bytes = new MemoryStream();
        await content.CopyToAsync(bytes, cancellationToken);
        var stored = Put(stableId, BlobArea.Pending, bytes.ToArray());
        return stored.Result;
    }

    public async Task<BlobCopyResult> CopyPendingAsync(
        string stableId,
        BlobArea destination,
        ETag expectedSourceETag,
        CancellationToken cancellationToken = default)
    {
        if (FailNextCopy)
        {
            FailNextCopy = false;
            throw new RequestFailedException(503, "deterministic transient failure");
        }

        var source = Required(stableId, BlobArea.Pending);
        if (source.Result.ETag != expectedSourceETag)
        {
            throw new RequestFailedException(412, "source changed");
        }

        var target = Put(stableId, destination, source.Content);
        var pause = NextCopyPause;
        NextCopyPause = null;
        if (pause is not null)
        {
            pause.SignalCopied();
            await pause.WaitForReleaseAsync(cancellationToken);
        }

        return new BlobCopyResult(
            source.Result.BlobUri,
            target.Result.BlobUri,
            target.Result.ETag);
    }

    public Task<ConditionalBlobDeleteDisposition> DeleteIfMatchAsync(
        string stableId,
        BlobArea area,
        ETag expectedETag,
        CancellationToken cancellationToken = default)
    {
        if (FailNextDeleteArea == area)
        {
            FailNextDeleteArea = null;
            throw new RequestFailedException(503, "deterministic transient failure");
        }

        if (!_blobs.TryGetValue((stableId, area), out var current))
        {
            return Task.FromResult(ConditionalBlobDeleteDisposition.NotFound);
        }

        if (current.Result.ETag != expectedETag)
        {
            return Task.FromResult(ConditionalBlobDeleteDisposition.ETagMismatch);
        }

        _blobs.Remove((stableId, area));
        return Task.FromResult(ConditionalBlobDeleteDisposition.Deleted);
    }

    public Task<ConditionalBlobReadResult> OpenCleanReadIfMatchAsync(
        string stableId,
        ETag expectedETag,
        CancellationToken cancellationToken = default)
    {
        var clean = Get(stableId, BlobArea.Clean);
        if (clean is null)
        {
            return Task.FromResult(new ConditionalBlobReadResult(ConditionalBlobReadDisposition.NotFound));
        }

        if (clean.ETag != expectedETag)
        {
            return Task.FromResult(new ConditionalBlobReadResult(ConditionalBlobReadDisposition.ETagMismatch));
        }

        return Task.FromResult(
            new ConditionalBlobReadResult(
                ConditionalBlobReadDisposition.Succeeded,
                new BlobReadResult(
                    new MemoryStream(Read(stableId, BlobArea.Clean), writable: false),
                    clean.ETag)));
    }

    public Task<BlobWriteResult?> GetPropertiesAsync(
        string stableId,
        BlobArea area,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Get(stableId, area));

    public BlobWriteResult? Get(string stableId, BlobArea area) =>
        _blobs.GetValueOrDefault((stableId, area))?.Result;

    public byte[] Read(string stableId, BlobArea area) =>
        Required(stableId, area).Content.ToArray();

    public void DeleteAsHost(string stableId) =>
        _blobs.Remove((stableId, BlobArea.Clean));

    public CopyPause PauseNextCopy()
    {
        var pause = new CopyPause();
        NextCopyPause = pause;
        return pause;
    }

    private StoredBlob Put(string stableId, BlobArea area, byte[] content)
    {
        var etag = new ETag($"\"blob-{Interlocked.Increment(ref _version)}\"");
        var result = new BlobWriteResult(
            new Uri($"{ServiceUri}{area.ToString().ToLowerInvariant()}/{stableId}"),
            etag,
            content.LongLength);
        var stored = new StoredBlob(content.ToArray(), result);
        _blobs[(stableId, area)] = stored;
        return stored;
    }

    private StoredBlob Required(string stableId, BlobArea area) =>
        _blobs.GetValueOrDefault((stableId, area)) ??
        throw new RequestFailedException(404, "blob missing");

    private sealed record StoredBlob(byte[] Content, BlobWriteResult Result);
}

internal sealed class CopyPause
{
    private readonly TaskCompletionSource<bool> _copied =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForCopyAsync(CancellationToken cancellationToken = default) =>
        _copied.Task.WaitAsync(cancellationToken);

    public Task WaitForReleaseAsync(CancellationToken cancellationToken = default) =>
        _release.Task.WaitAsync(cancellationToken);

    public void SignalCopied() => _copied.TrySetResult(true);

    public void Release() => _release.TrySetResult(true);
}
