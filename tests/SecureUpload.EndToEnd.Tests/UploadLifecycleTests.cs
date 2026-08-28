using System.Net;
using System.Net.Http.Json;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Scanning;

namespace SecureUpload.EndToEnd.Tests;

public sealed class UploadLifecycleTests
{
    [Fact]
    public async Task BenignUploadMovesFromPendingToAvailableAndIsHostReadable()
    {
        await using var host = new EndToEndTestHost();
        var bytes = "verified benign bytes"u8.ToArray();
        var upload = await host.UploadAsync(bytes);

        var pending = await host.Client.GetFromJsonAsync<PublicStatus>(
            $"/api/uploads/{upload.FileId}/status");
        var processed = await host.CreateProcessor().ProcessAsync(
            host.EventFor(upload.FileId, MalwareScanOutcome.Clean));
        var available = await host.Client.GetFromJsonAsync<PublicStatus>(
            $"/api/uploads/{upload.FileId}/status");

        Assert.Equal("pending", pending!.Status);
        Assert.Equal(ScanProcessingDisposition.Completed, processed.Disposition);
        Assert.Equal("available", available!.Status);
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Pending));
        Assert.Equal(bytes, host.Blobs.Read(upload.FileId, BlobArea.Clean));
    }

    [Fact]
    public async Task MaliciousAndNotScannedOutcomesStayOutsideHostReadableStorage()
    {
        await using var host = new EndToEndTestHost();
        var malicious = await host.UploadAsync("isolated malware marker"u8.ToArray());
        var uncertain = await host.UploadAsync("unsupported encrypted fixture"u8.ToArray());

        await host.CreateProcessor().ProcessAsync(
            host.EventFor(malicious.FileId, MalwareScanOutcome.Malicious));
        await host.CreateProcessor().ProcessAsync(
            host.EventFor(uncertain.FileId, MalwareScanOutcome.ScanError));

        Assert.Equal(FileState.Rejected, host.Statuses.Required(malicious.FileId).State);
        Assert.NotNull(host.Blobs.Get(malicious.FileId, BlobArea.Quarantine));
        Assert.Null(host.Blobs.Get(malicious.FileId, BlobArea.Clean));
        Assert.Equal(FileState.ScanError, host.Statuses.Required(uncertain.FileId).State);
        Assert.NotNull(host.Blobs.Get(uncertain.FileId, BlobArea.Pending));
        Assert.Null(host.Blobs.Get(uncertain.FileId, BlobArea.Clean));
    }

    [Fact]
    public async Task HostDeletionDoesNotLetDuplicateScanRecreateCleanBlob()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync();
        var scanEvent = host.EventFor(upload.FileId, MalwareScanOutcome.Clean);
        await host.CreateProcessor().ProcessAsync(scanEvent);
        host.Blobs.DeleteAsHost(upload.FileId);

        var replay = await host.CreateProcessor().ProcessAsync(scanEvent);

        Assert.Equal(ScanProcessingDisposition.OperationalConflict, replay.Disposition);
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Clean));
        Assert.Equal(FileState.Available, host.Statuses.Required(upload.FileId).State);
    }

    private sealed record PublicStatus(string FileId, string Status);
}
