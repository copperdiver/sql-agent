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
        // A fixed -2h offset here is flaky near local midnight: whenever the host's local time is within
        // two hours after midnight, "2 hours ago" in UTC lands on the *previous* local calendar day, and
        // ChatHistoryGrouping (which buckets by local calendar date, correctly per its own doc comment)
        // puts this chat under Yesterday instead of Today, failing the assertion below for a reason that
        // has nothing to do with the grouping logic. No offset at all still exercises the same thing this
        // test checks — a chat active right now sorts under Today, ahead of one 20 days old — without a
        // day-boundary edge case that only reproduces at a specific time of night.
        await SeedAsync("this morning", DateTime.UtcNow);
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

        Assert.Contains("active", section.Find(".chat-row").ClassName);
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
    public async Task Cancelling_the_rename_dialog_keeps_the_title()
    {
        var id = await SeedAsync("keep me", DateTime.UtcNow);
        var section = _ctx.RenderComponent<HistorySection>();
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();

        section.Find(".chat-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Rename")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("something else entirely");
        await dialog.Find("[data-testid=name-cancel]").ClickAsync(new MouseEventArgs());

        var reloaded = await LoadAsync(id);
        Assert.NotNull(reloaded);
        Assert.Equal("keep me", reloaded.Title);
        Assert.Null(dialogs.Current);
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
        Assert.Contains("active", history.Find(".chat-row").ClassName);

        // The same re-parameterization the router performs navigating from a stored chat back to "/".
        await page.InvokeAsync(() => page.SetParametersAndRender(p => p.Add(c => c.Id, (Guid?)null)));

        Assert.DoesNotContain(history.FindAll(".chat-row"), r => r.ClassName!.Contains("active"));
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
