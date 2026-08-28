using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Instrumentation.Http;
using SecureUpload.Core.Storage;
using SecureUpload.Core.Telemetry;
using SecureUpload.Processor.Scanning;
using SecureUpload.Processor.Telemetry;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration(configuration =>
    {
        configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        configuration.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        var openTelemetry = services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(TelemetryNames.ActivitySource))
            .WithMetrics(metrics => metrics.AddMeter(TelemetryNames.Meter));
        if (!string.IsNullOrWhiteSpace(
                context.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            openTelemetry.UseAzureMonitor(
                options => options.Credential = new DefaultAzureCredential());
        }
        services.Configure<HttpClientTraceInstrumentationOptions>(options =>
            options.EnrichWithHttpRequestMessage = TelemetryPathRedactor.RedactHttpDependency);
        var configuration = context.Configuration
            .GetSection("SecureUpload")
            .Get<ProcessorConfiguration>()
            ?? throw new InvalidOperationException("SecureUpload configuration is required.");
        var options = configuration.ToProcessorOptions();
        options.Validate();

        var credential = new DefaultAzureCredential();
        services.AddSingleton(options);
        services.AddSingleton(new TelemetryCorrelation(
            context.Configuration["Telemetry:CorrelationKey"]
            ?? throw new InvalidOperationException("Telemetry:CorrelationKey is required.")));
        services.AddSingleton<ScanTelemetry>();
        services.AddSingleton(new BlobStorageOptions
        {
            PendingContainerName = configuration.PendingContainerName,
            CleanContainerName = configuration.CleanContainerName,
            QuarantineContainerName = configuration.QuarantineContainerName
        });
        services.AddSingleton(new BlobServiceClient(configuration.BlobServiceUri, credential));
        services.AddSingleton(new TableServiceClient(configuration.TableServiceUri, credential));
        services.AddSingleton<IBlobFileStore>(provider =>
            new AzureBlobFileStore(
                provider.GetRequiredService<BlobServiceClient>(),
                provider.GetRequiredService<BlobStorageOptions>()));
        services.AddSingleton<IFileStatusStore>(provider =>
            new AzureTableFileStatusStore(
                provider.GetRequiredService<TableServiceClient>(),
                configuration.StatusTableName));
        services.AddSingleton<BlobPromotionService>();
        services.AddSingleton<FileDeletionCleanup>(provider =>
            new FileDeletionCleanup(
                provider.GetRequiredService<IBlobFileStore>(),
                options.MaximumConcurrencyAttempts));
        services.AddSingleton<DeletionProcessor>();
        services.AddSingleton<ScanResultProcessor>();
        services.AddSingleton<StalePendingWatchdog>();
    })
    .Build();

await host.RunAsync();

internal sealed class ProcessorConfiguration
{
    public required string ExpectedTopic { get; init; }
    public required Uri BlobServiceUri { get; init; }
    public required Uri TableServiceUri { get; init; }
    public required string StatusTableName { get; init; }
    public string? StorageAccountName { get; init; }
    public string PendingContainerName { get; init; } = "pending";
    public string CleanContainerName { get; init; } = "clean";
    public string QuarantineContainerName { get; init; } = "quarantine";
    public int MaximumConcurrencyAttempts { get; init; } = 5;
    public TimeSpan ScanWatchdogThreshold { get; init; } = TimeSpan.FromHours(3);

    public ScanProcessorOptions ToProcessorOptions() =>
        new()
        {
            ExpectedTopic = ExpectedTopic,
            BlobServiceUri = BlobServiceUri,
            StorageAccountName = StorageAccountName,
            PendingContainerName = PendingContainerName,
            MaximumConcurrencyAttempts = MaximumConcurrencyAttempts,
            ScanWatchdogThreshold = ScanWatchdogThreshold
        };
}
