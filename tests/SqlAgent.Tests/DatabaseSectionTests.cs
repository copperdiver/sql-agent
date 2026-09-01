using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class DatabaseSectionTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.BunitContext _ctx = new();

    public DatabaseSectionTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        // The section injects ILogger<DatabaseSection>. AddLogging registers the open generic, so every
        // component in these tests gets one without naming them individually.
        _ctx.Services.AddLogging();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    public void Dispose() { _ctx.Dispose(); _conn.Dispose(); }

    private async Task<Guid> SeedAsync(string name, DatabaseProviderType type = DatabaseProviderType.Postgres)
    {
        using var scope = _ctx.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        return (await svc.CreateAsync(new DatabaseConnectionInput(name, type, true), "cs")).Id;
    }

    [Fact]
    public void With_no_databases_only_the_heading_and_its_add_button_render()
    {
        // Same judgement the project section made: "no databases yet" tells the user nothing the empty
        // list does not already say, and the add button is right there.
        var section = _ctx.Render<DatabaseSection>();

        Assert.Empty(section.FindAll(".database-row"));
        Assert.Single(section.FindAll("[data-testid=database-add]"));
    }

    [Fact]
    public async Task A_database_shows_its_name_and_its_engine()
    {
        await SeedAsync("warehouse", DatabaseProviderType.SqlServer);

        var section = _ctx.Render<DatabaseSection>();

        var row = Assert.Single(section.FindAll(".database-row"));
        Assert.Contains("warehouse", row.TextContent);
        Assert.Contains("SQL Server", row.TextContent);
    }

    [Fact]
    public async Task A_row_links_to_that_database_and_the_add_button_to_a_new_one()
    {
        var id = await SeedAsync("warehouse");

        var section = _ctx.Render<DatabaseSection>();

        Assert.Equal($"/database/{id}", section.Find(".database-open").GetAttribute("href"));
        Assert.Equal("/database", section.Find("[data-testid=database-add]").GetAttribute("href"));
    }

    [Fact]
    public async Task A_database_is_untested_until_something_records_a_test()
    {
        var id = await SeedAsync("warehouse");
        var section = _ctx.Render<DatabaseSection>();

        Assert.Contains("untested", section.Find(".database-dot").GetAttribute("class"));

        var state = _ctx.Services.GetRequiredService<AppState>();
        await section.InvokeAsync(() => state.RecordTest(id, success: true));

        Assert.Contains("ok", section.Find(".database-dot").GetAttribute("class"));
    }

    [Fact]
    public async Task A_failed_test_shows_a_failed_dot()
    {
        var id = await SeedAsync("warehouse");
        var section = _ctx.Render<DatabaseSection>();
        var state = _ctx.Services.GetRequiredService<AppState>();

        await section.InvokeAsync(() => state.RecordTest(id, success: false));

        Assert.Contains("failed", section.Find(".database-dot").GetAttribute("class"));
    }

    [Fact]
    public async Task The_dot_state_is_announced_not_only_coloured()
    {
        // A coloured dot with no text is invisible to a screen reader and to anyone who cannot tell the
        // two colours apart.
        var id = await SeedAsync("warehouse");
        var section = _ctx.Render<DatabaseSection>();
        var state = _ctx.Services.GetRequiredService<AppState>();

        await section.InvokeAsync(() => state.RecordTest(id, success: false));

        Assert.Contains("failed", section.Find(".database-dot .sr-only").TextContent,
            StringComparison.OrdinalIgnoreCase);

        // aria-hidden on an ancestor prunes every descendant from the accessibility tree regardless of
        // the descendant's own visibility or clip state -- so the assertion above alone cannot tell a
        // genuinely announced label apart from one that sits in the DOM, matched by this selector, but is
        // invisible to a screen reader because .database-dot itself carries aria-hidden="true". The label
        // is a direct child of .database-dot, so the dot is the only ancestor between it and the row link
        // that could carry the attribute.
        Assert.Null(section.Find(".database-dot").GetAttribute("aria-hidden"));
    }

    [Fact]
    public async Task The_list_re_reads_itself_when_the_connection_set_changes()
    {
        // The section is a sibling of every page, not a child, and MainLayout is not recreated across
        // navigation — the same reason HistorySection and the old rail both subscribe.
        var section = _ctx.Render<DatabaseSection>();
        Assert.Empty(section.FindAll(".database-row"));

        await SeedAsync("warehouse");
        var state = _ctx.Services.GetRequiredService<AppState>();
        await section.InvokeAsync(state.NotifyConnectionsChanged);

        Assert.Single(section.FindAll(".database-row"));
    }

    [Fact]
    public async Task The_list_collapses_and_expands()
    {
        await SeedAsync("warehouse");
        var section = _ctx.Render<DatabaseSection>();

        Assert.Single(section.FindAll(".database-row"));

        await section.Find("[data-testid=databases-toggle]").ClickAsync(new MouseEventArgs());
        Assert.Empty(section.FindAll(".database-row"));

        await section.Find("[data-testid=databases-toggle]").ClickAsync(new MouseEventArgs());
        Assert.Single(section.FindAll(".database-row"));
    }
}
