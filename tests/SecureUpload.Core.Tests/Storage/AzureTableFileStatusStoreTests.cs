using Azure;
using Azure.Data.Tables;
using System.Reflection;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;

namespace SecureUpload.Core.Tests.Storage;

public sealed class AzureTableFileStatusStoreTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private const string DeletedBy = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Create_read_conditionally_update_and_query_follow_table_semantics()
    {
        var connectionString = Environment.GetEnvironmentVariable("AZURITE_TABLE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var tableName = $"files{Guid.NewGuid():N}";
        var store = new AzureTableFileStatusStore(
            new TableServiceClient(connectionString),
            tableName);
        await store.InitializeAsync();

        try
        {
            var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var uploading = FileRecord.CreateUploading(
                "untrusted<script>.pdf",
                "application/pdf",
                createdAt);

            var created = await store.CreateAsync(uploading);
            var loaded = await store.GetAsync(uploading.StableId);

            Assert.Equal(StatusWriteDisposition.Succeeded, created.Disposition);
            Assert.NotNull(created.Record?.StoreETag);
            Assert.Equal(uploading.StableId, loaded?.StableId);
            Assert.Equal("untrusted<script>.pdf", loaded?.OriginalFileName);

            var transition = FileStateMachine.Transition(
                loaded!,
                FileTransition.UploadCompleted("\"blob-v1\"", 12, createdAt.AddMinutes(1)));
            var updated = await store.UpdateAsync(transition.Record, loaded!.StoreETag!.Value);

            Assert.Equal(StatusWriteDisposition.Succeeded, updated.Disposition);
            Assert.Equal(FileState.Pending, updated.Record?.State);
            Assert.NotEqual(loaded.StoreETag, updated.Record?.StoreETag);

            var staleTransition = FileStateMachine.Transition(
                updated.Record!,
                FileTransition.ScanFailed(
                    "event-stale",
                    "correlation-stale",
                    "\"blob-v1\"",
                    "must-not-win",
                    createdAt.AddMinutes(2)));
            var staleAttempt = await store.UpdateAsync(
                staleTransition.Record,
                loaded.StoreETag!.Value);

            Assert.Equal(StatusWriteDisposition.ConcurrencyConflict, staleAttempt.Disposition);
            Assert.Null(staleAttempt.Record);

            var pending = new List<FileRecord>();
            await foreach (var record in store.QueryAsync(
                               new FileStatusQuery(State: FileState.Pending)))
            {
                pending.Add(record);
            }

            Assert.Contains(pending, record => record.StableId == uploading.StableId);
            Assert.Null((await store.GetAsync(uploading.StableId))?.FailureCode);
        }
        finally
        {
            await new TableServiceClient(connectionString).DeleteTableAsync(tableName);
        }
    }

    [Fact]
    public async Task Missing_record_returns_explicit_not_found_result()
    {
        var connectionString = Environment.GetEnvironmentVariable("AZURITE_TABLE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var tableName = $"files{Guid.NewGuid():N}";
        var store = new AzureTableFileStatusStore(new TableServiceClient(connectionString), tableName);
        await store.InitializeAsync();

        try
        {
            var missing = FileRecord.CreateUploading("missing.pdf", "application/pdf", DateTimeOffset.UtcNow);
            var result = await store.UpdateAsync(missing, new ETag("\"missing\""));

            Assert.Equal(StatusWriteDisposition.NotFound, result.Disposition);
        }
        finally
        {
            await new TableServiceClient(connectionString).DeleteTableAsync(tableName);
        }
    }

    [Fact]
    public void Deleted_tombstones_round_trip_through_the_table_entity_mapping()
    {
        var deleted = FileStateMachine.Transition(
            FileStateMachine.Transition(
                CreateAvailableRecord(),
                FileTransition.DeleteRequested(DeletedBy, CreatedAt.AddMinutes(4))).Record,
            FileTransition.DeleteCompleted(CreatedAt.AddMinutes(5))).Record;

        var entity = FromRecord(deleted);
        var roundTripped = ToRecord(entity);

        Assert.Equal(FileState.Deleted, roundTripped.State);
        Assert.Equal(DeletedBy, roundTripped.DeletedBy);
        Assert.Equal(CreatedAt.AddMinutes(4), roundTripped.DeletionRequestedAt);
        Assert.Equal(CreatedAt.AddMinutes(5), roundTripped.DeletedAt);
        Assert.Equal(deleted.OriginalFileName, roundTripped.OriginalFileName);
        Assert.Equal(deleted.SizeBytes, roundTripped.SizeBytes);
        Assert.Equal(deleted.SourceETag, roundTripped.SourceETag);
        Assert.Equal(deleted.TargetETag, roundTripped.TargetETag);
        Assert.Equal(deleted.UploadedAt, roundTripped.UploadedAt);
        Assert.Equal(deleted.ScanCompletedAt, roundTripped.ScanCompletedAt);
    }

    [Fact]
    public void Legacy_rows_without_deletion_columns_deserialize_with_null_tombstone_fields()
    {
        var entityType = GetEntityType();
        var entity = Activator.CreateInstance(entityType, nonPublic: true)
            ?? throw new InvalidOperationException("Missing FileStatusEntity.");
        Set(entity, "PartitionKey", new string('a', 64));
        Set(entity, "OriginalFileName", "legacy.pdf");
        Set(entity, "MediaType", "application/pdf");
        Set(entity, "State", FileState.Pending.ToString());
        Set(entity, "CreatedAt", CreatedAt);
        Set(entity, "UpdatedAt", CreatedAt.AddMinutes(1));
        Set(entity, "SizeBytes", 12L);
        Set(entity, "SourceETag", "\"source-v1\"");
        Set(entity, "UploadedAt", CreatedAt.AddMinutes(1));

        var record = ToRecord(entity);

        Assert.Equal(FileState.Pending, record.State);
        Assert.Equal("legacy.pdf", record.OriginalFileName);
        Assert.Equal("\"source-v1\"", record.SourceETag);
        Assert.Null(record.DeletionRequestedAt);
        Assert.Null(record.DeletedAt);
        Assert.Null(record.DeletedBy);
    }

    private static FileRecord CreateAvailableRecord()
    {
        var uploading = FileRecord.CreateUploading(
            "report.pdf",
            "application/pdf",
            CreatedAt,
            new string('a', 64));
        var pending = FileStateMachine.Transition(
            uploading,
            FileTransition.UploadCompleted("\"source-v1\"", 42, CreatedAt.AddMinutes(1))).Record;
        var promoting = FileStateMachine.Transition(
            pending,
            FileTransition.Clean("event-1", "correlation-1", "\"source-v1\"", CreatedAt.AddMinutes(2))).Record;
        return FileStateMachine.Transition(
            promoting,
            FileTransition.PromotionCompleted("\"clean-v1\"", CreatedAt.AddMinutes(3))).Record;
    }

    private static object FromRecord(FileRecord record)
    {
        var entityType = GetEntityType();
        return entityType.GetMethod("FromRecord", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [record])!;
    }

    private static FileRecord ToRecord(object entity)
    {
        var entityType = GetEntityType();
        return (FileRecord)entityType.GetMethod("ToRecord", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(entity, null)!;
    }

    private static Type GetEntityType() =>
        typeof(AzureTableFileStatusStore).GetNestedType("FileStatusEntity", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing FileStatusEntity.");

    private static void Set(object target, string propertyName, object? value) =>
        target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(target, value);
}
