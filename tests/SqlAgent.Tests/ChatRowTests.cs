using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// The row owns a chat's actions rather than reporting them upward, because two sections render it and
/// the alternative is the same thirty lines of dialog-and-write logic in both. These tests therefore
/// assert against the store, not against callbacks.
/// </summary>
public class ChatRowTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public ChatRowTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<ChatService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<DialogService>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task The_row_shows_the_title_and_opens_the_chat()
    {
        var chat = await SeedAsync("quarterly revenue");
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));

        row.Find(".chat-row-open").Click();

        var nav = _ctx.Services.GetRequiredService<FakeNavigationManager>();
        Assert.Contains("quarterly revenue", row.Markup);
        Assert.EndsWith($"/chat/{chat.Id}", nav.Uri);
    }

    [Fact]
    public async Task The_active_row_says_so_in_its_class()
    {
        // The sidebar is the only thing telling the user which conversation they are in once the
        // transcript scrolls past the first message.
        var chat = await SeedAsync("quarterly revenue");

        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat).Add(r => r.Active, true));

        Assert.Contains("active", row.Find(".chat-row").ClassName);
    }

    [Fact]
    public async Task Re_rendering_with_a_new_active_flag_moves_the_highlight()
    {
        // Active is a parameter rather than a read of AppState inside the row precisely so this works:
        // a child that reads circuit state directly can be skipped by the diff when its own parameters
        // have not changed, stranding the highlight on the chat the user just left.
        var chat = await SeedAsync("quarterly revenue");
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat).Add(r => r.Active, true));

        row.SetParametersAndRender(p => p.Add(r => r.Active, false));

        Assert.DoesNotContain("active", row.Find(".chat-row").ClassName);
    }

    [Fact]
    public async Task Renaming_goes_through_a_dialog_and_updates_the_store()
    {
        var chat = await SeedAsync("first question, truncated");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));

        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Rename")).Click();

        // Handed to DialogService rather than rendered here: inside the drawer a Modal would resolve its
        // position against the sidebar's transform and ride off-screen with it.
        Assert.NotNull(dialogs.Current);
        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("quarterly revenue");
        await dialog.Find("[data-testid=name-save]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("quarterly revenue", (await LoadAsync(chat.Id))!.Title);
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task Deleting_asks_first_naming_the_chat_and_then_removes_it()
    {
        var chat = await SeedAsync("throwaway");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));

        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        // "Are you sure?" with no subject is how the wrong one goes.
        Assert.Contains("throwaway", dialog.Markup);
        await dialog.Find("[data-testid=delete-confirm]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Null(await LoadAsync(chat.Id));
    }

    [Fact]
    public async Task Cancelling_the_delete_dialog_keeps_the_chat()
    {
        var chat = await SeedAsync("keep me");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));
        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=delete-cancel]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(await LoadAsync(chat.Id));
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task Deleting_the_open_chat_clears_the_selection_and_leaves_its_route()
    {
        // Otherwise the sidebar keeps highlighting a row that no longer exists and the page keeps showing
        // a conversation deleted out from under it.
        var chat = await SeedAsync("open one");
        var state = _ctx.Services.GetRequiredService<AppState>();
        state.SetActiveChat(chat.Id);
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat).Add(r => r.Active, true));
        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=delete-confirm]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Null(state.ActiveChatId);
        Assert.EndsWith("/", _ctx.Services.GetRequiredService<FakeNavigationManager>().Uri);
    }

    [Fact]
    public async Task A_write_tells_the_sidebar_the_list_changed()
    {
        // The row does not know which section is rendering it. Announcing through AppState is what makes
        // both the history list and a project reload themselves after a rename.
        var chat = await SeedAsync("first");
        var notified = 0;
        _ctx.Services.GetRequiredService<AppState>().ChatsChanged += () => notified++;
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));
        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Rename")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("second");
        await dialog.Find("[data-testid=name-save]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(notified > 0);
    }

    [Fact]
    public async Task The_menu_trigger_names_the_chat_for_a_screen_reader()
    {
        // A sidebar of twenty identical "more actions" buttons is unusable without it.
        var chat = await SeedAsync("quarterly revenue");

        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));

        Assert.Contains("quarterly revenue", row.Find(".menu-trigger .sr-only").TextContent);
    }

    private async Task<ChatSummary> SeedAsync(string title)
    {
        using var scope = _ctx.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<ChatService>();
        var id = await chats.CreateChatAsync(title);
        return new ChatSummary(id, title, DateTime.UtcNow);
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
