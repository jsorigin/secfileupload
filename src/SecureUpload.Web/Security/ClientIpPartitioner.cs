using System.Net;
using Microsoft.Extensions.Options;

namespace SecureUpload.Web.Security;

public sealed class ForwardedClientIpOptions
{
    public IPAddress[] TrustedProxies { get; set; } = [];
}

public sealed class ClientIpPartitioner(IOptions<ForwardedClientIpOptions> options)
{
    private readonly HashSet<IPAddress> _trustedProxies = options.Value.TrustedProxies.ToHashSet();

    public string GetPartition(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer is not null && _trustedProxies.Contains(peer))
        {
            var chain = context.Request.Headers["X-Forwarded-For"]
                .ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var index = chain.Length - 1; index >= 0; index--)
            {
                if (IPAddress.TryParse(chain[index], out var forwarded) &&
                    !_trustedProxies.Contains(forwarded))
                {
                    return forwarded.ToString();
                }
            }
        }

        return peer?.ToString() ?? "unknown";
    }
}

public sealed class UploadRateLimitOptions
{
    public int RequestsPerIpPerWindow { get; set; } = 10;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
