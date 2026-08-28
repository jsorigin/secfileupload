using SecureUpload.Core.Files;

namespace SecureUpload.Core.Tests.Files;

public sealed class FileProcessingRecoveryTests
{
    private const string SourceETag = "\"source-v1\"";
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Target_copy_is_durable_before_terminal_transition()
    {
        var processing = MakeProcessing();

        var copied = FileStateMachine.Transition(
            processing,
            FileTransition.TargetCopyRecorded("\"clean-v1\"", Now.AddMinutes(1)));
        var terminal = FileStateMachine.Transition(
            copied.Record,
            FileTransition.PromotionCompleted("\"clean-v1\"", Now.AddMinutes(2)));

        Assert.Equal(TransitionDisposition.Applied, copied.Disposition);
        Assert.Equal(FileState.Promoting, copied.Record.State);
        Assert.Equal("\"clean-v1\"", copied.Record.TargetETag);
        Assert.Equal(FileState.Available, terminal.Record.State);
    }

    [Fact]
    public void Terminal_transition_cannot_commit_a_different_target()
    {
        var copied = FileStateMachine.Transition(
            MakeProcessing(),
            FileTransition.TargetCopyRecorded("\"clean-v1\"", Now.AddMinutes(1))).Record;

        var result = FileStateMachine.Transition(
            copied,
            FileTransition.PromotionCompleted("\"other\"", Now.AddMinutes(2)));

        Assert.Equal(TransitionDisposition.Rejected, result.Disposition);
        Assert.Equal(FileState.Promoting, result.Record.State);
    }

    [Fact]
    public void Recovery_from_processing_scan_error_clears_the_abandoned_target_etag()
    {
        var copied = FileStateMachine.Transition(
            MakeProcessing(),
            FileTransition.TargetCopyRecorded("\"clean-v1\"", Now.AddMinutes(1))).Record;
        var failed = FileStateMachine.Transition(
            copied,
            FileTransition.ScanFailed(
                "event-error",
                "correlation-error",
                SourceETag,
                "blob-state-invalid",
                Now.AddMinutes(2))).Record;

        var recovery = FileStateMachine.Transition(
            failed,
            FileTransition.Clean(
                "event-retry",
                "correlation-retry",
                SourceETag,
                Now.AddMinutes(3)));

        Assert.Equal(FileState.ScanError, failed.State);
        Assert.Equal("\"clean-v1\"", failed.TargetETag);
        Assert.Equal(FileState.Promoting, recovery.Record.State);
        Assert.Null(recovery.Record.TargetETag);
    }

    private static FileRecord MakeProcessing()
    {
        var uploading = FileRecord.CreateUploading(
            "report.pdf",
            "application/pdf",
            Now.AddMinutes(-2),
            new string('a', 64));
        var pending = FileStateMachine.Transition(
            uploading,
            FileTransition.UploadCompleted(SourceETag, 42, Now.AddMinutes(-1))).Record;
        return FileStateMachine.Transition(
            pending,
            FileTransition.Clean("event", "correlation", SourceETag, Now)).Record;
    }
}
