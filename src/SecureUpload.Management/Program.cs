using System.Diagnostics;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SecureUpload.Core.Storage;
using SecureUpload.Core.Telemetry;
using SecureUpload.Management.Files;
using SecureUpload.Management.Security;
using SecureUpload.Management.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new AutoValidateAntiforgeryTokenAttribute());
});
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
builder.Services.AddManagementAuthorization(builder.Configuration);
var inventoryOptions = builder.Configuration
    .GetSection(FileInventoryOptions.SectionName)
    .Get<FileInventoryOptions>() ?? new();
inventoryOptions.Validate();
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(inventoryOptions));
builder.Services.AddSingleton(TimeProvider.System);
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
builder.Services.AddSingleton<ManagementTelemetry>();
builder.Services.AddSingleton<FileInventoryService>();
builder.Services.AddSingleton<CleanFileDownloadService>();
builder.Services.AddSingleton<FileDeletionService>();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-SecureUpload.Management.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("An unexpected error occurred.");
    });
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'; object-src 'none';";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.OnStarting(static state =>
    {
        var httpContext = (HttpContext)state;
        if (httpContext.User.Identity?.IsAuthenticated == true &&
            httpContext.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            httpContext.Response.Headers.CacheControl = "no-store, no-cache";
            httpContext.Response.Headers.Pragma = "no-cache";
            httpContext.Response.Headers.Expires = "0";
        }

        return Task.CompletedTask;
    }, context);

    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.Use(async (context, next) =>
{
    try
    {
        await next();
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
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.Run();

static Uri RequiredStorageUri(IConfiguration configuration, string key)
{
    var rawValue = configuration[key];
    if (!Uri.TryCreate(rawValue, UriKind.Absolute, out var uri))
    {
        throw new InvalidOperationException($"{key} must be an absolute URI.");
    }

    return uri;
}

public partial class Program;
