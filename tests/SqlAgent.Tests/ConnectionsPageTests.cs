using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Pages;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ConnectionsPageTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public ConnectionsPageTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProvider, PostgresProviderStub>();
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<ConnectionTester>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void Saved_connections_are_listed_with_provider_and_read_only_flag()
    {
        SeedAsync("prod-analytics", isReadOnly: true).GetAwaiter().GetResult();

        var page = _ctx.RenderComponent<Connections>();

        Assert.Contains("prod-analytics", page.Markup);
        Assert.Contains("Postgres", page.Markup);
        Assert.Contains("read-only", page.Markup);
    }

    [Fact]
    public void The_connection_string_is_never_rendered_back()
    {
        SeedAsync("prod-analytics", isReadOnly: true).GetAwaiter().GetResult();

        var page = _ctx.RenderComponent<Connections>();

        // The secret is write-only: it goes in, it never comes back out to the browser.
        Assert.DoesNotContain("Password=", page.Markup);
        Assert.DoesNotContain("super-secret", page.Markup);
    }

    [Fact]
    public void Creating_without_a_connection_string_shows_a_status_message_and_does_not_save()
    {
        var page = _ctx.RenderComponent<Connections>();

        var nameInput = page.Find("input");
        nameInput.Change("new-conn");

        FindButton(page, "Save").Click();

        Assert.Contains("A connection string is required for a new connection.", page.Markup);

        var stillEmpty = GetSavedConnectionsAsync().GetAwaiter().GetResult();
        Assert.Empty(stillEmpty);
    }

    [Fact]
    public void Editing_and_leaving_the_connection_string_blank_keeps_the_stored_secret()
    {
        var id = SeedAsync("prod-analytics", isReadOnly: true).GetAwaiter().GetResult();

        var page = _ctx.RenderComponent<Connections>();
        FindButton(page, "Edit").Click();

        // Change the name but leave the connection-string field blank.
        var nameInput = page.Find("input");
        nameInput.Change("prod-analytics-renamed");

        FindButton(page, "Save").Click();

        Assert.Contains("prod-analytics-renamed", page.Markup);

        var stored = GetStoredSecretAsync(id).GetAwaiter().GetResult();
        Assert.Equal("Host=localhost;Password=super-secret", stored);
    }

    [Fact]
    public void Deleting_the_currently_selected_connection_clears_the_selection()
    {
        var id = SeedAsync("prod-analytics", isReadOnly: true).GetAwaiter().GetResult();

        // bUnit resolves a component's @inject services directly from ctx.Services (no per-render
        // scope), so an AddScoped registration behaves like a singleton for the test's lifetime and
        // this is the same AppState instance the rendered page will inject.
        var state = _ctx.Services.GetRequiredService<AppState>();
        var info = GetSavedConnectionsAsync().GetAwaiter().GetResult().Single(c => c.Id == id);
        state.Select(info);

        var page = _ctx.RenderComponent<Connections>();
        FindButton(page, "Delete").Click();

        Assert.Null(state.ConnectionId);
        Assert.DoesNotContain("prod-analytics", page.Markup);
    }

    [Fact]
    public void Testing_a_saved_connection_shows_the_provider_result_without_leaking_the_secret()
    {
        SeedAsync("prod-analytics", isReadOnly: true).GetAwaiter().GetResult();

        var page = _ctx.RenderComponent<Connections>();
        FindButton(page, "Test").Click();

        Assert.Contains("Connection OK (PostgreSQL 16.0, 12 ms)", page.Markup);
        Assert.DoesNotContain("super-secret", page.Markup);
    }

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<Connections> page, string text) =>
        page.FindAll("button").First(b => b.TextContent.Trim() == text);

    private async Task<Guid> SeedAsync(string name, bool isReadOnly)
    {
        using var scope = _ctx.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        var info = await connections.CreateAsync(
            new DatabaseConnectionInput(name, DatabaseProviderType.Postgres, isReadOnly),
            "Host=localhost;Password=super-secret");
        return info.Id;
    }

    private async Task<IReadOnlyList<DatabaseConnectionInfo>> GetSavedConnectionsAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        return await connections.ListAsync();
    }

    private async Task<string?> GetStoredSecretAsync(Guid id)
    {
        using var scope = _ctx.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        return await connections.ResolveConnectionStringAsync(id);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}

/// <summary>Provider double: the page never reaches a real database in these tests.</summary>
file sealed class PostgresProviderStub : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;
    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok("PostgreSQL 16.0", 12));
    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema([]));
    public Task<QueryResultSet> ExecuteQueryAsync(string cs, string sql, QueryExecutionOptions o, CancellationToken ct = default)
        => Task.FromResult(new QueryResultSet([], [], false));
}
