namespace SqlAgent.Host.Web;

/// <summary>
/// Resolves the address the host listens on. The port is configurable; the address is not — a loopback
/// bind is what keeps the configuration surface off the network until phase 3 adds TLS and sessions.
/// Kept as a pure function so the binding rule is unit-testable: WebApplicationFactory runs on an
/// in-memory TestServer and opens no socket, so it cannot observe the real binding.
/// </summary>
public static class LoopbackUrl
{
    public const int DefaultPort = 5099;
    public const string ConfigKey = "SqlAgent:Web:Port";

    public static string Resolve(IConfiguration configuration) => $"http://127.0.0.1:{ResolvePort(configuration)}";

    /// <summary>
    /// Parses <see cref="ConfigKey"/> into a port number, or throws for a value that cannot be one.
    /// The single source of truth for that parse: HostInfo.Port used to run its own, silently-falls-
    /// back-to-default copy of this logic, so it could report a port <see cref="Resolve"/> would have
    /// refused to bind to at all -- unreachable in the running host, since Program.cs calls
    /// <see cref="Resolve"/> at startup and would fail the process first on the same bad value, but
    /// reachable by constructing HostInfo directly, which tests do. Sharing this parse means the two
    /// can no longer disagree.
    /// </summary>
    public static int ResolvePort(IConfiguration configuration)
    {
        var raw = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultPort;

        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException($"{ConfigKey} must be a TCP port between 1 and 65535, but was '{raw}'.");

        return port;
    }
}
