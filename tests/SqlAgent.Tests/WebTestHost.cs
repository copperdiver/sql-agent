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
