using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;

namespace SqlAgent.Host.Web;

/// <summary>
/// Writes the tokenized launch URL to a file beside the SQLite store, restricted to the account the
/// host runs as (plus local administrators, who can read anything anyway and are the people who
/// installed the service).
///
/// This exists because the launch token became the whole trust boundary when the named pipe was
/// replaced. A pipe's ACL restricted it to one user; a loopback TCP port is reachable by any local
/// account and any process, so the token is the only thing separating them from the SQL UI. Logging
/// it at Information sent it to whatever providers the host has attached — and
/// <c>AddWindowsService()</c> attaches the Windows Event Log while <c>AddSystemd()</c> routes stdout
/// into the journal, both readable by a wider set of principals than the service account. A file the
/// operator can read is the retrieval path that replaces it.
/// </summary>
public static class LaunchUrlFile
{
    public const string FileName = "launch-url.txt";

    /// <summary>
    /// The directory holding the SQLite store, which is where the launch URL goes. Anything that can
    /// read the store already sits inside the trust boundary, so no new directory is introduced.
    /// </summary>
    public static string ResolveDirectory(IConfiguration configuration)
    {
        var connectionString = configuration["SqlAgent:Storage:ConnectionString"] ?? "Data Source=sqlagent.db";
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        // A relative Data Source (the default) resolves against the process working directory, which
        // is exactly where the .db file itself lands.
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        return string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory;
    }

    /// <summary>Writes <paramref name="url"/> and returns the full path it was written to.</summary>
    public static string Write(string directory, string url)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);

        // Create (or truncate) the file empty first, restrict it, and only then put the token in it.
        // The other order leaves the secret briefly readable under whatever the directory's default
        // permissions happen to be.
        using (new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) { }
        Restrict(path);
        File.WriteAllText(path, url + Environment.NewLine);
        return path;
    }

    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows())
            RestrictWindows(path);
        else
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var security = new FileSecurity();
        // Inheritance off, and no copy of the inherited rules kept: a publish directory under
        // %ProgramFiles% grants BUILTIN\Users read by default, which is precisely the "any local
        // account" this file must not be exposed to.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is { } account)
            security.AddAccessRule(new FileSystemAccessRule(
                account, FileSystemRights.FullControl, AccessControlType.Allow));

        // Without this a LocalSystem service would write a file only LocalSystem can open, and the
        // administrator who installed it could not read the token they need.
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
    }
}
