namespace SqlAgent.Host.Web;

/// <summary>
/// Rejects requests whose Host or Origin is not local. A loopback port is reachable from any page the
/// user has open, so without this a hostile site could drive the configuration UI, and a name resolving
/// to 127.0.0.1 could defeat the browser's same-origin rule (DNS rebinding). Runs ahead of everything
/// else, including the Blazor WebSocket negotiation.
/// </summary>
public sealed class LocalOriginMiddleware(RequestDelegate next)
{
    private static readonly string[] LocalHosts = ["127.0.0.1", "localhost", "[::1]"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsLocalHost(context.Request.Host.Host) || !IsLocalOrigin(context.Request.Headers.Origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }

    private static bool IsLocalHost(string host) =>
        LocalHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    private static bool IsLocalOrigin(string? origin)
    {
        // No Origin at all is normal for a top-level navigation, so absence is not a rejection.
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLocalHost(uri.Host);
    }
}
