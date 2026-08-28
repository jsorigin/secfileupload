using Microsoft.Extensions.Options;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Management.Telemetry;

namespace SecureUpload.Management.Files;

public sealed class FileInventoryOptions
{
    public const string SectionName = "Inventory";

    public int Capacity { get; set; } = 10_000;
    public int DefaultPageSize { get; set; } = 25;
    public int MaximumPageSize { get; set; } = 100;
    public int MaximumSearchLength { get; set; } = 255;

    public void Validate()
    {
        if (Capacity <= 0 ||
            DefaultPageSize <= 0 ||
            MaximumPageSize <= 0 ||
            DefaultPageSize > MaximumPageSize ||
            MaximumSearchLength <= 0 ||
            MaximumSearchLength > 255)
        {
            throw new InvalidOperationException(
                "Inventory options require positive capacity, page-size bounds, and a search length between 1 and 255.");
        }
    }
}

public enum InventoryLoadState
{
    Ready,
    Empty,
    NoMatch,
    CapacityExceeded,
    StorageError
}

public enum FileLookupState
{
    Found,
    NotFound,
    InvalidId,
    StorageError
}

public sealed record InventoryFilterOption(string Value, string Label);

public sealed record FileInventoryQuery(
    string Search,
    string Filter,
    int PageNumber,
    int PageSize,
    FileState? StateFilter);

public sealed record FileInventoryResult(
    InventoryLoadState State,
    FileInventoryQuery Query,
    IReadOnlyList<ManagementFileView> Files,
    int SnapshotCount,
    int MatchedCount,
    int TotalPages,
    int FirstItemNumber,
    int LastItemNumber)
{
    public bool HasResults => Files.Count > 0;
    public bool HasPreviousPage => TotalPages > 0 && Query.PageNumber > 1;
    public bool HasNextPage => TotalPages > 0 && Query.PageNumber < TotalPages;
}

public sealed record FileLookupResult(
    FileLookupState State,
    string RequestedFileId,
    ManagementFileView? File = null)
{
    public bool Found => State == FileLookupState.Found && File is not null;
}

public sealed class FileInventoryService(
    IFileStatusStore statusStore,
    IOptions<FileInventoryOptions> options,
    ManagementTelemetry telemetry)
{
    private const string AllFilter = "all";

    private static readonly IReadOnlyList<InventoryFilterOption> Filters =
    [
        new(AllFilter, "All statuses"),
        new("uploading", "Uploading"),
        new("pending", "Pending scan"),
        new("promoting", "Promoting clean copy"),
        new("quarantining", "Quarantining copy"),
        new("available", "Available"),
        new("rejected", "Rejected"),
        new("scan-error", "Scan error"),
        new("upload-failed", "Upload failed"),
        new("deleting", "Deleting"),
        new("deleted", "Deleted")
    ];

    private readonly FileInventoryOptions _options = options.Value;

    public int Capacity => _options.Capacity;
    public int DefaultPageSize => _options.DefaultPageSize;
    public int MaximumPageSize => _options.MaximumPageSize;
    public int MaximumSearchLength => _options.MaximumSearchLength;
    public IReadOnlyList<InventoryFilterOption> FilterOptions => Filters;

    public async Task<FileInventoryResult> LoadAsync(
        string? search,
        string? filter,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeQuery(search, filter, page, pageSize);
        List<FileRecord> snapshot;
        try
        {
            snapshot = await LoadSnapshotAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            telemetry.RecordInventoryStorageFailure("query", exception);
            return new FileInventoryResult(
                InventoryLoadState.StorageError,
                normalizedQuery,
                [],
                0,
                0,
                0,
                0,
                0);
        }

        if (snapshot.Count > _options.Capacity)
        {
            telemetry.RecordCapacityExceeded();
            return new FileInventoryResult(
                InventoryLoadState.CapacityExceeded,
                normalizedQuery,
                [],
                0,
                0,
                0,
                0,
                0);
        }

        if (snapshot.Count == 0)
        {
            return new FileInventoryResult(
                InventoryLoadState.Empty,
                normalizedQuery,
                [],
                0,
                0,
                0,
                0,
                0);
        }

        var matches = snapshot
            .Where(record => Matches(record, normalizedQuery))
            .OrderByDescending(record => record.CreatedAt)
            .ThenBy(record => record.StableId, StringComparer.Ordinal)
            .Select(ManagementFileView.FromRecord)
            .ToArray();

        if (matches.Length == 0)
        {
            return new FileInventoryResult(
                InventoryLoadState.NoMatch,
                normalizedQuery,
                [],
                snapshot.Count,
                0,
                0,
                0,
                0);
        }

        var totalPages = (int)Math.Ceiling(matches.Length / (double)normalizedQuery.PageSize);
        if (normalizedQuery.PageNumber > totalPages)
        {
            normalizedQuery = normalizedQuery with { PageNumber = totalPages };
        }

        var skip = (normalizedQuery.PageNumber - 1) * normalizedQuery.PageSize;
        var pageItems = matches.Skip(skip).Take(normalizedQuery.PageSize).ToArray();

        return new FileInventoryResult(
            InventoryLoadState.Ready,
            normalizedQuery,
            pageItems,
            snapshot.Count,
            matches.Length,
            totalPages,
            skip + 1,
            skip + pageItems.Length);
    }

    public async Task<FileLookupResult> GetFileAsync(
        string? fileId,
        CancellationToken cancellationToken = default)
    {
        var requestedFileId = fileId?.Trim() ?? string.Empty;
        if (!TryNormalizeStableId(requestedFileId, out var normalizedFileId))
        {
            return new FileLookupResult(FileLookupState.InvalidId, requestedFileId);
        }

        try
        {
            var record = await statusStore.GetAsync(normalizedFileId, cancellationToken);
            return record is null
                ? new FileLookupResult(FileLookupState.NotFound, normalizedFileId)
                : new FileLookupResult(
                    FileLookupState.Found,
                    normalizedFileId,
                    ManagementFileView.FromRecord(record));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            telemetry.RecordInventoryStorageFailure("lookup", exception);
            return new FileLookupResult(FileLookupState.StorageError, normalizedFileId);
        }
    }

    private async Task<List<FileRecord>> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = new List<FileRecord>(Math.Min(_options.Capacity + 1, 256));
        await foreach (var record in statusStore.QueryAsync(new FileStatusQuery(), cancellationToken))
        {
            snapshot.Add(record);
            if (snapshot.Count > _options.Capacity)
            {
                break;
            }
        }

        return snapshot;
    }

    private FileInventoryQuery NormalizeQuery(
        string? search,
        string? filter,
        int? page,
        int? pageSize)
    {
        var normalizedSearch = NormalizeSearch(search);
        var normalizedFilter = NormalizeFilter(filter, out var stateFilter);
        var normalizedPage = page is > 0 ? page.Value : 1;
        var normalizedPageSize = pageSize is > 0
            ? Math.Min(pageSize.Value, _options.MaximumPageSize)
            : _options.DefaultPageSize;

        return new FileInventoryQuery(
            normalizedSearch,
            normalizedFilter,
            normalizedPage,
            normalizedPageSize,
            stateFilter);
    }

    private string NormalizeSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return string.Empty;
        }

        var normalized = ManagementFileView.NormalizeFileName(search);
        if (normalized.Length > _options.MaximumSearchLength)
        {
            normalized = normalized[^_options.MaximumSearchLength..];
        }

        return normalized;
    }

    private static string NormalizeFilter(string? filter, out FileState? stateFilter)
    {
        var normalized = filter?.Trim().ToLowerInvariant();
        stateFilter = normalized switch
        {
            "uploading" => FileState.Uploading,
            "pending" => FileState.Pending,
            "promoting" => FileState.Promoting,
            "quarantining" => FileState.Quarantining,
            "available" => FileState.Available,
            "rejected" => FileState.Rejected,
            "scan-error" => FileState.ScanError,
            "upload-failed" => FileState.UploadFailed,
            "deleting" => FileState.Deleting,
            "deleted" => FileState.Deleted,
            _ => null
        };

        return stateFilter is null ? AllFilter : normalized!;
    }

    private static bool Matches(FileRecord record, FileInventoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(query);

        if (query.StateFilter is { } stateFilter && record.State != stateFilter)
        {
            return false;
        }

        if (string.IsNullOrEmpty(query.Search))
        {
            return true;
        }

        return ManagementFileView.NormalizeFileName(record.OriginalFileName)
            .Contains(query.Search, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeStableId(string candidate, out string stableId)
    {
        stableId = candidate.Trim().ToLowerInvariant();
        if (stableId.Length != 64 || stableId.Any(character =>
                character is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
        {
            stableId = string.Empty;
            return false;
        }

        return true;
    }
}
