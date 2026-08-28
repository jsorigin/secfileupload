using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text;
using Azure;
using Microsoft.Extensions.Logging;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Core.Telemetry;
using SecureUpload.Management.Files;
using SecureUpload.Management.Telemetry;

namespace SecureUpload.Management.Tests.Files;

public sealed class CleanFileDownloadServiceTests
{
    [Fact]
    public async Task OpenReadAsync_AvailableFileStreamsTheVerifiedCleanBlobWithASafeFileName()
    {
        var record = ManagementFileTestData.CreateRecord(
            FileState.Available,
            "C:\\unsafe\\Quarterly\x0001.pdf",
            1);
        var statuses = new DownloadStatusStore(record);
        var blobs = new DownloadBlobStore
        {
            ReadDisposition = ConditionalBlobReadDisposition.Succeeded,
            ReadETag = new ETag(record.TargetETag!),
            ReadContent = "clean bytes"u8.ToArray()
        };
        var service = CreateService(statuses, blobs);

        var result = await service.OpenReadAsync(record.StableId);

        Assert.Equal(CleanFileDownloadDisposition.Ready, result.Disposition);
        Assert.Equal("Quarterly.pdf", result.DownloadFileName);
        Assert.NotNull(result.Content);
        await using var content = result.Content!;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        Assert.Equal("clean bytes", Encoding.UTF8.GetString(buffer.ToArray()));
        Assert.Equal(record.TargetETag, blobs.ExpectedEtags.Single().ToString());
    }

    [Theory]
    [InlineData(FileState.Uploading)]
    [InlineData(FileState.Pending)]
    [InlineData(FileState.Promoting)]
    [InlineData(FileState.Quarantining)]
    [InlineData(FileState.Rejected)]
    [InlineData(FileState.ScanError)]
    [InlineData(FileState.UploadFailed)]
    [InlineData(FileState.Deleting)]
    [InlineData(FileState.Deleted)]
    public async Task OpenReadAsync_RejectsEveryNonDownloadableState(FileState state)
    {
        var record = ManagementFileTestData.CreateRecord(state, $"{state}.pdf", 2);
        var statuses = new DownloadStatusStore(record);
        var blobs = new DownloadBlobStore();
        var service = CreateService(statuses, blobs);

        var result = await service.OpenReadAsync(record.StableId);

        Assert.Equal(CleanFileDownloadDisposition.NotAvailable, result.Disposition);
        Assert.Null(result.Content);
        Assert.Equal(0, blobs.ReadCalls);
    }

    [Fact]
    public async Task OpenReadAsync_MissingTargetEtagFailsClosedWithoutTouchingBlobStorage()
    {
        var record = WithoutTargetETag(ManagementFileTestData.CreateRecord(FileState.Available, "report.pdf", 3));
        var measurements = new ConcurrentBag<(string Name, string? Reason)>();
        using var listener = Listen(measurements);
        var blobs = new DownloadBlobStore();
        var logger = new CapturingLogger<ManagementTelemetry>();
        var service = CreateService(new DownloadStatusStore(record), blobs, logger);

        var result = await service.OpenReadAsync(record.StableId);

        Assert.Equal(CleanFileDownloadDisposition.IntegrityFailure, result.Disposition);
        Assert.Equal(CleanFileIntegrityFailureReason.MissingTargetETag, result.IntegrityFailureReason);
        Assert.Equal(0, blobs.ReadCalls);
        Assert.Contains(
            measurements,
            measurement => measurement.Name == TelemetryNames.ManagementDownloadIntegrityFailure &&
                           measurement.Reason == "target-etag-missing");
        Assert.DoesNotContain(record.StableId, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("report.pdf", logger.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ConditionalBlobReadDisposition.NotFound, CleanFileIntegrityFailureReason.BlobMissing, "blob-missing")]
    [InlineData(ConditionalBlobReadDisposition.ETagMismatch, CleanFileIntegrityFailureReason.ETagMismatch, "etag-mismatch")]
    public async Task OpenReadAsync_IntegrityFailuresEmitPrivacySafeTelemetry(
        ConditionalBlobReadDisposition blobDisposition,
        CleanFileIntegrityFailureReason expectedReason,
        string expectedTelemetryReason)
    {
        var record = ManagementFileTestData.CreateRecord(FileState.Available, "report.pdf", 4);
        var measurements = new ConcurrentBag<(string Name, string? Reason)>();
        using var listener = Listen(measurements);
        var logger = new CapturingLogger<ManagementTelemetry>();
        var blobs = new DownloadBlobStore
        {
            ReadDisposition = blobDisposition,
            ReadETag = new ETag(record.TargetETag!)
        };
        var service = CreateService(new DownloadStatusStore(record), blobs, logger);

        var result = await service.OpenReadAsync(record.StableId);

        Assert.Equal(CleanFileDownloadDisposition.IntegrityFailure, result.Disposition);
        Assert.Equal(expectedReason, result.IntegrityFailureReason);
        Assert.Contains(
            measurements,
            measurement => measurement.Name == TelemetryNames.ManagementDownloadIntegrityFailure &&
                           measurement.Reason == expectedTelemetryReason);
        Assert.DoesNotContain(record.StableId, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("report.pdf", logger.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenReadAsync_InvalidAndMissingIdsFailClosed()
    {
        var service = CreateService(new DownloadStatusStore(null), new DownloadBlobStore());

        var invalid = await service.OpenReadAsync("not-a-stable-id");
        var missing = await service.OpenReadAsync(ManagementFileTestData.CreateStableId(999));

        Assert.Equal(CleanFileDownloadDisposition.InvalidId, invalid.Disposition);
        Assert.Equal(CleanFileDownloadDisposition.NotFound, missing.Disposition);
    }

    private static CleanFileDownloadService CreateService(
        DownloadStatusStore statuses,
        DownloadBlobStore blobs,
        CapturingLogger<ManagementTelemetry>? logger = null) =>
        new(
            statuses,
            blobs,
            new ManagementTelemetry(logger ?? new CapturingLogger<ManagementTelemetry>()));

    private static FileRecord WithoutTargetETag(FileRecord record)
    {
        var mutated = record with { };
        typeof(FileRecord)
            .GetProperty(nameof(FileRecord.TargetETag), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(mutated, null);
        return mutated;
    }

    private static MeterListener Listen(ConcurrentBag<(string Name, string? Reason)> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TelemetryNames.Meter)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == TelemetryNames.ReasonTag)
                {
                    reason = tag.Value?.ToString();
                    break;
                }
            }

            measurements.Add((instrument.Name, reason));
        });
        listener.Start();
        return listener;
    }

    private sealed class DownloadStatusStore(FileRecord? record) : IFileStatusStore
    {
        public FileRecord? Record { get; set; } = record;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<StatusWriteResult> CreateAsync(
            FileRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileRecord?> GetAsync(
            string stableId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Record);

        public Task<StatusWriteResult> UpdateAsync(
            FileRecord record,
            ETag expectedETag,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<FileRecord> QueryAsync(
            FileStatusQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Record is not null)
            {
                yield return Record;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class DownloadBlobStore : IBlobFileStore
    {
        public ConditionalBlobReadDisposition ReadDisposition { get; init; }
        public ETag ReadETag { get; init; } = new("\"clean-v1\"");
        public byte[] ReadContent { get; init; } = [];
        public int ReadCalls { get; private set; }
        public List<ETag> ExpectedEtags { get; } = [];

        public Task<BlobWriteResult> UploadPendingAsync(
            string stableId,
            Stream content,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BlobCopyResult> CopyPendingAsync(
            string stableId,
            BlobArea destination,
            ETag expectedSourceETag,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConditionalBlobDeleteDisposition> DeleteIfMatchAsync(
            string stableId,
            BlobArea area,
            ETag expectedETag,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BlobWriteResult?> GetPropertiesAsync(
            string stableId,
            BlobArea area,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConditionalBlobReadResult> OpenCleanReadIfMatchAsync(
            string stableId,
            ETag expectedETag,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            ExpectedEtags.Add(expectedETag);
            return Task.FromResult(ReadDisposition switch
            {
                ConditionalBlobReadDisposition.NotFound =>
                    new ConditionalBlobReadResult(ConditionalBlobReadDisposition.NotFound),
                ConditionalBlobReadDisposition.ETagMismatch =>
                    new ConditionalBlobReadResult(ConditionalBlobReadDisposition.ETagMismatch),
                _ => new ConditionalBlobReadResult(
                    ConditionalBlobReadDisposition.Succeeded,
                    new BlobReadResult(
                        new MemoryStream(ReadContent, writable: false),
                        ReadETag))
            });
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];
        public string Text => string.Join(Environment.NewLine, _messages);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _messages.Add(formatter(state, exception));
    }
}
