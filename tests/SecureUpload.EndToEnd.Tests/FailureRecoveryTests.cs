using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Processor.Scanning;

namespace SecureUpload.EndToEnd.Tests;

public sealed class FailureRecoveryTests
{
    private const string DeletedBy = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task TransientCopyFailureRetriesFromDurableProcessingState()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync();
        var scanEvent = host.EventFor(upload.FileId, MalwareScanOutcome.Clean);
        host.Blobs.FailNextCopy = true;

        await Assert.ThrowsAsync<RetryableScanProcessingException>(
            () => host.CreateProcessor().ProcessAsync(scanEvent));
        var retry = await host.CreateProcessor().ProcessAsync(scanEvent);

        Assert.Equal(ScanProcessingDisposition.Completed, retry.Disposition);
        Assert.Equal(FileState.Available, host.Statuses.Required(upload.FileId).State);
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Pending));
        Assert.NotNull(host.Blobs.Get(upload.FileId, BlobArea.Clean));
    }

    [Fact]
    public async Task SourceDeleteFailureResumesWithoutDuplicateOrPrematureAvailability()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync();
        var scanEvent = host.EventFor(upload.FileId, MalwareScanOutcome.Clean);
        host.Blobs.FailNextDeleteArea = BlobArea.Pending;

        await Assert.ThrowsAsync<RetryableScanProcessingException>(
            () => host.CreateProcessor().ProcessAsync(scanEvent));
        Assert.Equal(FileState.Promoting, host.Statuses.Required(upload.FileId).State);
        Assert.NotNull(host.Blobs.Get(upload.FileId, BlobArea.Pending));

        var retry = await host.CreateProcessor().ProcessAsync(scanEvent);

        Assert.Equal(ScanProcessingDisposition.Completed, retry.Disposition);
        Assert.Equal(FileState.Available, host.Statuses.Required(upload.FileId).State);
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Pending));
    }

    [Fact]
    public async Task ReorderedConflictingEventsNeverReverseTerminalState()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync();
        var clean = host.EventFor(upload.FileId, MalwareScanOutcome.Clean, "clean");
        var malicious = clean with
        {
            EventId = "malicious",
            CorrelationId = "correlation-malicious",
            Outcome = MalwareScanOutcome.Malicious
        };

        await host.CreateProcessor().ProcessAsync(clean);
        var conflict = await host.CreateProcessor().ProcessAsync(malicious);

        Assert.Equal(ScanProcessingDisposition.OperationalConflict, conflict.Disposition);
        Assert.Equal(FileState.Available, host.Statuses.Required(upload.FileId).State);
        Assert.NotNull(host.Blobs.Get(upload.FileId, BlobArea.Clean));
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Quarantine));
    }

    [Fact]
    public async Task DeleteWinningAfterTargetCopyLeavesOnlyATombstone()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync();
        host.Statuses.BeforeUpdate = record =>
        {
            if (record.State != FileState.Promoting || record.TargetETag is null)
            {
                return;
            }

            var deleting = FileStateMachine.Transition(
                host.Statuses.Required(upload.FileId),
                FileTransition.DeleteRequested(DeletedBy, record.UpdatedAt)).Record;
            host.Statuses.Overwrite(upload.FileId, deleting, new Azure.ETag("\"table-delete\""));
            host.Statuses.BeforeUpdate = null;
        };

        var result = await host.CreateProcessor().ProcessAsync(
            host.EventFor(upload.FileId, MalwareScanOutcome.Clean));

        Assert.Equal(ScanProcessingDisposition.Duplicate, result.Disposition);
        Assert.Equal(FileState.Deleted, host.Statuses.Required(upload.FileId).State);
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Pending));
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Clean));
    }

    [Fact]
    public async Task ConcurrencyRaceIsBoundedAndLeavesPendingBytesInaccessible()
    {
        await using var host = new EndToEndTestHost();
        var upload = await host.UploadAsync();
        host.Statuses.ConflictsRemaining = 3;

        await Assert.ThrowsAsync<RetryableScanProcessingException>(
            () => host.CreateProcessor(maximumAttempts: 3).ProcessAsync(
                host.EventFor(upload.FileId, MalwareScanOutcome.Clean)));

        Assert.Equal(FileState.Pending, host.Statuses.Required(upload.FileId).State);
        Assert.NotNull(host.Blobs.Get(upload.FileId, BlobArea.Pending));
        Assert.Null(host.Blobs.Get(upload.FileId, BlobArea.Clean));
    }
}
