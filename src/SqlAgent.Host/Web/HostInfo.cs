using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace SqlAgent.Host.Web;

/// <summary>
/// Facts about this host that the UI reports but cannot change: the OS account it runs as, where the
/// store lives, and where it is listening. Everything here is host configuration, owned by
/// appsettings.json and the runbook, so the About dialog and the Settings page read it rather than
/// offering a form that would need its own validation and restart story.
/// </summary>
public sealed class HostInfo(IConfiguration configuration)
{
    public string AccountName { get; } = Environment.UserName;

    public string MachineName { get; } = Environment.MachineName;

    /// <summary>Up to two letters for the avatar. Falls back to "?" so an empty account name (possible
    /// in some service contexts) cannot render a blank circle.</summary>
    public string Initials { get; } = Initialize(Environment.UserName);

    public string Version { get; } =
        typeof(HostInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HostInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Directory holding the SQLite store, resolved the same way LaunchUrlFile resolves it.</summary>
    public string StoreDirectory { get; } = LaunchUrlFile.ResolveDirectory(configuration);

    // Port and BindUrl both derive from LoopbackUrl's own parse (Task 6 review finding) rather than each
    // running its own copy of the port-parsing logic: two independent parsers with different failure
    // policies (this used to silently fall back to DefaultPort on a bad value while LoopbackUrl.Resolve
    // threw for the exact same value) can only ever drift apart, reporting a port the URL disagrees
    // with. Sharing the parse makes that impossible by construction.
    public int Port { get; } = LoopbackUrl.ResolvePort(configuration);

    public string BindUrl { get; } = LoopbackUrl.Resolve(configuration);

    private static string Initialize(string account)
    {
        // Split on the separators real account names use — "ada.lovelace", "ada_lovelace",
        // "DOMAIN\ada" — then take the first letter of the first two parts.
        var parts = account.Split(['.', '_', '-', ' ', '\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var letters = parts.Take(2).Select(p => char.ToUpperInvariant(p[0]));
        return string.Concat(letters);
    }
}
