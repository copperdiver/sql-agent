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

    public static string Resolve(IConfiguration configuration)
    {
        var raw = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
            return $"http://127.0.0.1:{DefaultPort}";

        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException($"{ConfigKey} must be a TCP port between 1 and 65535, but was '{raw}'.");

        return $"http://127.0.0.1:{port}";
    }
}
