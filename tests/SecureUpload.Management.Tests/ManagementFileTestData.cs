using System.Reflection;
using Azure;
using SecureUpload.Core.Files;

namespace SecureUpload.Management.Tests;

internal static class ManagementFileTestData
{
    private const string DeletedBy = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    public static FileRecord CreateRecord(FileState state, string fileName, int seed) =>
        CreateRecord(state, fileName, BaseTime.AddMinutes(seed), seed);

    public static string CreateStableId(int seed) =>
        seed.ToString("x").PadLeft(64, '0');

    public static FileRecord WithStoreETag(FileRecord record, string eTag)
    {
        var stored = record with { };
        typeof(FileRecord)
            .GetProperty(nameof(FileRecord.StoreETag), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(stored, new ETag(eTag));
        return stored;
    }

    private static FileRecord CreateRecord(
        FileState state,
        string fileName,
        DateTimeOffset createdAt,
        int seed)
    {
        var stableId = CreateStableId(seed);
        var uploading = FileRecord.CreateUploading(fileName, "application/pdf", createdAt, stableId);

        return state switch
        {
            FileState.Uploading => uploading,
            FileState.Pending => FileStateMachine.Transition(
                uploading,
                FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
            FileState.Promoting => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    uploading,
                    FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
                FileTransition.Clean("event-clean", "correlation-clean", "\"source-v1\"", createdAt.AddMinutes(2))).Record,
            FileState.Quarantining => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    uploading,
                    FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
                FileTransition.Malicious("event-mal", "correlation-mal", "\"source-v1\"", createdAt.AddMinutes(2))).Record,
            FileState.Available => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    FileStateMachine.Transition(
                        uploading,
                        FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
                    FileTransition.Clean("event-clean", "correlation-clean", "\"source-v1\"", createdAt.AddMinutes(2))).Record,
                FileTransition.PromotionCompleted("\"clean-v1\"", createdAt.AddMinutes(3))).Record,
            FileState.Rejected => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    FileStateMachine.Transition(
                        uploading,
                        FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
                    FileTransition.Malicious("event-mal", "correlation-mal", "\"source-v1\"", createdAt.AddMinutes(2))).Record,
                FileTransition.QuarantineCompleted("\"quarantine-v1\"", createdAt.AddMinutes(3))).Record,
            FileState.ScanError => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    uploading,
                    FileTransition.UploadCompleted("\"source-v1\"", 42, createdAt.AddMinutes(1))).Record,
                FileTransition.ScanFailed(
                    "event-error",
                    "correlation-error",
                    "\"source-v1\"",
                    "scan-error",
                    createdAt.AddMinutes(2))).Record,
            FileState.UploadFailed => FileStateMachine.Transition(
                uploading,
                FileTransition.UploadFailed("upload-failed", createdAt.AddMinutes(1))).Record,
            FileState.Deleting => FileStateMachine.Transition(
                CreateRecord(FileState.Available, fileName, createdAt, seed + 10_000),
                FileTransition.DeleteRequested(DeletedBy, createdAt.AddMinutes(4))).Record,
            FileState.Deleted => FileStateMachine.Transition(
                FileStateMachine.Transition(
                    CreateRecord(FileState.Available, fileName, createdAt, seed + 20_000),
                    FileTransition.DeleteRequested(DeletedBy, createdAt.AddMinutes(4))).Record,
                FileTransition.DeleteCompleted(createdAt.AddMinutes(5))).Record,
            _ => throw new InvalidOperationException("Unsupported test file state.")
        };
    }
}
