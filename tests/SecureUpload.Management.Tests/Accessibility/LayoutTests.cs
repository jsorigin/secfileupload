namespace SecureUpload.Management.Tests.Accessibility;

public sealed class LayoutTests
{
    [Fact]
    public async Task LayoutIncludesLandmarksSkipLinkFocusStylesAndSignOutControl()
    {
        await using var factory = new ManagementWebApplicationFactory(
            ManagementWebApplicationFactory.CreatePrincipal());
        using var client = factory.CreateManagementClient();

        var html = await client.GetStringAsync("/");
        var css = await client.GetStringAsync("/css/site.css");

        Assert.Contains("<title>File management - Secure file management</title>", html);
        Assert.Contains("Skip to main content", html);
        Assert.Contains("id=\"main-content\"", html);
        Assert.Contains("type=\"submit\">Sign out</button>", html);
        Assert.Contains("id=\"signed-in-oid\"", html);
        Assert.Contains(":focus-visible", css);
        Assert.Contains("@media (max-width: 40rem)", css);
    }
}
