using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SecureUpload.Core.Telemetry;
using SecureUpload.Web.Telemetry;

namespace SecureUpload.Web.Tests.Telemetry;

public sealed class UploadTelemetryTests
{
    private const string StableId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Accepted_upload_emits_count_and_bytes_with_keyed_correlation()
    {
        var measurements = new ConcurrentBag<(string Name, long Value, string? Correlation)>();
        using var listener = Listen(measurements);
        var logger = new CapturingLogger<UploadTelemetry>();
        var telemetry = Create(logger);
        using var operation = telemetry.Start();

        telemetry.RecordAccepted(operation, StableId, 1234);

        Assert.Contains(measurements, item =>
            item.Name == TelemetryNames.UploadAccepted && item.Value == 1);
        Assert.Contains(measurements, item =>
            item.Name == TelemetryNames.UploadBytes && item.Value == 1234);
        Assert.All(measurements, item => Assert.NotEqual(StableId, item.Correlation));
        Assert.DoesNotContain(StableId, logger.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejection_and_rate_limit_use_bounded_reason_values()
    {
        var measurements = new ConcurrentBag<(string Name, long Value, string? Correlation)>();
        using var listener = Listen(measurements);
        var telemetry = Create(new CapturingLogger<UploadTelemetry>());
        using var operation = telemetry.Start();

        telemetry.RecordRejected(operation, "filename-from-user.exe");
        telemetry.RecordRateLimited("disabled");

        Assert.Contains(measurements, item => item.Name == TelemetryNames.UploadRejected);
        Assert.Contains(measurements, item => item.Name == TelemetryNames.UploadRateLimited);
        Assert.Contains(measurements, item => item.Name == TelemetryNames.UploadKillSwitch);
    }

    [Fact]
    public void Cleanup_failure_does_not_log_exception_message_or_sensitive_values()
    {
        var logger = new CapturingLogger<UploadTelemetry>();
        var telemetry = Create(logger);
        using var operation = telemetry.Start();
        const string sensitive =
            "evil.pdf Bearer token-value https://account.blob.core.windows.net/pending/capability-id";

        telemetry.RecordCleanupFailure(operation, "pending-delete", new InvalidOperationException(sensitive));

        Assert.DoesNotContain(sensitive, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.pdf", logger.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), logger.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Capability_bearing_paths_are_redacted()
    {
        var path = $"/api/host/files/{StableId}/status";

        var redacted = TelemetryPathRedactor.Redact(path);

        Assert.Equal("/api/host/files/{fileId}/status", redacted);
        Assert.DoesNotContain(StableId, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Storage_dependency_urls_are_redacted()
    {
        using var activity = new Activity("storage").Start();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://account.table.core.windows.net/filestatus()?PartitionKey='{StableId}'");

        TelemetryPathRedactor.RedactHttpDependency(activity, request);

        var url = activity.GetTagItem("url.full")?.ToString();
        Assert.NotNull(url);
        Assert.DoesNotContain(StableId, url, StringComparison.Ordinal);
    }

    private static UploadTelemetry Create(ILogger<UploadTelemetry> logger) =>
        new(
            new TelemetryCorrelation("unit-test-correlation-key-at-least-32-characters"),
            logger);

    private static MeterListener Listen(
        ConcurrentBag<(string Name, long Value, string? Correlation)> measurements)
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
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var correlation = tags.ToArray()
                .FirstOrDefault(tag => tag.Key == "secure_upload.file_correlation").Value?.ToString();
            measurements.Add((instrument.Name, value, correlation));
        });
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
