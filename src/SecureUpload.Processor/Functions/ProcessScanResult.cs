using System.Text.Json.Nodes;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SecureUpload.Processor.Scanning;
using SecureUpload.Processor.Telemetry;

namespace SecureUpload.Processor.Functions;

public sealed class ProcessScanResult(
    ScanResultProcessor processor,
    ScanProcessorOptions options,
    ScanTelemetry telemetry)
{
    [Function(nameof(ProcessScanResult))]
    public async Task RunAsync(
        [EventGridTrigger] string eventGridEventJson,
        CancellationToken cancellationToken)
    {
        try
        {
            if (eventGridEventJson.Length > 128 * 1024)
            {
                telemetry.RecordInvalidEvent("oversized");
                throw new MalformedScanEventException("The Event Grid payload is invalid.");
            }

            if (JsonNode.Parse(eventGridEventJson) is not JsonObject envelope)
            {
                telemetry.RecordInvalidEvent("malformed");
                throw new MalformedScanEventException("The Event Grid payload is invalid.");
            }

            var scanEvent = MalwareScanEventParser.Parse(envelope, options);
            using var operation = telemetry.Start(scanEvent);
            var result = await processor.ProcessAsync(scanEvent, cancellationToken);
            telemetry.RecordOutcome(operation, scanEvent, result.Disposition);
        }
        catch (UntrustedScanEventException exception)
        {
            telemetry.RecordInvalidEvent(exception.Reason);
        }
        catch (MalformedScanEventException exception)
        {
            telemetry.RecordInvalidEvent("malformed");
            if (exception.FailClosedEvent is not null)
            {
                using var operation = telemetry.Start(exception.FailClosedEvent);
                var result = await processor.ProcessAsync(exception.FailClosedEvent, cancellationToken);
                telemetry.RecordOutcome(operation, exception.FailClosedEvent, result.Disposition);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            telemetry.RecordInvalidEvent("malformed");
        }
        catch (RetryableScanProcessingException)
        {
            telemetry.RecordRetry("storage");
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            telemetry.RecordRetry("unexpected");
            throw new RetryableScanProcessingException("Unexpected scan processing failure.");
        }
    }

}
