namespace SecureUpload.Core.Files;

public enum FileState
{
    Uploading,
    Pending,
    Promoting,
    Quarantining,
    Available,
    Rejected,
    ScanError,
    UploadFailed,
    Deleting,
    Deleted
}

public enum PublicFileState
{
    Pending,
    Available,
    Rejected,
    ScanError
}

public static class FileStateExtensions
{
    public static PublicFileState ToPublicState(this FileState state) =>
        state switch
        {
            FileState.Available => PublicFileState.Available,
            FileState.Rejected => PublicFileState.Rejected,
            FileState.ScanError => PublicFileState.ScanError,
            FileState.Uploading or FileState.Pending or FileState.Promoting or FileState.Quarantining =>
                PublicFileState.Pending,
            FileState.UploadFailed or FileState.Deleting or FileState.Deleted =>
                throw new InvalidOperationException($"{state} is not an accepted upload state."),
            _ => throw new InvalidOperationException($"{state} is not an accepted upload state.")
        };
}
