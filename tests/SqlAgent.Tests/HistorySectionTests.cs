using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;
using SqlAgent.Storage;
// `SqlAgent.Storage.Chat` (the entity, from `using SqlAgent.Storage`) and
// `SqlAgent.Host.Components.Pages.Chat` (the page rendered in the ActiveChatId regression test below)
// share the bare name "Chat". ChatPageTests carries the same alias for the same reason.
using ChatPage = SqlAgent.Host.Components.Pages.Chat;

namespace SqlAgent.Tests;

public class HistorySectionTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public HistorySectionTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<ChatService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<DialogService>();
        // Only Returning_from_a_stored_chat_to_a_new_one_clears_the_sidebars_highlight below renders the
        // real ChatPage, but bUnit's TestServiceProvider refuses any registration made after the first
        // service has been resolved from it — which the constructor itself does two lines down — so these
        // have to be registered here for every test rather than inside that one method.
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry>(new DatabaseProviderRegistry([]));
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Chats_are_listed_under_their_day_headings_newest_first()
    {
        await SeedAsync("this morning", DateTime.UtcNow.AddHours(-2));
        await SeedAsync("last month", DateTime.UtcNow.AddDays(-20));

        var section = _ctx.RenderComponent<HistorySection>();

        var headings = section.FindAll(".history-heading").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal("Today", headings[0]);
        Assert.Contains("Previous 30 days", headings);
        Assert.Contains("this morning", section.Markup);
    }

    [Fact]
    public async Task Nothing_but_an_explanation_shows_when_there_is_no_history()
    {
        var section = _ctx.RenderComponent<HistorySection>();

        Assert.Empty(section.FindAll(".history-row"));
        Assert.Contains("No chats yet", section.Markup);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_open_chat_is_marked_active()
    {
        // The sidebar is the only thing telling the user which conversation they are in once the
        // transcript scrolls past the first message.
        var id = await SeedAsync("open one", DateTime.UtcNow);
        _ctx.Services.GetRequiredService<AppState>().SetActiveChat(id);

        var section = _ctx.RenderComponent<HistorySection>();

        Assert.Contains("active", section.Find(".history-row").ClassName);
    }

    [Fact]
    public async Task The_list_re_reads_itself_when_the_page_says_history_changed()
    {
        // HistorySection and the chat page are siblings under MainLayout, so a chat created on the page
        // reaches this component only through AppState. Without the subscription the sidebar would show
        // whatever existed when the tab was opened, which is exactly the defect SchemaRail already had.
        var section = _ctx.RenderComponent<HistorySection>();
        Assert.Contains("No chats yet", section.Markup);

        await SeedAsync("brand new", DateTime.UtcNow);
        await section.InvokeAsync(_ctx.Services.GetRequiredService<AppState>().NotifyChatsChanged);

        Assert.Contains("brand new", section.Markup);
    }

    [Fact]
    public async Task Renaming_from_the_row_menu_goes_through_a_dialog_and_updates_the_store()
    {
        var id = await SeedAsync("first question, truncated", DateTime.UtcNow);
        var section = _ctx.RenderComponent<HistorySection>();
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();

        section.Find(".history-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Rename")).Click();

        // The dialog is handed to DialogService rather than rendered here: inside the drawer a Modal
        // would resolve its position against the sidebar's transform (Phase A carry-forward 1).
        Assert.NotNull(dialogs.Current);

        // Render what the host would render, and drive it.
        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("quarterly revenue");
        await dialog.Find("[data-testid=rename-save]").ClickAsync(new MouseEventArgs());

        Assert.Equal("quarterly revenue", (await LoadAsync(id)).Title);
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task Deleting_from_the_row_menu_asks_first_and_then_removes_the_chat()
    {
        var id = await SeedAsync("throwaway", DateTime.UtcNow);
        var section = _ctx.RenderComponent<HistorySection>();
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();

        section.Find(".history-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        // The chat is named in the dialog: "are you sure?" with no subject is how the wrong one gets
        // deleted.
        Assert.Contains("throwaway", dialog.Markup);
        await dialog.Find("[data-testid=delete-confirm]").ClickAsync(new MouseEventArgs());

        Assert.Null(await LoadAsync(id));
        Assert.DoesNotContain("throwaway", section.Markup);
    }

    [Fact]
    public async Task Cancelling_the_delete_dialog_keeps_the_chat()
    {
        var id = await SeedAsync("keep me", DateTime.UtcNow);
        var section = _ctx.RenderComponent<HistorySection>();
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        section.Find(".history-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=delete-cancel]").ClickAsync(new MouseEventArgs());

        Assert.NotNull(await LoadAsync(id));
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task Deleting_the_chat_that_is_open_clears_the_selection()
    {
        // Otherwise the sidebar keeps highlighting a row that no longer exists and the page keeps
        // showing a conversation that has been deleted from under it.
        var id = await SeedAsync("open one", DateTime.UtcNow);
        var state = _ctx.Services.GetRequiredService<AppState>();
        state.SetActiveChat(id);
        var section = _ctx.RenderComponent<HistorySection>();
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        section.Find(".history-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=delete-confirm]").ClickAsync(new MouseEventArgs());

        Assert.Null(state.ActiveChatId);
    }

    // --- Task 9 carry-forward: ActiveChatId had a fix (SetActiveChat now runs on the first render of
    // "/", not just on a stored chat's route) with no consumer able to regress it. HistorySection's
    // active-row rendering is the first one, so this is the first test that actually exercises it. ---

    [Fact]
    public async Task Returning_from_a_stored_chat_to_a_new_one_clears_the_sidebars_highlight()
    {
        // Renders the real chat page rather than calling AppState.SetActiveChat directly: the bug this
        // guards against lived in Chat.razor's OnParametersSetAsync (a guard that skipped SetActiveChat
        // on the first render of "/"), and calling AppState by hand would not exercise that guard at all.
        var chatB = await SeedAsync("already there", DateTime.UtcNow);

        var page = _ctx.RenderComponent<ChatPage>(p => p.Add(c => c.Id, chatB));
        var history = _ctx.RenderComponent<HistorySection>();
        Assert.Contains("active", history.Find(".history-row").ClassName);

        // The same re-parameterization the router performs navigating from a stored chat back to "/".
        await page.InvokeAsync(() => page.SetParametersAndRender(p => p.Add(c => c.Id, (Guid?)null)));

        Assert.DoesNotContain(history.FindAll(".history-row"), r => r.ClassName!.Contains("active"));
    }

    private async Task<Guid> SeedAsync(string title, DateTime lastMessageAtUtc)
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>();
        var chat = new SqlAgent.Storage.Chat
        {
            Id = Guid.NewGuid(),
            Title = title,
            CreatedAt = lastMessageAtUtc,
            UpdatedAt = lastMessageAtUtc,
            LastMessageAt = lastMessageAtUtc,
        };
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
        return chat.Id;
    }

    private async Task<ChatDetail?> LoadAsync(Guid id)
    {
        using var scope = _ctx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ChatService>().GetChatAsync(id);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
