using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SecureUpload.Web.Security;

public sealed class HostWorkloadAuthorizationOptions
{
    public const string SectionName = "HostWorkloadAuthorization";
    public const string PolicyName = "host-workload";

    public string TenantId { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string AllowedClientApplicationId { get; set; } = string.Empty;
    public string RequiredRole { get; set; } = string.Empty;

    public void Validate()
    {
        if (!Guid.TryParse(TenantId, out _) ||
            string.IsNullOrWhiteSpace(Audience) ||
            !Guid.TryParse(AllowedClientApplicationId, out _) ||
            string.IsNullOrWhiteSpace(RequiredRole))
        {
            throw new InvalidOperationException(
                $"{SectionName} requires a tenant ID, audience, allowed client application ID, and application role.");
        }
    }
}

public static class HostWorkloadAuthorization
{
    public static IServiceCollection AddHostWorkloadAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = configuration
            .GetSection(HostWorkloadAuthorizationOptions.SectionName)
            .Get<HostWorkloadAuthorizationOptions>() ?? new();
        settings.Validate();

        services.AddSingleton(Options.Create(settings));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority =
                    $"https://login.microsoftonline.com/{settings.TenantId}/v2.0";
                options.Audience = settings.Audience;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers =
                    [
                        $"https://login.microsoftonline.com/{settings.TenantId}/v2.0",
                        $"https://sts.windows.net/{settings.TenantId}/"
                    ],
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                HostWorkloadAuthorizationOptions.PolicyName,
                policy =>
                {
                    policy.AuthenticationSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
                    policy.RequireAuthenticatedUser();
                    policy.RequireAssertion(context => IsAllowedAppToken(context.User, settings));
                });
        });

        return services;
    }

    private static bool IsAllowedAppToken(
        ClaimsPrincipal principal,
        HostWorkloadAuthorizationOptions settings)
    {
        if (!ClaimEquals(principal, "tid", settings.TenantId) ||
            !ClaimEquals(principal, "idtyp", "app") ||
            principal.HasClaim(claim => claim.Type == "scp") ||
            !principal.Claims.Any(claim =>
                claim.Type == "roles" &&
                StringComparer.Ordinal.Equals(claim.Value, settings.RequiredRole)))
        {
            return false;
        }

        var version = principal.FindFirstValue("ver");
        var clientClaim = version switch
        {
            "1.0" => "appid",
            "2.0" => "azp",
            _ => null
        };

        if (clientClaim is null ||
            !ClaimEquals(principal, clientClaim, settings.AllowedClientApplicationId))
        {
            return false;
        }

        var expectedIssuer = version == "1.0"
            ? $"https://sts.windows.net/{settings.TenantId}/"
            : $"https://login.microsoftonline.com/{settings.TenantId}/v2.0";
        return ClaimEquals(principal, "iss", expectedIssuer);
    }

    private static bool ClaimEquals(ClaimsPrincipal principal, string type, string expected) =>
        StringComparer.OrdinalIgnoreCase.Equals(principal.FindFirstValue(type), expected);
}
