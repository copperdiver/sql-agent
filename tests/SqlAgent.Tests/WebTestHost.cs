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

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sqlagent-test-{Guid.NewGuid():N}.db");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SqlAgent:LocalAuth:Token"] = Token,
            ["SqlAgent:Storage:ConnectionString"] = $"Data Source={_dbPath}",
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
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
