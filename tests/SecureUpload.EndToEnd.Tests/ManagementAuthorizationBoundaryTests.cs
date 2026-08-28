using System.Net;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Core.Telemetry;
using SecureUpload.Processor.Scanning;

namespace SecureUpload.EndToEnd.Tests;

public sealed class ManagementAuthorizationBoundaryTests
{
    [Fact]
    public async Task UnauthenticatedWrongTenantAndMissingRoleCallersCannotInferInventoryOrContent()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync(
            "sensitive clean bytes"u8.ToArray(),
            "secret-fixture.txt");
        await host.CreateProcessor().ProcessAsync(
            host.EventFor(upload.FileId, MalwareScanOutcome.Clean));

        using var anonymousClient = host.CreateManagementClient(allowAutoRedirect: false);
        using var wrongTenantClient = host.CreateManagementClient(
            ManagementTestPrincipal.WrongTenant,
            allowAutoRedirect: false);
        using var noRoleClient = host.CreateManagementClient(
            ManagementTestPrincipal.NoRole,
            allowAutoRedirect: false);

        var anonymousInventory = await anonymousClient.GetAsync("/");
        var anonymousInventoryBody = await anonymousInventory.Content.ReadAsStringAsync();
        var anonymousDetails = await anonymousClient.GetAsync(
            $"/Files/Details?fileId={upload.FileId}&returnUrl=%2F");
        var anonymousDetailsBody = await anonymousDetails.Content.ReadAsStringAsync();
        var wrongTenantDetails = await wrongTenantClient.GetAsync(
            $"/Files/Details?fileId={upload.FileId}&returnUrl=%2F");
        var wrongTenantBody = await wrongTenantDetails.Content.ReadAsStringAsync();
        var noRoleDownload = await noRoleClient.GetAsync(
            $"/Files/Details?handler=Download&fileId={upload.FileId}&returnUrl=%2F");
        var noRoleDownloadBody = await noRoleDownload.Content.ReadAsStringAsync();
        var noRoleDelete = await noRoleClient.PostAsync(
            $"/Files/Details?handler=Delete&fileId={upload.FileId}",
            new FormUrlEncodedContent(
            [
                new("fileId", upload.FileId),
                new("returnUrl", "/"),
                new("DeleteConfirmation", "secret-fixture.txt")
            ]));
        var noRoleDeleteBody = await noRoleDelete.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Found, anonymousInventory.StatusCode);
        Assert.StartsWith("/.auth/login/aad", anonymousInventory.Headers.Location?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-fixture.txt", anonymousInventoryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Inventory results", anonymousInventoryBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Found, anonymousDetails.StatusCode);
        Assert.DoesNotContain("secret-fixture.txt", anonymousDetailsBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, wrongTenantDetails.StatusCode);
        Assert.Equal(string.Empty, wrongTenantBody);
        Assert.Equal(HttpStatusCode.Forbidden, noRoleDownload.StatusCode);
        Assert.Equal(string.Empty, noRoleDownloadBody);
        Assert.Equal(HttpStatusCode.Forbidden, noRoleDelete.StatusCode);
        Assert.Equal(string.Empty, noRoleDeleteBody);
        Assert.Equal(FileState.Available, host.Statuses.Required(upload.FileId).State);
        Assert.NotNull(host.Blobs.Get(upload.FileId, BlobArea.Clean));
    }

    [Fact]
    public void DeploymentContractKeepsManagementSettingsTelemetryAndLeastPrivilegeAligned()
    {
        foreach (var relativePath in new[]
                 {
                     Path.Combine("infra", "tests", "main.test.json"),
                     Path.Combine("infra", "tests", "security.test.json")
                 })
        {
            var contract = ReadRepositoryFile(relativePath);

            Assert.Contains(@"""name"": ""Inventory__Capacity""", contract, StringComparison.Ordinal);
            Assert.DoesNotContain("ManagementInventory__Capacity", contract, StringComparison.Ordinal);
            Assert.Contains(TelemetryNames.ManagementInventoryCapacityExceeded, contract, StringComparison.Ordinal);
            Assert.DoesNotContain("secure_upload.management.inventory.capacity_near_limit", contract, StringComparison.Ordinal);
            Assert.DoesNotContain("secure_upload.management.inventory.capacity_exhausted", contract, StringComparison.Ordinal);
        }

        var securityContract = ReadRepositoryFile(Path.Combine("infra", "tests", "security.test.json"));
        Assert.Contains(@"""statusTableReadUpdateOnly"": true", securityContract, StringComparison.Ordinal);
        Assert.Contains(@"""cleanBlobReadOnly"": true", securityContract, StringComparison.Ordinal);
        Assert.Contains(@"""pendingBlobAccess"": false", securityContract, StringComparison.Ordinal);
        Assert.Contains(@"""quarantineBlobAccess"": false", securityContract, StringComparison.Ordinal);
        Assert.Contains(@"""uploadAdmissionTableAccess"": false", securityContract, StringComparison.Ordinal);
        Assert.Contains(@"""functionHostStorageAccess"": false", securityContract, StringComparison.Ordinal);
        Assert.Contains(@"""eventGridAccess"": false", securityContract, StringComparison.Ordinal);
        Assert.Contains(@"""authDiagnosticsCategory"": ""AppServiceAuthenticationLogs""", securityContract, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
}
