using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace SecureUpload.Management.Security;

public sealed class ManagementAuthorizationOptions
{
    public const string SectionName = "ManagementAuthorization";
    public const string AuthenticationScheme = "management-app-service";

    public string TenantId { get; set; } = string.Empty;
    public string RequiredRole { get; set; } = string.Empty;
    public string LoginPath { get; set; } = "/.auth/login/aad";
    public string LogoutPath { get; set; } = "/.auth/logout";
    public string ExpectedIdentityProvider { get; set; } = "aad";

    public void Validate()
    {
        if (!Guid.TryParse(TenantId, out _) ||
            string.IsNullOrWhiteSpace(RequiredRole) ||
            !IsAuthPath(LoginPath) ||
            !IsAuthPath(LogoutPath) ||
            string.IsNullOrWhiteSpace(ExpectedIdentityProvider))
        {
            throw new InvalidOperationException(
                $"{SectionName} requires a tenant ID, required role, Easy Auth login/logout paths, and expected identity provider.");
        }
    }

    private static bool IsAuthPath(string value) =>
        value.StartsWith("/.auth/", StringComparison.Ordinal) &&
        !value.StartsWith("//", StringComparison.Ordinal);
}

public static class ManagementAuthorization
{
    private const int MaximumHeaderLength = 32 * 1024;
    private const int MaximumPayloadBytes = 24 * 1024;
    private const int MaximumClaims = 256;
    private const int MaximumClaimTypeLength = 512;
    private const int MaximumClaimValueLength = 4096;
    private static readonly string[] TenantClaimTypes =
    [
        "tid",
        "http://schemas.microsoft.com/identity/claims/tenantid"
    ];
    private static readonly string[] ObjectIdClaimTypes =
    [
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier"
    ];
    private static readonly string[] IdentityTypeClaimTypes =
    [
        "idtyp"
    ];
    private static readonly string[] NestedReturnUrlKeys =
    [
        "returnUrl",
        "post_login_redirect_uri",
        "post_logout_redirect_uri"
    ];

    public static IServiceCollection AddManagementAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = configuration
            .GetSection(ManagementAuthorizationOptions.SectionName)
            .Get<ManagementAuthorizationOptions>() ?? new();
        settings.Validate();

        services.AddSingleton(Options.Create(settings));
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = ManagementAuthorizationOptions.AuthenticationScheme;
                options.DefaultChallengeScheme = ManagementAuthorizationOptions.AuthenticationScheme;
                options.DefaultForbidScheme = ManagementAuthorizationOptions.AuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, AppServiceClientPrincipalAuthenticationHandler>(
                ManagementAuthorizationOptions.AuthenticationScheme,
                _ => { });

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ManagementPrincipalRequirement())
                .Build();
        });
        services.AddSingleton<IAuthorizationHandler, ManagementPrincipalAuthorizationHandler>();

        return services;
    }

    public static bool TryParseClientPrincipal(string? headerValue, out ClaimsPrincipal? principal)
    {
        principal = null;
        if (string.IsNullOrWhiteSpace(headerValue) || headerValue.Length > MaximumHeaderLength)
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(headerValue);
        }
        catch (FormatException)
        {
            return false;
        }

        if (payload.Length is 0 or > MaximumPayloadBytes)
        {
            return false;
        }

        ClientPrincipalEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ClientPrincipalEnvelope>(
                payload,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (JsonException)
        {
            return false;
        }

        if (envelope is null ||
            string.IsNullOrWhiteSpace(envelope.AuthenticationType) ||
            envelope.Claims is null ||
            envelope.Claims.Count is 0 or > MaximumClaims)
        {
            return false;
        }

        var claims = new List<Claim>(envelope.Claims.Count);
        foreach (var claim in envelope.Claims)
        {
            if (string.IsNullOrWhiteSpace(claim.Type) ||
                string.IsNullOrWhiteSpace(claim.Value) ||
                claim.Type.Length > MaximumClaimTypeLength ||
                claim.Value.Length > MaximumClaimValueLength)
            {
                return false;
            }

            claims.Add(new Claim(claim.Type, claim.Value));
        }

        var identity = new ClaimsIdentity(
            claims,
            envelope.AuthenticationType,
            string.IsNullOrWhiteSpace(envelope.NameClaimType) ? ClaimTypes.Name : envelope.NameClaimType,
            string.IsNullOrWhiteSpace(envelope.RoleClaimType) ? ClaimTypes.Role : envelope.RoleClaimType);

        principal = new ClaimsPrincipal(identity);
        return true;
    }

    public static bool TryGetValidatedUserObjectId(
        ClaimsPrincipal principal,
        ManagementAuthorizationOptions options,
        out string objectId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        objectId = string.Empty;
        var identity = principal.Identity as ClaimsIdentity;
        if (identity is null ||
            !identity.IsAuthenticated ||
            !StringComparer.OrdinalIgnoreCase.Equals(identity.AuthenticationType, options.ExpectedIdentityProvider))
        {
            return false;
        }

        if (!TryGetSingleGuidClaim(principal, TenantClaimTypes, out var tenantId) ||
            tenantId != Guid.Parse(options.TenantId) ||
            !TryGetSingleGuidClaim(principal, ObjectIdClaimTypes, out var parsedObjectId) ||
            principal.Claims.Any(claim =>
                IdentityTypeClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase) &&
                StringComparer.OrdinalIgnoreCase.Equals(claim.Value, "app")) ||
            !HasRequiredRole(principal, options.RequiredRole))
        {
            return false;
        }

        objectId = parsedObjectId.ToString();
        return true;
    }

    public static string BuildCurrentLocalPath(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = request.PathBase.Add(request.Path).ToString();
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        var queryBuilder = new QueryBuilder();
        foreach (var pair in request.Query)
        {
            foreach (var value in pair.Value)
            {
                if (NestedReturnUrlKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var safeValue = SanitizeLocalReturnUrl(value);
                    if (!StringComparer.Ordinal.Equals(safeValue, "/"))
                    {
                        queryBuilder.Add(pair.Key, safeValue);
                    }

                    continue;
                }

                queryBuilder.Add(pair.Key, value ?? string.Empty);
            }
        }

        return $"{path}{queryBuilder.ToQueryString()}";
    }

    public static string SanitizeLocalReturnUrl(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "/";
        }

        var trimmed = candidate.Trim();
        if (trimmed.Length > 2048 ||
            !trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/\\", StringComparison.Ordinal) ||
            trimmed.Contains('\r') ||
            trimmed.Contains('\n'))
        {
            return "/";
        }

        return trimmed;
    }

    public static string CreateSignOutPath(
        ManagementAuthorizationOptions options,
        string? returnUrl) =>
        QueryHelpers.AddQueryString(
            options.LogoutPath,
            "post_logout_redirect_uri",
            SanitizeLocalReturnUrl(returnUrl));

    private static bool TryGetSingleGuidClaim(
        ClaimsPrincipal principal,
        IReadOnlyCollection<string> claimTypes,
        out Guid value)
    {
        var values = principal.Claims
            .Where(claim => claimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (values.Length == 1 && Guid.TryParse(values[0], out value))
        {
            return true;
        }

        value = Guid.Empty;
        return false;
    }

    private static bool HasRequiredRole(ClaimsPrincipal principal, string requiredRole)
    {
        if (principal.IsInRole(requiredRole))
        {
            return true;
        }

        var roleClaimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ClaimTypes.Role,
            "roles",
            "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
        };

        if (principal.Identity is ClaimsIdentity identity &&
            !string.IsNullOrWhiteSpace(identity.RoleClaimType))
        {
            roleClaimTypes.Add(identity.RoleClaimType);
        }

        return principal.Claims.Any(claim =>
            roleClaimTypes.Contains(claim.Type) &&
            StringComparer.Ordinal.Equals(claim.Value, requiredRole));
    }

    private sealed class ClientPrincipalEnvelope
    {
        [JsonPropertyName("auth_typ")]
        public string? AuthenticationType { get; init; }

        [JsonPropertyName("claims")]
        public List<ClientPrincipalClaim>? Claims { get; init; }

        [JsonPropertyName("name_typ")]
        public string? NameClaimType { get; init; }

        [JsonPropertyName("role_typ")]
        public string? RoleClaimType { get; init; }
    }

    private sealed class ClientPrincipalClaim
    {
        [JsonPropertyName("typ")]
        public string? Type { get; init; }

        [JsonPropertyName("val")]
        public string? Value { get; init; }
    }
}

internal sealed class AppServiceClientPrincipalAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ManagementAuthorizationOptions> authorizationOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string ClientPrincipalHeader = "X-MS-CLIENT-PRINCIPAL";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ClientPrincipalHeader, out var header) ||
            header.Count == 0 ||
            string.IsNullOrWhiteSpace(header[0]))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (header.Count != 1 ||
            !ManagementAuthorization.TryParseClientPrincipal(header[0], out var principal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Malformed App Service client principal header."));
        }

        var ticket = new AuthenticationTicket(principal!, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
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

internal sealed class ManagementPrincipalRequirement : IAuthorizationRequirement;

internal sealed class ManagementPrincipalAuthorizationHandler(
    IOptions<ManagementAuthorizationOptions> options)
    : AuthorizationHandler<ManagementPrincipalRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ManagementPrincipalRequirement requirement)
    {
        if (ManagementAuthorization.TryGetValidatedUserObjectId(
            context.User,
            options.Value,
            out _))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
