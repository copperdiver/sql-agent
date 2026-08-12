using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

/// <summary>
/// LaunchUrlFile is the most security-sensitive new file in the branch: it is the on-disk copy of
/// the token that is the whole trust boundary around the loopback port. It previously had no
/// automated coverage at all, only a manual Get-Acl pass. These tests pin the mechanics that matter:
/// the file is created with the URL in it, its permissions are restricted on whichever platform is
/// running, and it can be cleanly removed (including when there is nothing to remove).
/// </summary>
public sealed class LaunchUrlFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"sqlagent-launchurl-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Write_creates_the_file_with_the_url_in_it()
    {
        var path = LaunchUrlFile.Write(_dir, "http://127.0.0.1:5099/?token=abc123");

        Assert.Equal(Path.Combine(_dir, LaunchUrlFile.FileName), path);
        Assert.True(File.Exists(path));
        Assert.Contains("http://127.0.0.1:5099/?token=abc123", File.ReadAllText(path));
    }

    [Fact]
    public void Write_restricts_the_file_to_the_owner_on_unix()
    {
        if (OperatingSystem.IsWindows()) return; // Windows has its own ACL model — see the DACL test.

        var path = LaunchUrlFile.Write(_dir, "http://127.0.0.1:5099/?token=abc123");

        // Anything beyond UserRead|UserWrite (group or other access) would let a second local
        // account read the token off disk, which is exactly the exposure this file exists to avoid.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Write_breaks_dacl_inheritance_and_grants_only_the_current_user_and_administrators_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix's mode bits are covered by the test above.

        var path = LaunchUrlFile.Write(_dir, "http://127.0.0.1:5099/?token=abc123");

        var security = new FileInfo(path).GetAccessControl();
        // Protection off would mean the directory's inherited rules still apply — a publish
        // directory under %ProgramFiles% grants BUILTIN\Users read by default, which is precisely
        // the "any local account" this file must not be exposed to.
        Assert.True(security.AreAccessRulesProtected);

        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .ToList();

        var currentUser = WindowsIdentity.GetCurrent().User!.Value;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;

        Assert.Contains(rules, r => r.IdentityReference.Value == currentUser);
        Assert.Contains(rules, r => r.IdentityReference.Value == admins);
        // No third identity was granted access — the two above are the only ones allowed to read it.
        Assert.All(rules, r => Assert.True(r.IdentityReference.Value == currentUser || r.IdentityReference.Value == admins));
    }

    [Fact]
    public void Delete_removes_the_file()
    {
        var path = LaunchUrlFile.Write(_dir, "http://127.0.0.1:5099/?token=abc123");

        var result = LaunchUrlFile.Delete(_dir);

        Assert.True(result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Deleting_a_file_that_was_never_written_is_a_no_op_not_a_throw()
    {
        Directory.CreateDirectory(_dir); // directory exists, but launch-url.txt was never written to it

        var result = LaunchUrlFile.Delete(_dir);

        Assert.True(result);
    }

    [Fact]
    public void Deleting_from_a_directory_that_does_not_exist_is_also_not_a_throw()
    {
        var result = LaunchUrlFile.Delete(_dir); // _dir was never created at all

        Assert.True(result);
    }
}
