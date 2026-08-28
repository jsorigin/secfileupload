using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecureUpload.Web.Pages;
using SecureUpload.Web.Security;

namespace SecureUpload.Web.Tests.Security;

public sealed class OriginPolicyTests
{
    [Fact]
    public void ExactOriginsDriveCspCorsAndMessaging()
    {
        var policy = new AllowedOriginPolicy(Options.Create(new AllowedOriginOptions
        {
            Origins = ["https://host.example", "https://test.host.example:8443"]
        }));

        Assert.True(policy.IsAllowed("https://host.example"));
        Assert.True(policy.IsAllowed("https://test.host.example:8443"));
        Assert.False(policy.IsAllowed("https://host.example.evil"));
        Assert.False(policy.IsAllowed("HTTPS://HOST.EXAMPLE"));
        Assert.Equal("frame-ancestors https://host.example https://test.host.example:8443", policy.FrameAncestors);
        Assert.Equal("https://host.example", policy.GetMessageTarget("https://host.example"));
        Assert.Null(policy.GetMessageTarget("https://evil.example"));
    }

    [Fact]
    public void InsecureOriginsAreRejectedExceptForLoopbackDevelopment()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new AllowedOriginPolicy(Options.Create(new AllowedOriginOptions
            {
                Origins = ["http://host.example"]
            })));

        var local = new AllowedOriginPolicy(Options.Create(new AllowedOriginOptions
        {
            Origins = ["http://127.0.0.1:5000", "http://localhost:5001"]
        }));

        Assert.True(local.IsAllowed("http://127.0.0.1:5000"));
        Assert.True(local.IsAllowed("http://localhost:5001"));
    }

    [Fact]
    public async Task MiddlewareAddsCspAndCorsOnlyForAllowedOrigin()
    {
        var policy = new AllowedOriginPolicy(Options.Create(new AllowedOriginOptions
        {
            Origins = ["https://host.example"]
        }));
        var middleware = new OriginSecurityMiddleware(_ => Task.CompletedTask);
        var allowed = new DefaultHttpContext();
        allowed.Request.Headers.Origin = "https://host.example";
        var denied = new DefaultHttpContext();
        denied.Request.Headers.Origin = "https://evil.example";

        await middleware.InvokeAsync(allowed, policy);
        await middleware.InvokeAsync(denied, policy);

        Assert.Equal("frame-ancestors https://host.example", allowed.Response.Headers.ContentSecurityPolicy);
        Assert.Equal("https://host.example", allowed.Response.Headers.AccessControlAllowOrigin);
        Assert.False(denied.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void UploadPageSerializesOnlyValidatedParentOrigin()
    {
        var policy = new AllowedOriginPolicy(Options.Create(new AllowedOriginOptions
        {
            Origins = ["https://host.example"]
        }));
        var configuration = new ConfigurationBuilder().Build();
        var approved = new UploadModel(policy, configuration);
        var denied = new UploadModel(policy, configuration);
        var missing = new UploadModel(policy, configuration);

        approved.OnGet("https://host.example");
        denied.OnGet("https://evil.example");
        missing.OnGet(null);

        Assert.Equal(
            "https://host.example",
            JsonDocument.Parse(approved.ClientConfiguration).RootElement
                .GetProperty("targetOrigin").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            JsonDocument.Parse(denied.ClientConfiguration).RootElement
                .GetProperty("targetOrigin").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            JsonDocument.Parse(missing.ClientConfiguration).RootElement
                .GetProperty("targetOrigin").ValueKind);
        Assert.DoesNotContain("allowedOrigins", approved.ClientConfiguration, StringComparison.Ordinal);
    }
}
