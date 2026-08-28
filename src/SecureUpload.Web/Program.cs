using System.Net;
using System.Threading.RateLimiting;
using System.Diagnostics;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Instrumentation.Http;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Core.Telemetry;
using SecureUpload.Web.Endpoints;
using SecureUpload.Web.Security;
using SecureUpload.Web.Telemetry;
using SecureUpload.Web.Uploads;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddProblemDetails();
var openTelemetry = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(TelemetryNames.ActivitySource))
    .WithMetrics(metrics => metrics.AddMeter(TelemetryNames.Meter));
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    openTelemetry.UseAzureMonitor(options => options.Credential = new DefaultAzureCredential());
}
builder.Services.Configure<HttpClientTraceInstrumentationOptions>(options =>
    options.EnrichWithHttpRequestMessage = TelemetryPathRedactor.RedactHttpDependency);
builder.Services.AddHostWorkloadAuthorization(builder.Configuration);
builder.Services.Configure<FilePolicyOptions>(builder.Configuration.GetSection("FilePolicy"));
builder.Services.Configure<AllowedOriginOptions>(builder.Configuration.GetSection("AllowedOrigins"));
builder.Services.Configure<ForwardedClientIpOptions>(builder.Configuration.GetSection("ForwardedClientIp"));
builder.Services.Configure<UploadRateLimitOptions>(builder.Configuration.GetSection("RateLimits"));
builder.Services.Configure<UploadAdmissionOptions>(builder.Configuration.GetSection("Admission"));
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = FilePolicyOptions.DefaultMaximumFileSizeBytes + (1024 * 1024);
    options.MultipartHeadersLengthLimit = 16 * 1024;
});

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = FilePolicyOptions.DefaultMaximumFileSizeBytes + (1024 * 1024));

builder.Services.AddSingleton<AllowedOriginPolicy>();
builder.Services.AddSingleton<ClientIpPartitioner>();
builder.Services.AddSingleton<IUploadAdmissionStore>(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var serviceUri = RequiredStorageUri(configuration, "Storage:TableServiceUri");
    var tableName = RequiredSetting(configuration, "Storage:UploadAdmissionTableName");
    return new AzureTableUploadAdmissionStore(
        new TableClient(serviceUri, tableName, new DefaultAzureCredential()),
        services.GetRequiredService<IOptions<UploadAdmissionOptions>>());
});
builder.Services.AddSingleton<UploadAdmissionController>();
builder.Services.AddSingleton<UploadPolicyValidator>();
builder.Services.AddSingleton(new TelemetryCorrelation(
    builder.Configuration["Telemetry:CorrelationKey"]
    ?? throw new InvalidOperationException("Telemetry:CorrelationKey is required.")));
builder.Services.AddSingleton<UploadTelemetry>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<StreamingUploadService>();

builder.Services.AddSingleton<IBlobFileStore>(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var serviceUri = RequiredStorageUri(configuration, "Storage:BlobServiceUri");
    return new AzureBlobFileStore(
        new BlobServiceClient(serviceUri, new DefaultAzureCredential()),
        configuration.GetSection("Storage").Get<BlobStorageOptions>());
});
builder.Services.AddSingleton<IFileStatusStore>(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var serviceUri = RequiredStorageUri(configuration, "Storage:TableServiceUri");
    return new AzureTableFileStatusStore(
        new TableServiceClient(serviceUri, new DefaultAzureCredential()),
        configuration["Storage:StatusTableName"] ?? "filestatus");
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = static (context, _) =>
    {
        context.HttpContext.RequestServices
            .GetRequiredService<UploadTelemetry>()
            .RecordRateLimited("request-budget");
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("upload-ip", context =>
    {
        var settings = context.RequestServices.GetRequiredService<IOptions<UploadRateLimitOptions>>().Value;
        var partitioner = context.RequestServices.GetRequiredService<ClientIpPartitioner>();
        return RateLimitPartition.GetFixedWindowLimiter(
            partitioner.GetPartition(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.RequestsPerIpPerWindow,
                Window = settings.Window,
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
    options.AddPolicy("status-id", context =>
    {
        var stableId = context.Request.RouteValues["stableId"]?.ToString() ?? "invalid";
        return RateLimitPartition.GetFixedWindowLimiter(
            stableId,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();

_ = app.Services.GetRequiredService<IBlobFileStore>();
_ = app.Services.GetRequiredService<IFileStatusStore>();
_ = app.Services.GetRequiredService<IUploadAdmissionStore>();

app.UseExceptionHandler();
app.UseMiddleware<OriginSecurityMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    finally
    {
        if (Activity.Current is { } activity)
        {
            var redactedPath = TelemetryPathRedactor.Redact(context.Request.Path.Value ?? string.Empty);
            activity.SetTag("url.path", redactedPath);
            activity.SetTag("http.target", redactedPath);
            if (activity.DisplayName.Contains(context.Request.Path.Value ?? string.Empty, StringComparison.Ordinal))
            {
                activity.DisplayName = redactedPath;
            }
        }
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapUploadEndpoints();
app.MapHostStatusEndpoints();
app.MapGet("/", () => Results.Redirect("/upload"));

app.Run();

static Uri RequiredStorageUri(IConfiguration configuration, string key)
{
    var value = configuration[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{key} is required.");
    }

    return new Uri(value, UriKind.Absolute);
}

static string RequiredSetting(IConfiguration configuration, string key)
{
    var value = configuration[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{key} is required.");
    }

    return value;
}

public partial class Program;
