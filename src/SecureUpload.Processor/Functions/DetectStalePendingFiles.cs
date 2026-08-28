using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SecureUpload.Processor.Scanning;
using SecureUpload.Processor.Telemetry;

namespace SecureUpload.Processor.Functions;

public sealed class DetectStalePendingFiles(
    StalePendingWatchdog watchdog,
    ScanTelemetry telemetry)
{
    [Function(nameof(DetectStalePendingFiles))]
    public async Task RunAsync(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await watchdog.DetectAsync(DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (RetryableScanProcessingException)
        {
            telemetry.RecordRetry("watchdog");
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            telemetry.RecordRetry("unexpected");
            throw new RetryableScanProcessingException("Unexpected watchdog failure.");
        }
    }
}
