using Microsoft.Azure.Functions.Worker;
using SecureUpload.Processor.Scanning;
using SecureUpload.Processor.Telemetry;

namespace SecureUpload.Processor.Functions;

public sealed class ProcessPendingDeletions(
    DeletionProcessor processor,
    ScanTelemetry telemetry)
{
    [Function(nameof(ProcessPendingDeletions))]
    public async Task RunAsync(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await processor.ProcessPendingAsync(DateTimeOffset.UtcNow, cancellationToken);
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
            throw new RetryableScanProcessingException("Unexpected deletion cleanup failure.");
        }
    }
}
