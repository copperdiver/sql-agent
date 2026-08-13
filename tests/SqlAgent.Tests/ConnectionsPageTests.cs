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
    private readonly PostgresProviderStub _providerStub = new();

    public ConnectionsPageTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        // Registered as a fixed instance (not by type) so individual tests can reach in and change
        // what TestConnectionAsync returns — see PostgresProviderStub.Result below.
        _ctx.Services.AddSingleton<IDatabaseProvider>(_providerStub);
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<ConnectionTester>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        // The page logs the exceptions it now catches instead of letting them escape to the boundary.
        _ctx.Services.AddLogging();

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

        // This is the assertion the write-only test could not make: it never clicked Edit, so it only
        // ever inspected a blank *new*-connection form. Clicking Edit is the one path that could
        // populate the secret field from the server, and it must leave it empty.
        Assert.True(string.IsNullOrEmpty(page.Find("input[type=password]").GetAttribute("value")));

        // Change the name but leave the connection-string field blank.
        var nameInput = page.Find("input");
        nameInput.Change("prod-analytics-renamed");

        FindButton(page, "Save").Click();

        Assert.Contains("prod-analytics-renamed", page.Markup);

        var stored = GetStoredSecretAsync(id).GetAwaiter().GetResult();
        Assert.Equal("Host=localhost;Password=super-secret", stored);
    }

    [Fact]
    public void A_successful_create_clears_the_form_so_pressing_Save_again_cannot_duplicate_the_row()
    {
        var page = _ctx.RenderComponent<Connections>();

        page.Find("input").Change("new-conn");
        page.Find("input[type=password]").Change("Host=localhost;Password=super-secret");
        FindButton(page, "Save").Click();

        Assert.Contains("Saved.", page.Markup);
        // The form used to keep the values just saved, so Save again attempted an identical second
        // connection — blocked only incidentally by the blank-secret check, not by design.
        Assert.Equal("", page.Find("input").GetAttribute("value"));
        Assert.True(string.IsNullOrEmpty(page.Find("input[type=password]").GetAttribute("value")));

        FindButton(page, "Save").Click();

        Assert.Contains("connection_string_required", page.Markup);
        Assert.Single(GetSavedConnectionsAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void An_expected_refusal_is_rendered_through_OutcomeMessage_with_its_code()
    {
        var page = _ctx.RenderComponent<Connections>();

        page.Find("input").Change("new-conn");
        FindButton(page, "Save").Click();

        // Not hand-built prose in a bare <p role="status">: this page routes refusals through the same
        // component the SQL and chat pages use, so the user sees a stable code and can tell a
        // deliberate refusal from a crash.
        var outcome = page.Find(".outcome");
        Assert.Contains("A connection string is required for a new connection.", outcome.TextContent);
        Assert.Equal("connection_string_required", page.Find(".outcome .outcome-code").TextContent);
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
    public void Deleting_the_row_open_in_the_edit_form_resets_the_form_to_new_connection()
    {
        var id = SeedAsync("prod-analytics", isReadOnly: true).GetAwaiter().GetResult();

        var page = _ctx.RenderComponent<Connections>();
        FindButton(page, "Edit").Click();
        Assert.Contains("Edit connection", page.Markup);

        FindButton(page, "Delete").Click();

        Assert.Contains("New connection", page.Markup);
        Assert.DoesNotContain("Edit connection", page.Markup);
        // The name field must have been cleared, not left holding the deleted row's name — that
        // would be a form still effectively bound to the id that no longer exists.
        var nameInput = page.Find("input");
        Assert.Equal("", nameInput.GetAttribute("value"));
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

    [Fact]
    public void Testing_a_reachable_but_rejecting_server_shows_the_failure_reason()
    {
        SeedAsync("prod-analytics", isReadOnly: true).GetAwaiter().GetResult();
        _providerStub.Result = () => ConnectionTestResult.Fail("password authentication failed", 8);

        var page = _ctx.RenderComponent<Connections>();
        FindButton(page, "Test").Click();

        Assert.Contains("Connection failed: password authentication failed", page.Markup);
    }

    [Fact]
    public void Testing_a_connection_with_no_stored_secret_reports_it_is_missing_not_a_connection_failure()
    {
        var id = SeedAsync("prod-analytics", isReadOnly: true).GetAwaiter().GetResult();
        // Construct the "secret missing" state honestly through the real storage services: create a
        // normal connection (which writes a secret), then remove only the secret, leaving the
        // DatabaseConnection row in place — exactly the shape ConnectionTester.TestSavedAsync needs
        // to hit its second null-return branch (info found, connectionString resolves to null).
        RemoveStoredSecretAsync(id).GetAwaiter().GetResult();

        var page = _ctx.RenderComponent<Connections>();
        FindButton(page, "Test").Click();

        Assert.Contains("Connection or its secret is missing.", page.Markup);
        Assert.DoesNotContain("Connection failed", page.Markup);
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

    /// <summary>
    /// Deletes only the secret behind a saved connection, via the real ISecretStore and the real
    /// DatabaseConnection row's ConnectionStringSecretRef — leaving the connection itself intact.
    /// This is the exact state ConnectionTester.TestSavedAsync needs to see to return null because
    /// ResolveConnectionStringAsync resolves to null, distinct from the connection not existing at all.
    /// </summary>
    private async Task RemoveStoredSecretAsync(Guid id)
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var entity = await db.DatabaseConnections.FindAsync(id);
        await secrets.DeleteAsync(entity!.ConnectionStringSecretRef);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}

/// <summary>
/// Provider double: the page never reaches a real database in these tests. <see cref="Result"/> is
/// settable per test so both success and reachable-but-rejecting outcomes can be pinned, not just
/// the always-Ok default. Not `file`-scoped (unlike the original) because a private field of
/// <see cref="ConnectionsPageTests"/> now needs to reference this type, and C# forbids file-local
/// types in any member signature of a non-file-local type.
/// </summary>
sealed class PostgresProviderStub : IDatabaseProvider
{
    public Func<ConnectionTestResult> Result { get; set; } = () => ConnectionTestResult.Ok("PostgreSQL 16.0", 12);

    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;
    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(Result());
    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema([]));
    public Task<QueryResultSet> ExecuteQueryAsync(string cs, string sql, QueryExecutionOptions o, CancellationToken ct = default)
        => Task.FromResult(new QueryResultSet([], [], false));
}
