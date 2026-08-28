using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using SecureUpload.Core.Files;

namespace SecureUpload.Core.Storage;

public sealed class AzureTableFileStatusStore : IFileStatusStore
{
    private const string RowKey = "status";
    private readonly TableClient _table;

    public AzureTableFileStatusStore(TableServiceClient serviceClient, string tableName)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        _table = serviceClient.GetTableClient(tableName);
    }

    public AzureTableFileStatusStore(
        Uri serviceUri,
        string tableName,
        TokenCredential? credential = null)
        : this(
            new TableServiceClient(
                serviceUri,
                credential ?? new DefaultAzureCredential()),
            tableName)
    {
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await _table.CreateIfNotExistsAsync(cancellationToken);

    public async Task<StatusWriteResult> CreateAsync(
        FileRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        FileRecord.ValidateStableId(record.StableId);
        record.ValidateDeletionAudit();

        try
        {
            var entity = FileStatusEntity.FromRecord(record);
            await _table.AddEntityAsync(entity, cancellationToken);
            var stored = await GetAsync(record.StableId, cancellationToken);
            return new(StatusWriteDisposition.Succeeded, stored);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            return new(StatusWriteDisposition.AlreadyExists);
        }
    }

    public async Task<FileRecord?> GetAsync(
        string stableId,
        CancellationToken cancellationToken = default)
    {
        FileRecord.ValidateStableId(stableId);

        try
        {
            var response = await _table.GetEntityAsync<FileStatusEntity>(
                stableId,
                RowKey,
                cancellationToken: cancellationToken);
            return response.Value.ToRecord();
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<StatusWriteResult> UpdateAsync(
        FileRecord record,
        ETag expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        FileRecord.ValidateStableId(record.StableId);
        record.ValidateDeletionAudit();

        if (expectedETag == default || expectedETag == ETag.All)
        {
            throw new ArgumentException("A concrete Table ETag is required for every update.", nameof(expectedETag));
        }

        try
        {
            var entity = FileStatusEntity.FromRecord(record) with { ETag = expectedETag };
            await _table.UpdateEntityAsync(entity, expectedETag, TableUpdateMode.Replace, cancellationToken);
            var stored = await GetAsync(record.StableId, cancellationToken);
            return new(StatusWriteDisposition.Succeeded, stored);
        }
        catch (RequestFailedException exception) when (exception.Status == 412)
        {
            return new(StatusWriteDisposition.ConcurrencyConflict);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return new(StatusWriteDisposition.NotFound);
        }
        catch (RequestFailedException exception) when (exception.Status == 400 && exception.ErrorCode == "InvalidInput")
        {
            // Azurite reports a conditional replace of a missing entity as InvalidInput.
            if (await GetAsync(record.StableId, cancellationToken) is null)
            {
                return new(StatusWriteDisposition.NotFound);
            }

            throw;
        }
    }

    public async IAsyncEnumerable<FileRecord> QueryAsync(
        FileStatusQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filters = new List<string>();
        if (query.State is not null)
        {
            filters.Add(TableClient.CreateQueryFilter($"State eq {query.State.ToString()}"));
        }

        if (query.UpdatedBefore is not null)
        {
            filters.Add(TableClient.CreateQueryFilter($"UpdatedAt lt {query.UpdatedBefore.Value}"));
        }

        var filter = filters.Count == 0 ? null : string.Join(" and ", filters);
        await foreach (var entity in _table.QueryAsync<FileStatusEntity>(
                           filter: filter,
                           cancellationToken: cancellationToken))
        {
            yield return entity.ToRecord();
        }
    }

    private sealed record FileStatusEntity : ITableEntity
    {
        public required string PartitionKey { get; set; }
        public string RowKey { get; set; } = AzureTableFileStatusStore.RowKey;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public required string OriginalFileName { get; init; }
        public required string MediaType { get; init; }
        public required string State { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
        public long? SizeBytes { get; init; }
        public string? SourceETag { get; init; }
        public string? TargetETag { get; init; }
        public string? PendingBlobUri { get; init; }
        public DateTimeOffset? UploadedAt { get; init; }
        public DateTimeOffset? ProcessingStartedAt { get; init; }
        public DateTimeOffset? ScanCompletedAt { get; init; }
        public string? LastEventId { get; init; }
        public string? ScanCorrelationId { get; init; }
        public string? FailureCode { get; init; }
        public DateTimeOffset? DeletionRequestedAt { get; init; }
        public DateTimeOffset? DeletedAt { get; init; }
        public string? DeletedBy { get; init; }

        public static FileStatusEntity FromRecord(FileRecord record) =>
            new()
            {
                PartitionKey = record.StableId,
                OriginalFileName = record.OriginalFileName,
                MediaType = record.MediaType,
                State = record.State.ToString(),
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt,
                SizeBytes = record.SizeBytes,
                SourceETag = record.SourceETag,
                TargetETag = record.TargetETag,
                PendingBlobUri = record.PendingBlobUri?.AbsoluteUri,
                UploadedAt = record.UploadedAt,
                ProcessingStartedAt = record.ProcessingStartedAt,
                ScanCompletedAt = record.ScanCompletedAt,
                LastEventId = record.LastEventId,
                ScanCorrelationId = record.ScanCorrelationId,
                FailureCode = record.FailureCode,
                DeletionRequestedAt = record.DeletionRequestedAt,
                DeletedAt = record.DeletedAt,
                DeletedBy = record.DeletedBy,
                ETag = record.StoreETag ?? default
            };

        public FileRecord ToRecord() =>
            new()
            {
                StableId = PartitionKey,
                OriginalFileName = OriginalFileName,
                MediaType = MediaType,
                State = Enum.Parse<FileState>(State, ignoreCase: false),
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                SizeBytes = SizeBytes,
                SourceETag = SourceETag,
                TargetETag = TargetETag,
                PendingBlobUri = PendingBlobUri is null ? null : new Uri(PendingBlobUri),
                UploadedAt = UploadedAt,
                ProcessingStartedAt = ProcessingStartedAt,
                ScanCompletedAt = ScanCompletedAt,
                LastEventId = LastEventId,
                ScanCorrelationId = ScanCorrelationId,
                FailureCode = FailureCode,
                DeletionRequestedAt = DeletionRequestedAt,
                DeletedAt = DeletedAt,
                DeletedBy = DeletedBy,
                StoreETag = ETag
            };
    }
}
