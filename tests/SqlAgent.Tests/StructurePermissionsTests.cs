using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Core.Policy;
using SqlAgent.Host.Components.Shared.Database;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class StructurePermissionsTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public StructurePermissionsTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddLogging();
        _ctx.Services.AddScoped<ScopedRunner>();

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    public void Dispose() { _ctx.Dispose(); _conn.Dispose(); }

    private async Task<Guid> SeedAsync(AllowedDdl allowed = AllowedDdl.None)
    {
        using var scope = _ctx.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        var id = (await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, false), "cs")).Id;
        if (allowed != AllowedDdl.None) await svc.SetAllowedDdlAsync(id, allowed);
        return id;
    }

    private IRenderedComponent<StructurePermissions> Render(Guid id) =>
        _ctx.RenderComponent<StructurePermissions>(p => p.Add(x => x.ConnectionId, id));

    [Fact]
    public async Task All_six_structure_operations_are_off_by_default_and_unsupported_operations_are_explained()
    {
        var panel = Render(await SeedAsync());

        var operationInputs = panel.FindAll("input[type=checkbox][data-testid^=ddl-permission-]")
            .Where(input => input.GetAttribute("data-testid") != "ddl-permission-all").ToList();
        Assert.Equal(6, operationInputs.Count);
        Assert.All(operationInputs, input =>
            Assert.Null(input.GetAttribute("checked")));
        Assert.Contains("views", panel.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXEC", panel.Markup);
        Assert.Contains("GRANT", panel.Markup);
    }

    [Fact]
    public async Task Existing_permissions_are_loaded_and_a_toggle_is_persisted()
    {
        var panel = Render(await SeedAsync(AllowedDdl.CreateTable | AllowedDdl.Truncate));

        Assert.NotNull(panel.Find("[data-testid=ddl-permission-CreateTable][checked]"));
        Assert.NotNull(panel.Find("[data-testid=ddl-permission-Truncate][checked]"));
        Assert.Empty(panel.FindAll("[data-testid=ddl-permission-DropTable][checked]"));

        await panel.InvokeAsync(() => panel.Find("[data-testid=ddl-permission-DropTable]").Change(true));

        using var scope = _ctx.Services.CreateScope();
        var info = await scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>()
            .GetAsync(panel.Instance.ConnectionId);
        Assert.True(info!.AllowedDdl.HasFlag(AllowedDdl.DropTable));
    }

    [Fact]
    public async Task Master_checkbox_selects_all_and_can_clear_all()
    {
        var panel = Render(await SeedAsync());

        await panel.InvokeAsync(() => panel.Find("[data-testid=ddl-permission-all]").Change(true));

        using (var scope = _ctx.Services.CreateScope())
        {
            var info = await scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>()
                .GetAsync(panel.Instance.ConnectionId);
            Assert.Equal(AllowedDdl.All, info!.AllowedDdl);
        }

        await panel.InvokeAsync(() => panel.Find("[data-testid=ddl-permission-all]").Change(false));

        using var verify = _ctx.Services.CreateScope();
        var cleared = await verify.ServiceProvider.GetRequiredService<DatabaseConnectionService>()
            .GetAsync(panel.Instance.ConnectionId);
        Assert.Equal(AllowedDdl.None, cleared!.AllowedDdl);
    }
}
