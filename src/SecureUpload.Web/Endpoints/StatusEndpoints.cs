using Microsoft.AspNetCore.Authorization;
using SecureUpload.Core.Files;
using SecureUpload.Core.Storage;
using SecureUpload.Web.Files;
using SecureUpload.Web.Security;

namespace SecureUpload.Web.Endpoints;

public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapHostStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/host/files/{stableId}/status", GetHostStatusAsync)
            .RequireAuthorization(HostWorkloadAuthorizationOptions.PolicyName);
        return endpoints;
    }

    private static async Task<IResult> GetHostStatusAsync(
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

        return Results.Ok(PublicFileStatusMapper.ForHost(record));
    }
}
