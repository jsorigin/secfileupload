using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SecureUpload.Core.Storage;
using SecureUpload.Management.Telemetry;

namespace SecureUpload.Management.Tests;

internal sealed class ManagementWebApplicationFactory(
    ClaimsPrincipal? principal = null,
    IFileStatusStore? statusStore = null,
    int? inventoryCapacity = null,
    IBlobFileStore? blobStore = null,
    TimeProvider? timeProvider = null)
    : WebApplicationFactory<Program>
{
    internal const string TenantId = "11111111-1111-1111-1111-111111111111";
    internal const string ObjectId = "22222222-2222-2222-2222-222222222222";
    internal const string RequiredRole = "SecureUpload.Management";

    public HttpClient CreateManagementClient(bool allowAutoRedirect = false) =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = allowAutoRedirect,
            BaseAddress = new Uri("https://localhost")
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ManagementAuthorization:TenantId", TenantId);
        builder.UseSetting("ManagementAuthorization:RequiredRole", RequiredRole);
        builder.UseSetting("Storage:BlobServiceUri", "https://storage-account.blob.core.windows.net");
        builder.UseSetting("Storage:TableServiceUri", "https://storage-account.table.core.windows.net");
        builder.UseSetting("Storage:StatusTableName", "filestatus");
        builder.UseSetting("Inventory:Capacity", (inventoryCapacity ?? 10_000).ToString());
        builder.UseSetting("Inventory:DefaultPageSize", "25");
        builder.UseSetting("Inventory:MaximumPageSize", "100");
        builder.UseSetting("Inventory:MaximumSearchLength", "255");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ManagementTelemetry>();
            services.AddSingleton(new ManagementTelemetry(NullLogger<ManagementTelemetry>.Instance));

            if (statusStore is not null)
            {
                services.RemoveAll<IFileStatusStore>();
                services.AddSingleton(statusStore);
            }

            if (blobStore is not null)
            {
                services.RemoveAll<IBlobFileStore>();
                services.AddSingleton(blobStore);
            }

            if (timeProvider is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
            }

            if (principal is null)
            {
                return;
            }

            services.AddSingleton(new TestAuthenticationState(principal));
            services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
        });
    }

    internal static ClaimsPrincipal CreatePrincipal(
        string tenantId = TenantId,
        string objectId = ObjectId,
        string? displayName = "admin@example.com",
        string[]? roles = null,
        string? identityType = null)
    {
        roles ??= [RequiredRole];
        var claims = new List<Claim>
        {
            new("tid", tenantId),
            new("oid", objectId)
        };

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            claims.Add(new Claim(ClaimTypes.Name, displayName));
        }

        if (!string.IsNullOrWhiteSpace(identityType))
        {
            claims.Add(new Claim("idtyp", identityType));
        }

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "aad", ClaimTypes.Name, ClaimTypes.Role));
    }
}

internal sealed class TestAuthenticationState(ClaimsPrincipal principal)
{
    public ClaimsPrincipal Principal { get; } = principal;
}

internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TestAuthenticationState state)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "management-test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(state.Principal, SchemeName)));

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
