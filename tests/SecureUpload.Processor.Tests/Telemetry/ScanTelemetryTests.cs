using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using Azure;
using Microsoft.Extensions.Logging;
using SecureUpload.Core.Storage;
using SecureUpload.Core.Telemetry;
using SecureUpload.Processor.Scanning;
using SecureUpload.Processor.Telemetry;

namespace SecureUpload.Processor.Tests.Telemetry;

public sealed class ScanTelemetryTests
{
    private const string StableId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Scan_outcome_emits_latency_and_privacy_safe_correlation()
    {
        var names = new ConcurrentBag<string>();
        using var listener = Listen(names);
        var logger = new CapturingLogger<ScanTelemetry>();
        var telemetry = Create(logger);
        var scanEvent = Event() with { FailureCode = "SAM999999: malware-name" };
        using var operation = telemetry.Start(scanEvent);

        telemetry.RecordScanLatency(
            scanEvent.StableId,
            scanEvent.ScanFinishedAt.AddMinutes(-1),
            scanEvent.ScanFinishedAt);
        telemetry.RecordOutcome(operation, scanEvent, ScanProcessingDisposition.ScanErrorRecorded);

        Assert.Contains(TelemetryNames.ScanOutcome, names);
        Assert.Contains(TelemetryNames.ScanLatency, names);
        Assert.DoesNotContain(StableId, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(scanEvent.EventId, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(scanEvent.CorrelationId, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(scanEvent.BlobUri.AbsolutePath, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("malware-name", logger.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_retry_stale_and_blob_failure_signals_are_emitted()
    {
        var names = new ConcurrentBag<string>();
        using var listener = Listen(names);
        var logger = new CapturingLogger<ScanTelemetry>();
        var telemetry = Create(logger);

        telemetry.RecordInvalidEvent("malicious-event-body");
        telemetry.RecordRetry("storage", StableId);
        telemetry.RecordStalePending(StableId, TimeSpan.FromHours(4));
        telemetry.RecordBlobFailure(
            StableId,
            "copy",
            new InvalidOperationException("https://account/pending/capability malware-name token"));
        telemetry.RecordDeletionCleanup(
            new FileDeletionCleanupResult(
            [
                new BlobAreaCleanupResult(BlobArea.Clean, BlobAreaCleanupDisposition.Deleted, 2),
                new BlobAreaCleanupResult(BlobArea.Quarantine, BlobAreaCleanupDisposition.Incomplete, 3)
            ]));

        Assert.Contains(TelemetryNames.InvalidEvent, names);
        Assert.Contains(TelemetryNames.ProcessingRetry, names);
        Assert.Contains(TelemetryNames.StalePending, names);
        Assert.Contains(TelemetryNames.OldestPendingAge, names);
        Assert.Contains(TelemetryNames.BlobOperationFailure, names);
        Assert.Contains(TelemetryNames.DeletionCleanupRetry, names);
        Assert.Contains(TelemetryNames.DeletionCleanupFailure, names);
        Assert.DoesNotContain(StableId, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("capability", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("malware-name", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("token", logger.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static ScanTelemetry Create(ILogger<ScanTelemetry> logger) =>
        new(
            new TelemetryCorrelation("unit-test-correlation-key-at-least-32-characters"),
            logger);

    private static MalwareScanEvent Event() =>
        new(
            "event-raw-id",
            "defender-raw-correlation",
            StableId,
            new Uri($"https://account.blob.core.windows.net/pending/{StableId}"),
            new ETag("\"source\""),
            DateTimeOffset.UtcNow.AddMinutes(-2),
            MalwareScanOutcome.ScanError,
            "sam-259206");

    private static MeterListener Listen(ConcurrentBag<string> names)
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
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => names.Add(instrument.Name));
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => names.Add(instrument.Name));
        listener.Start();
        return listener;
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
