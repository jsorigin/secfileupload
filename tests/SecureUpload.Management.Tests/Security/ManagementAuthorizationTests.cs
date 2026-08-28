using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using SecureUpload.Management.Security;

namespace SecureUpload.Management.Tests.Security;

public sealed class ManagementAuthorizationTests
{
    [Fact]
    public async Task AuthorizedUserReachesLandingPageAndSeesStableObjectId()
    {
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal());
        using var client = factory.CreateManagementClient();

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Authenticated session", html);
        Assert.Contains(ManagementWebApplicationFactory.ObjectId, html);
    }

    [Fact]
    public async Task AnonymousRequestChallengesThroughEasyAuthAndPreservesSafeDeepLink()
    {
        await using var factory = new ManagementWebApplicationFactory();
        using var client = factory.CreateManagementClient();

        var response = await client.GetAsync("/?filter=available&page=2");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        var query = QueryHelpers.ParseQuery(new Uri($"https://localhost{location}").Query);
        Assert.StartsWith("/.auth/login/aad", location, StringComparison.Ordinal);
        Assert.Equal("/?filter=available&page=2", query["post_login_redirect_uri"].ToString());
    }

    [Fact]
    public async Task NestedExternalReturnUrlIsDroppedFromSignInChallenge()
    {
        await using var factory = new ManagementWebApplicationFactory();
        using var client = factory.CreateManagementClient();

        var response = await client.GetAsync("/?returnUrl=https%3A%2F%2Fevil.example%2Fphish");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        var query = QueryHelpers.ParseQuery(new Uri($"https://localhost{location}").Query);
        Assert.Equal("/", query["post_login_redirect_uri"].ToString());
        Assert.DoesNotContain("evil.example", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedUserWithoutRequiredRoleGetsForbiddenWithoutProtectedBody()
    {
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal(roles: []));
        using var client = factory.CreateManagementClient();

        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task SignOutPostRequiresAntiforgeryToken()
    {
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal());
        using var client = factory.CreateManagementClient();

        var response = await client.PostAsync(
            "/?handler=SignOut",
            new FormUrlEncodedContent([new("returnUrl", "/")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/files/details?fileId=abc123", "/files/details?fileId=abc123")]
    [InlineData("https://evil.example/phish", "/")]
    [InlineData("//evil.example/phish", "/")]
    public async Task SignOutUsesOnlySafeLocalReturnTargets(string requestedReturnUrl, string expectedReturnUrl)
    {
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal());
        using var client = factory.CreateManagementClient();

        var html = await client.GetStringAsync("/");
        var antiforgeryToken = ExtractHiddenInputValue(html, "__RequestVerificationToken");

        var response = await client.PostAsync(
            "/?handler=SignOut",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", antiforgeryToken),
                new("returnUrl", requestedReturnUrl)
            ]));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        var query = QueryHelpers.ParseQuery(new Uri($"https://localhost{location}").Query);
        Assert.StartsWith("/.auth/logout", location, StringComparison.Ordinal);
        Assert.Equal(expectedReturnUrl, query["post_logout_redirect_uri"].ToString());
    }

    [Fact]
    public void DocumentedClientPrincipalRepresentationParsesAndValidates()
    {
        var header = CreateClientPrincipalHeader(
        [
            ("http://schemas.microsoft.com/identity/claims/tenantid", ManagementWebApplicationFactory.TenantId),
            ("http://schemas.microsoft.com/identity/claims/objectidentifier", ManagementWebApplicationFactory.ObjectId),
            (ClaimTypes.Role, ManagementWebApplicationFactory.RequiredRole),
            (ClaimTypes.Name, "admin@example.com")
        ]);
        var options = CreateAuthorizationOptions();
        var objectId = string.Empty;

        var parsed = ManagementAuthorization.TryParseClientPrincipal(header, out var principal);
        var validated = principal is not null &&
            ManagementAuthorization.TryGetValidatedUserObjectId(principal, options, out objectId);

        Assert.True(parsed);
        Assert.True(validated);
        Assert.Equal(ManagementWebApplicationFactory.ObjectId, objectId);
    }

    [Fact]
    public void MalformedClientPrincipalHeaderIsRejected()
    {
        var parsed = ManagementAuthorization.TryParseClientPrincipal("not-base64", out var principal);

        Assert.False(parsed);
        Assert.Null(principal);
    }

    [Theory]
    [MemberData(nameof(InvalidPrincipals))]
    public void InvalidClaimsFailClosed(
        IEnumerable<(string Type, string Value)> claims)
    {
        var header = CreateClientPrincipalHeader(claims);
        var options = CreateAuthorizationOptions();

        Assert.True(ManagementAuthorization.TryParseClientPrincipal(header, out var principal));
        Assert.NotNull(principal);
        Assert.False(ManagementAuthorization.TryGetValidatedUserObjectId(principal!, options, out _));
    }

    public static TheoryData<IEnumerable<(string Type, string Value)>> InvalidPrincipals() =>
        new()
        {
            new[]
            {
                ("http://schemas.microsoft.com/identity/claims/tenantid", "33333333-3333-3333-3333-333333333333"),
                ("http://schemas.microsoft.com/identity/claims/objectidentifier", ManagementWebApplicationFactory.ObjectId),
                (ClaimTypes.Role, ManagementWebApplicationFactory.RequiredRole)
            },
            new[]
            {
                ("http://schemas.microsoft.com/identity/claims/tenantid", ManagementWebApplicationFactory.TenantId),
                ("idtyp", "app"),
                ("http://schemas.microsoft.com/identity/claims/objectidentifier", ManagementWebApplicationFactory.ObjectId),
                (ClaimTypes.Role, ManagementWebApplicationFactory.RequiredRole)
            },
            new[]
            {
                ("http://schemas.microsoft.com/identity/claims/tenantid", ManagementWebApplicationFactory.TenantId),
                (ClaimTypes.Role, ManagementWebApplicationFactory.RequiredRole)
            },
            new[]
            {
                ("http://schemas.microsoft.com/identity/claims/tenantid", ManagementWebApplicationFactory.TenantId),
                ("http://schemas.microsoft.com/identity/claims/objectidentifier", ManagementWebApplicationFactory.ObjectId)
            },
            new[]
            {
                ("http://schemas.microsoft.com/identity/claims/tenantid", ManagementWebApplicationFactory.TenantId),
                ("http://schemas.microsoft.com/identity/claims/tenantid", "33333333-3333-3333-3333-333333333333"),
                ("http://schemas.microsoft.com/identity/claims/objectidentifier", ManagementWebApplicationFactory.ObjectId),
                (ClaimTypes.Role, ManagementWebApplicationFactory.RequiredRole)
            }
        };

    private static ManagementAuthorizationOptions CreateAuthorizationOptions()
    {
        var options = new ManagementAuthorizationOptions
        {
            TenantId = ManagementWebApplicationFactory.TenantId,
            RequiredRole = ManagementWebApplicationFactory.RequiredRole
        };
        options.Validate();
        return options;
    }

    private static string CreateClientPrincipalHeader(IEnumerable<(string Type, string Value)> claims)
    {
        var payload = JsonSerializer.Serialize(new
        {
            auth_typ = "aad",
            name_typ = ClaimTypes.Name,
            role_typ = ClaimTypes.Role,
            claims = claims.Select(claim => new { typ = claim.Type, val = claim.Value }).ToArray()
        });

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
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
