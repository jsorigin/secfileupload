using System.Net;
using SecureUpload.Web.Tests.Files;

namespace SecureUpload.Web.Tests.Security;

public sealed class HostWorkloadAuthorizationTests
{
    [Fact]
    public async Task MissingTokenIsDenied()
    {
        await using var factory = new HostStatusFactory();

        var response = await factory.Client.GetAsync($"/api/host/files/{new string('a', 64)}/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task InvalidOrUnauthorizedTokenIsDenied(
        TokenClaims claims,
        HttpStatusCode expectedStatus)
    {
        await using var factory = new HostStatusFactory();
        var record = Files.StatusEndpointTests.CreateRecord(Core.Files.FileState.Pending);
        await factory.Statuses.CreateAsync(record);

        using var request = factory.AuthorizedRequest(
            $"/api/host/files/{record.StableId}/status",
            claims);
        var response = await factory.Client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("2.0")]
    public async Task VersionAppropriateApplicationIdentityClaimIsAccepted(string version)
    {
        await using var factory = new HostStatusFactory();
        var record = Files.StatusEndpointTests.CreateRecord(Core.Files.FileState.Pending);
        await factory.Statuses.CreateAsync(record);

        using var request = factory.AuthorizedRequest(
            $"/api/host/files/{record.StableId}/status",
            new TokenClaims { Version = version });
        var response = await factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<TokenClaims, HttpStatusCode> InvalidTokens() =>
        new()
        {
            { new TokenClaims { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }, HttpStatusCode.Unauthorized },
            { new TokenClaims { TenantId = "33333333-3333-3333-3333-333333333333" }, HttpStatusCode.Unauthorized },
            { new TokenClaims { Issuer = "https://issuer.example.invalid/" }, HttpStatusCode.Unauthorized },
            { new TokenClaims { Audience = "api://wrong-audience" }, HttpStatusCode.Unauthorized },
            { new TokenClaims { IdentityType = "user", Scope = "status.read" }, HttpStatusCode.Forbidden },
            { new TokenClaims { Scope = "status.read" }, HttpStatusCode.Forbidden },
            { new TokenClaims { ClientId = "44444444-4444-4444-4444-444444444444" }, HttpStatusCode.Forbidden },
            { new TokenClaims { Version = "1.0", ApplicationIdentityClaim = "azp" }, HttpStatusCode.Forbidden },
            { new TokenClaims { Version = "2.0", ApplicationIdentityClaim = "appid" }, HttpStatusCode.Forbidden },
            { new TokenClaims { Roles = [] }, HttpStatusCode.Forbidden },
            { new TokenClaims { Roles = ["SecureUpload.Status.Write"] }, HttpStatusCode.Forbidden }
        };
}
