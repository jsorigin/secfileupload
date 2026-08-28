using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Web.Files;
using SecureUpload.Web.Security;
using SecureUpload.Web.Telemetry;
using SecureUpload.Web.Uploads;

namespace SecureUpload.Web.Endpoints;

public static class UploadEndpoints
{
    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/uploads", UploadAsync)
            .DisableAntiforgery()
            .RequireRateLimiting("upload-ip");

        endpoints.MapGet("/api/uploads/{stableId}/status", GetStatusAsync)
            .RequireRateLimiting("status-id");

        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        StreamingUploadService uploads,
        UploadAdmissionController admission,
        UploadTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        await using var lease = await admission.TryAcquireAsync(
            context.Request.ContentLength,
            cancellationToken);
        if (!lease.IsAcquired)
        {
            telemetry.RecordRateLimited(lease.Reason ?? "other");
            context.Response.Headers.RetryAfter = "60";
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Uploads are temporarily unavailable.",
                extensions: new Dictionary<string, object?> { ["code"] = lease.Reason });
        }

        var result = await uploads.UploadAsync(context.Request, cancellationToken);
        if (result is UploadResult.Accepted)
        {
            lease.Commit();
        }

        return result switch
        {
            UploadResult.Accepted accepted => Results.Json(
                new { fileId = accepted.StableId, status = accepted.State.ToString().ToLowerInvariant() },
                statusCode: StatusCodes.Status202Accepted),
            UploadResult.Rejected rejected => Results.Problem(
                statusCode: rejected.StatusCode,
                title: rejected.Message,
                extensions: new Dictionary<string, object?> { ["code"] = rejected.Code }),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<IResult> GetStatusAsync(
        string stableId,
        HttpContext context,
        IFileStatusStore statuses,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        try
        {
            FileRecord.ValidateStableId(stableId);
        }
        catch (ArgumentException)
        {
            return Results.NotFound();
        }

        var record = await statuses.GetAsync(stableId, cancellationToken);
        if (!PublicFileStatusMapper.IsPubliclyVisible(record))
        {
            return Results.NotFound();
        }

        if (record.State is FileState.Uploading or FileState.Pending or
            FileState.Promoting or FileState.Quarantining)
        {
            context.Response.Headers.RetryAfter = "2";
        }

        return Results.Ok(PublicFileStatusMapper.ForPolling(record));
    }
}
