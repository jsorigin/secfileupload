using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SecureUpload.Web.Tests;

public sealed class StorageConfigurationTests
{
    [Theory]
    [InlineData("Storage:BlobServiceUri")]
    [InlineData("Storage:TableServiceUri")]
    [InlineData("Storage:UploadAdmissionTableName")]
    public void StartupRejectsMissingStorageServiceUri(string key)
    {
        using var factory = new MissingStorageConfigurationFactory(key);

        var exception = Assert.Throws<InvalidOperationException>(
            () => factory.CreateClient());
        Assert.Contains($"{key} is required.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains($"{key} is required.", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class MissingStorageConfigurationFactory(string missingKey)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(missingKey, string.Empty);
        }
    }
}
