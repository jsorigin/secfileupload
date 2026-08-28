using Microsoft.AspNetCore.Mvc.Testing;

namespace SecureUpload.Web.Tests.Accessibility;

public sealed class UploadPageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public UploadPageTests(WebApplicationFactory<Program> factory) =>
        _client = factory.CreateClient();

    [Fact]
    public async Task PageHasAccessibleUploadRetryAndLiveStatus()
    {
        var html = await _client.GetStringAsync("/upload");

        Assert.Contains("<label", html);
        Assert.Contains("type=\"file\"", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("id=\"retry\"", html);
        Assert.Contains("focus-visible", await _client.GetStringAsync("/css/uploader.css"));
        Assert.Contains("@media", await _client.GetStringAsync("/css/uploader.css"));
    }

    [Fact]
    public async Task ScriptUsesExactTargetAndBoundedPolling()
    {
        var script = await _client.GetStringAsync("/js/uploader.js");

        Assert.DoesNotContain("postMessage(message, '*')", script);
        Assert.DoesNotContain("document.referrer", script);
        Assert.Contains("targetOrigin", script);
        Assert.Contains("MAX_POLL_ATTEMPTS = 2160", script);
        Assert.Contains("document.visibilityState", script);
        Assert.Contains("accepted", script);
        Assert.Contains("scan-error", script);
        Assert.Contains("Security check is still pending", script);
    }
}
