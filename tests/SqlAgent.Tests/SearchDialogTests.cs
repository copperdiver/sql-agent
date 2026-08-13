using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class SearchDialogTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public SearchDialogTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<ChatService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<SearchService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<ShortcutService>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void An_empty_box_shows_a_hint_rather_than_an_empty_list()
    {
        // A blank result area reads as "nothing found" when nothing has been asked yet.
        var dialog = _ctx.RenderComponent<SearchDialog>();

        Assert.Empty(dialog.FindAll("[data-testid=search-hit]"));
        Assert.Contains("Search", dialog.Markup);
    }

    [Fact]
    public async Task Typing_finds_a_chat_by_title_and_opening_it_navigates()
    {
        await SeedChatAsync("quarterly revenue", "body");
        var dialog = _ctx.RenderComponent<SearchDialog>();

        dialog.Find("input").Input("quarterly");
        await WaitForHitsAsync(dialog);

        var hit = dialog.FindAll("[data-testid=search-hit]").First();
        Assert.Contains("quarterly revenue", hit.TextContent);
        await hit.ClickAsync(new MouseEventArgs());

        Assert.Contains("/chat/", _ctx.Services.GetRequiredService<FakeNavigationManager>().Uri);
    }

    [Fact]
    public async Task A_message_match_shows_the_snippet_that_explains_it()
    {
        // The title is the first sixty characters of the first question, so a body match with no snippet
        // gives the user no idea why the chat is in the list.
        await SeedChatAsync("untitled", "the quick brown fox jumps over the lazy dog");
        var dialog = _ctx.RenderComponent<SearchDialog>();

        dialog.Find("input").Input("lazy");
        await WaitForHitsAsync(dialog);

        Assert.Contains("lazy", dialog.Markup);
    }

    [Fact]
    public async Task Nothing_found_says_so()
    {
        await SeedChatAsync("quarterly revenue", "body");
        var dialog = _ctx.RenderComponent<SearchDialog>();

        dialog.Find("input").Input("zzzz");
        await WaitForConditionAsync(() => dialog.Markup.Contains("No matches", StringComparison.Ordinal));

        Assert.Empty(dialog.FindAll("[data-testid=search-hit]"));
    }

    [Fact]
    public async Task The_arrow_keys_move_the_highlight_and_Enter_opens_it()
    {
        // The point of a command palette is that the hands never leave the keyboard.
        await SeedChatAsync("lazy one", "x");
        await SeedChatAsync("lazy two", "x");
        var dialog = _ctx.RenderComponent<SearchDialog>();
        dialog.Find("input").Input("lazy");
        await WaitForHitsAsync(dialog);

        Assert.Contains("highlighted", dialog.FindAll("[data-testid=search-hit]")[0].ClassName);

        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Contains("highlighted", dialog.FindAll("[data-testid=search-hit]")[1].ClassName);

        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        Assert.Contains("highlighted", dialog.FindAll("[data-testid=search-hit]")[0].ClassName);

        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        Assert.Contains("/chat/", _ctx.Services.GetRequiredService<FakeNavigationManager>().Uri);
    }

    [Fact]
    public async Task The_highlight_does_not_run_off_either_end()
    {
        await SeedChatAsync("lazy one", "x");
        var dialog = _ctx.RenderComponent<SearchDialog>();
        dialog.Find("input").Input("lazy");
        await WaitForHitsAsync(dialog);

        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Contains("highlighted", dialog.FindAll("[data-testid=search-hit]")[0].ClassName);
    }

    [Fact]
    public async Task Opening_a_project_hit_asks_the_sidebar_to_expand_it()
    {
        // There is no project route to navigate to, and a hit that does nothing when clicked is worse
        // than no hit at all.
        Guid projectId;
        using (var scope = _ctx.Services.CreateScope())
            projectId = (await scope.ServiceProvider.GetRequiredService<ProjectService>()
                .CreateProjectAsync("quarterly")).Id!.Value;
        var dialog = _ctx.RenderComponent<SearchDialog>();
        dialog.Find("input").Input("quarterly");
        await WaitForHitsAsync(dialog);

        await dialog.FindAll("[data-testid=search-hit]").First().ClickAsync(new MouseEventArgs());

        Assert.Equal(projectId, _ctx.Services.GetRequiredService<AppState>().ProjectToExpand);
    }

    private async Task SeedChatAsync(string title, string body)
    {
        using var scope = _ctx.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<ChatService>();
        var id = await chats.CreateChatAsync(title);
        await chats.AppendMessageAsync(new ChatMessageInput(id, ChatRole.User, body, []));
    }

    private static Task WaitForHitsAsync(IRenderedComponent<SearchDialog> dialog) =>
        WaitForConditionAsync(() => dialog.FindAll("[data-testid=search-hit]").Count > 0);

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "The dialog never reached the expected state.");
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
