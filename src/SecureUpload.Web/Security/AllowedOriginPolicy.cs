using Microsoft.Extensions.Options;

namespace SecureUpload.Web.Security;

public sealed class AllowedOriginOptions
{
    public string[] Origins { get; set; } = [];
}

public sealed class AllowedOriginPolicy
{
    private readonly HashSet<string> _origins;

    public AllowedOriginPolicy(IOptions<AllowedOriginOptions> options)
    {
        _origins = options.Value.Origins
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);
        FrameAncestors = _origins.Count == 0
            ? "frame-ancestors 'none'"
            : $"frame-ancestors {string.Join(' ', _origins.Order(StringComparer.Ordinal))}";
    }

    public string FrameAncestors { get; }
    public IReadOnlyCollection<string> Origins => _origins;

    public bool IsAllowed(string? origin) =>
        origin is not null && _origins.Contains(origin);

    public string? GetMessageTarget(string? origin) =>
        IsAllowed(origin) ? origin : null;

    private static string Normalize(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps &&
            !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                $"Allowed origin '{origin}' must be an exact HTTPS origin; HTTP is allowed only for loopback development.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}

public sealed class OriginSecurityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AllowedOriginPolicy policy)
    {
        context.Response.Headers.ContentSecurityPolicy = policy.FrameAncestors;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        var origin = context.Request.Headers.Origin.ToString();
        if (policy.IsAllowed(origin))
        {
            context.Response.Headers.AccessControlAllowOrigin = origin;
            context.Response.Headers.Vary = "Origin";
            context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
            context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
        }

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = policy.IsAllowed(origin)
                ? StatusCodes.Status204NoContent
                : StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }
}
