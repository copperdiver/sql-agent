using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ProjectSectionTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public ProjectSectionTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<ChatService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<DialogService>();
        _ctx.Services.AddScoped<ShortcutService>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void With_no_projects_only_the_heading_and_its_add_button_render()
    {
        // "No projects yet" tells the user nothing they cannot already see, and the history section
        // below already covers the genuinely empty case.
        var section = _ctx.RenderComponent<ProjectSection>();

        Assert.Empty(section.FindAll(".project-row"));
        Assert.Single(section.FindAll("[data-testid=project-add]"));
        Assert.DoesNotContain("No projects", section.Markup);
    }

    [Fact]
    public async Task A_project_shows_its_name_and_how_many_chats_are_in_it()
    {
        await SeedProjectAsync("quarterly", "first", "second");

        var section = _ctx.RenderComponent<ProjectSection>();

        Assert.Contains("quarterly", section.Markup);
        Assert.Contains("2", section.Find(".project-count").TextContent);
    }

    [Fact]
    public async Task A_project_is_collapsed_until_it_is_opened()
    {
        // Expanding every project on load would bury the history section under everything the user has
        // ever filed.
        var id = await SeedProjectAsync("quarterly", "first");
        var section = _ctx.RenderComponent<ProjectSection>();

        Assert.Empty(section.FindAll(".chat-row"));

        await section.Find(".project-open").ClickAsync(new MouseEventArgs());
        Assert.Single(section.FindAll(".chat-row"));

        await section.Find(".project-open").ClickAsync(new MouseEventArgs());
        Assert.Empty(section.FindAll(".chat-row"));
    }

    [Fact]
    public async Task Creating_a_project_goes_through_a_dialog_and_appears_in_the_list()
    {
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var section = _ctx.RenderComponent<ProjectSection>();

        section.Find("[data-testid=project-add]").Click();
        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("quarterly");
        await dialog.Find("[data-testid=name-save]").ClickAsync(new MouseEventArgs());

        Assert.Contains("quarterly", section.Markup);
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task A_name_already_taken_is_reported_in_the_dialog_which_stays_open()
    {
        // The alternative — closing the dialog and quietly doing nothing — is how a user concludes the
        // button is broken.
        await SeedProjectAsync("quarterly");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var section = _ctx.RenderComponent<ProjectSection>();

        section.Find("[data-testid=project-add]").Click();
        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("quarterly");
        await dialog.Find("[data-testid=name-save]").ClickAsync(new MouseEventArgs());

        Assert.NotNull(dialogs.Current);
        Assert.Contains("already", _ctx.Render(dialogs.Current!).Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await ListProjectsAsync());
    }

    [Fact]
    public async Task Deleting_a_project_offers_both_outcomes_and_keeping_the_chats_returns_them()
    {
        var id = await SeedProjectAsync("quarterly", "kept");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var section = _ctx.RenderComponent<ProjectSection>();

        section.Find(".project-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        Assert.Contains("quarterly", dialog.Markup);
        await dialog.Find("[data-testid=project-delete-keep]").ClickAsync(new MouseEventArgs());

        Assert.Empty(await ListProjectsAsync());
        using var scope = _ctx.Services.CreateScope();
        var history = await scope.ServiceProvider.GetRequiredService<ChatService>().ListHistoryAsync();
        Assert.Contains(history, c => c.Title == "kept");
    }

    [Fact]
    public async Task Deleting_a_project_with_its_chats_takes_them_too()
    {
        await SeedProjectAsync("quarterly", "doomed");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var section = _ctx.RenderComponent<ProjectSection>();
        section.Find(".project-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=project-delete-with-chats]").ClickAsync(new MouseEventArgs());

        Assert.Empty(await ListProjectsAsync());
        using var scope = _ctx.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ChatService>().ListHistoryAsync());
    }

    [Fact]
    public async Task Deleting_an_expanded_project_removes_it_and_its_chats_from_the_section()
    {
        // ReloadAsync only keeps an expanded id that still resolves to a project, dropping the rest along
        // with their cached chats — this is the path that guard exists for: deleting the very project the
        // user has open, not one collapsed elsewhere in the list.
        await SeedProjectAsync("quarterly", "kept");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var section = _ctx.RenderComponent<ProjectSection>();

        await section.Find(".project-open").ClickAsync(new MouseEventArgs());
        Assert.Single(section.FindAll(".chat-row"));

        section.Find(".project-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();
        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=project-delete-keep]").ClickAsync(new MouseEventArgs());

        Assert.Empty(section.FindAll(".project-row"));
        Assert.Empty(section.FindAll(".chat-row"));
    }

    [Fact]
    public async Task A_chat_moved_out_of_an_expanded_project_disappears_from_its_list()
    {
        // The companion case to the one above: the project itself survives, but ReloadAsync's per-expanded-
        // id refresh (run every time something says the chats changed) has to re-read this project's chats
        // too, not just recompute its count, or a chat moved elsewhere would keep showing under a project
        // it already left.
        await SeedProjectAsync("quarterly", "wandering");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var section = _ctx.RenderComponent<ProjectSection>();

        await section.Find(".project-open").ClickAsync(new MouseEventArgs());
        Assert.Single(section.FindAll(".chat-row"));

        section.Find(".chat-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Move")).Click();
        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.FindAll("[data-testid=move-target]")
            .First(b => b.TextContent.Contains("No project")).ClickAsync(new MouseEventArgs());

        Assert.Empty(section.FindAll(".chat-row"));
        Assert.Equal("0", section.Find(".project-count").TextContent);
    }

    [Fact]
    public async Task Requesting_a_project_expanded_opens_it_and_shows_its_chats()
    {
        // SearchDialogTests only proves the request lands in AppState.ProjectToExpand; this is the
        // consuming side of that handshake -- the section that actually reads the request and opens the
        // project a search hit pointed at, since there is no project route to navigate to instead.
        var id = await SeedProjectAsync("quarterly", "first");
        var section = _ctx.RenderComponent<ProjectSection>();
        Assert.Empty(section.FindAll(".chat-row"));

        await section.InvokeAsync(() =>
            _ctx.Services.GetRequiredService<AppState>().RequestProjectExpanded(id));

        Assert.Single(section.FindAll(".chat-row"));
    }

    [Fact]
    public async Task The_list_re_reads_itself_when_something_says_the_chats_changed()
    {
        // A chat moved into a project from the history section's own row has to change this section's
        // counts, and the two are siblings that only meet through AppState.
        var section = _ctx.RenderComponent<ProjectSection>();
        await SeedProjectAsync("brand new");

        await section.InvokeAsync(_ctx.Services.GetRequiredService<AppState>().NotifyChatsChanged);

        Assert.Contains("brand new", section.Markup);
    }

    private async Task<Guid> SeedProjectAsync(string name, params string[] chatTitles)
    {
        using var scope = _ctx.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<ProjectService>();
        var chats = scope.ServiceProvider.GetRequiredService<ChatService>();
        var created = await projects.CreateProjectAsync(name);
        foreach (var title in chatTitles)
            await projects.MoveChatAsync(await chats.CreateChatAsync(title), created.Id!.Value);
        return created.Id!.Value;
    }

    private async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ProjectService>().ListProjectsAsync();
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
