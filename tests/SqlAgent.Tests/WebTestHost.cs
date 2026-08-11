using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SqlAgent.Tests;

/// <summary>Boots the real host pipeline in memory with a known token and a throwaway SQLite file.</summary>
public sealed class WebTestHost : WebApplicationFactory<Program>
{
    public const string Token = "test-token-value";

    // A directory of its own, not a bare file in %TEMP%: the host writes launch-url.txt beside the
    // store (see LaunchUrlFile), and concurrent factories sharing one directory would fight over it.
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), $"sqlagent-test-{Guid.NewGuid():N}");

    private string DbPath => Path.Combine(_storeDir, "sqlagent.db");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // SQLite will not create the folder for a Data Source path, and the host opens the store
        // before it writes anything beside it.
        Directory.CreateDirectory(_storeDir);
        builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SqlAgent:LocalAuth:Token"] = Token,
            ["SqlAgent:Storage:ConnectionString"] = $"Data Source={DbPath}",
        }));
        // WebApplicationFactory defaults an unset SUT environment to Development, but every real
        // deployment of this host runs in Production: the Windows-service/systemd hosts set it
        // deliberately, and an unpublished `dotnet run` (no launchSettings.json) defaults to it too.
        // Pinning the test host to Production as well means Program.cs's explicit
        // builder.WebHost.UseStaticWebAssets() call is the only thing that can make
        // Framework_assets_are_reachable_without_a_token pass — leaving the default Development
        // environment here would let WebApplication.CreateBuilder()'s own Development-only static web
        // asset wiring paper over that call being missing, which is exactly how the underlying
        // blazor.web.js regression shipped undetected.
        builder.UseEnvironment(Environments.Production);
        return base.CreateHost(builder);
    }

    /// <summary>A client that does not follow redirects, so a 302 to the login path is observable.</summary>
    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // Microsoft.Data.Sqlite pools the native connection handle independently of the DbContext's
        // own disposal, so on Windows the file stays locked until the pool is cleared explicitly.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_storeDir)) Directory.Delete(_storeDir, recursive: true);
    }
}
