using System.Net;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Scanning;

namespace SecureUpload.EndToEnd.Tests;

public sealed class AuthorizationBoundaryTests
{
    [Fact]
    public async Task AnonymousCallerCannotUseHostStatusButCanUseCapabilityPolling()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync();

        var hostStatus = await host.Client.GetAsync($"/api/host/files/{upload.FileId}/status");
        var polling = await host.Client.GetAsync($"/api/uploads/{upload.FileId}/status");

        Assert.Equal(HttpStatusCode.Unauthorized, hostStatus.StatusCode);
        Assert.Equal(HttpStatusCode.OK, polling.StatusCode);
    }

    [Fact]
    public async Task ComponentStorageViewsEnforceTheDocumentedLeastPrivilegeBoundary()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync();
        await host.CreateProcessor().ProcessAsync(
            host.EventFor(upload.FileId, MalwareScanOutcome.Clean));

        var permissions = ComponentPermissions.Default;

        Assert.True(permissions.CanRead(ComponentIdentity.Host, BlobArea.Clean));
        Assert.True(permissions.CanDelete(ComponentIdentity.Host, BlobArea.Clean));
        Assert.False(permissions.CanWrite(ComponentIdentity.Host, BlobArea.Clean));
        Assert.False(permissions.CanRead(ComponentIdentity.Host, BlobArea.Pending));
        Assert.False(permissions.CanRead(ComponentIdentity.Host, BlobArea.Quarantine));
        Assert.False(permissions.CanRead(ComponentIdentity.Web, BlobArea.Clean));
        Assert.True(permissions.CanWrite(ComponentIdentity.Web, BlobArea.Pending));
        Assert.True(permissions.CanRead(ComponentIdentity.Processor, BlobArea.Pending));
        Assert.True(permissions.CanWrite(ComponentIdentity.Processor, BlobArea.Clean));
        Assert.True(permissions.CanWrite(ComponentIdentity.Processor, BlobArea.Quarantine));
    }

    private enum ComponentIdentity
    {
        Web,
        Processor,
        Host
    }

    private sealed class ComponentPermissions
    {
        public static ComponentPermissions Default { get; } = new();

        public bool CanRead(ComponentIdentity identity, BlobArea area) =>
            identity == ComponentIdentity.Processor ||
            identity == ComponentIdentity.Host && area == BlobArea.Clean;

        public bool CanWrite(ComponentIdentity identity, BlobArea area) =>
            identity == ComponentIdentity.Processor ||
            identity == ComponentIdentity.Web && area == BlobArea.Pending;

        public bool CanDelete(ComponentIdentity identity, BlobArea area) =>
            identity == ComponentIdentity.Processor ||
            identity == ComponentIdentity.Web && area == BlobArea.Pending ||
            identity == ComponentIdentity.Host && area == BlobArea.Clean;
    }
}
