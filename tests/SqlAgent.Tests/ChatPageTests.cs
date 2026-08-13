using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Host.Web;
using SqlAgent.Storage;
using static SqlAgent.Tests.AsyncTestHelpers;
// `SqlAgent.Storage.Chat` (the entity, from `using SqlAgent.Storage`) and
// `SqlAgent.Host.Components.Pages.Chat` (the page under test) share the bare name "Chat". A plain
// `using SqlAgent.Host.Components.Pages;` makes every ordinary reference to the page ambiguous — this
// alias is what the rest of the file names it by instead.
using ChatPage = SqlAgent.Host.Components.Pages.Chat;

namespace SqlAgent.Tests;

/// <summary>
/// The chat page wired to the real ChatTurnService and NlQueryService over an in-memory store, the same
/// shape WorkspaceTests uses for the SQL page. These re-establish the behaviours the deleted
/// WorkspaceChatTests covered for the old chat tab — a blank question, a second send while one is in
/// flight, open-in-editor — and add the ones only persistence makes possible.
/// </summary>
public class ChatPageTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();
    private readonly ChatGatewayStub _gateway = new();
    private readonly TurnProviderStub _provider = new();

    public ChatPageTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProvider>(_provider);
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddSingleton<ILlmSqlGateway>(_gateway);
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<QueryExecutionService>();
        _ctx.Services.AddScoped<SchemaService>();
        _ctx.Services.AddScoped<NlQueryService>();
        _ctx.Services.AddScoped<ChatService>();
        _ctx.Services.AddScoped<ChatTurnService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<DialogService>();

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();

        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void A_new_chat_offers_the_composer_and_suggestions_rather_than_an_empty_transcript()
    {
        var page = _ctx.RenderComponent<ChatPage>();

        Assert.Contains("How can I help with your data?", page.Markup);
        Assert.NotEmpty(page.FindAll("[data-testid=suggestion]"));
        Assert.Empty(page.FindAll(".message"));
    }

    [Fact]
    public void A_suggestion_fills_the_composer_instead_of_sending()
    {
        // With no model configured, a chip that sent immediately would answer every click with an error
        // panel. Filling the box lets the question be edited first, which is the point of a suggestion.
        var page = _ctx.RenderComponent<ChatPage>();

        page.FindAll("[data-testid=suggestion]")[0].Click();

        Assert.Equal(0, _gateway.CallCount);
        Assert.NotEmpty(page.Find("textarea").GetAttribute("value")!);
    }

    [Fact]
    public async Task Sending_the_first_message_creates_the_chat_and_moves_to_its_route()
    {
        var connection = await AddConnectionAsync("prod");
        var page = _ctx.RenderComponent<ChatPage>();
        await AttachAsync(page, "prod");
        Type(page, "how many orders");

        await ClickAsync(page.Find("[data-testid=send]"));

        var chat = Assert.Single(await ListChatsAsync());
        Assert.Equal("how many orders", chat.Title);
        var nav = _ctx.Services.GetRequiredService<FakeNavigationManager>();
        Assert.EndsWith($"/chat/{chat.Id}", nav.Uri);

        // The attachment reached the store as a snapshot: the live id, and the name it had at send time.
        var question = (await LoadAsync(chat.Id))!.Messages.First();
        var attached = Assert.Single(question.Databases);
        Assert.Equal(connection, attached.ConnectionId);
        Assert.Equal("prod", attached.Name);
    }

    [Fact]
    public void Opening_a_new_chat_and_leaving_without_sending_creates_no_row()
    {
        // The single most visible way a chat app accumulates junk. The row is written on first send, not
        // on first render.
        _ctx.RenderComponent<ChatPage>();
        _ctx.DisposeComponents();

        Assert.Empty(ListChatsAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public async Task A_reloaded_chat_shows_its_messages_its_snapshot_and_no_grid()
    {
        // The whole phase in one test: what a user sees after pressing F5. Rows are gone by design, so
        // the answer must show its metadata rather than an empty table pretending the query returned
        // nothing.
        await AddConnectionAsync("prod");
        _gateway.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _provider.NextResult = new QueryResultSet(["id"], [new object?[] { 1 }], Truncated: false);

        var page = _ctx.RenderComponent<ChatPage>();
        await AttachAsync(page, "prod");
        Type(page, "how many orders");
        await ClickAsync(page.Find("[data-testid=send]"));
        var chatId = (await ListChatsAsync()).Single().Id;

        // A second component instance for the same route is what a reload actually is.
        var reloaded = _ctx.RenderComponent<ChatPage>(p => p.Add(c => c.Id, chatId));

        Assert.Contains("how many orders", reloaded.Markup);
        Assert.Contains("SELECT id FROM orders", reloaded.Markup);
        Assert.Contains("prod", reloaded.Markup);
        Assert.Contains("Rows are not stored", reloaded.Markup);
        // ChatOutcome's Restored branch never emits .grid-scroll at all — asserting on the absence of a
        // table the way ChatOutcomeTests does would still fail against a rendering that emitted the wrong
        // wrapper markup with a table inside it, where the narrower selector above would pass.
        Assert.Empty(reloaded.FindAll("table"));
    }

    [Fact]
    public async Task A_blank_question_sends_nothing()
    {
        // The guard lives in two places: Composer disables the send button, and SendAsync re-checks
        // string.IsNullOrWhiteSpace for the Enter-key path, which reaches SendFromEditor directly and
        // so never sees the button's disabled attribute at all. Both must hold for whitespace-only text.
        var page = _ctx.RenderComponent<ChatPage>();
        Type(page, "   ");

        Assert.True(page.Find("[data-testid=send]").HasAttribute("disabled"));

        await page.InvokeAsync(() => page.FindComponent<Composer>().Instance.SendFromEditor());

        Assert.Equal(0, _gateway.CallCount);
        Assert.Empty(page.FindAll(".message"));
    }

    [Fact]
    public async Task Sending_with_nothing_attached_answers_with_an_explanation_and_still_records_the_question()
    {
        // The question is cleared from the composer and persisted unconditionally — it is NOT kept in the
        // box for editing. It shows up here because it was sent and now sits in the transcript as its own
        // bubble, same as any other question; only the answer differs by explaining there was nothing to
        // ask.
        var page = _ctx.RenderComponent<ChatPage>();
        Type(page, "how many orders");

        await ClickAsync(page.Find("[data-testid=send]"));

        Assert.Equal(0, _gateway.CallCount);
        Assert.Contains("attachment menu", page.Markup);
        Assert.Contains("how many orders", page.Markup);
    }

    [Fact]
    public async Task Chips_stay_attached_for_the_next_question()
    {
        // Attachments are per message, but re-attaching on every turn would make a ten-question
        // conversation ten attachments. The chips carry over until they are removed.
        await AddConnectionAsync("prod");
        var page = _ctx.RenderComponent<ChatPage>();
        await AttachAsync(page, "prod");
        Type(page, "first");
        await ClickAsync(page.Find("[data-testid=send]"));

        Assert.Contains(page.FindAll(".composer .chip"), c => c.TextContent.Contains("prod"));
    }

    [Fact]
    public async Task A_second_send_while_one_is_in_flight_is_ignored()
    {
        await AddConnectionAsync("prod");
        _gateway.Hold();
        var page = _ctx.RenderComponent<ChatPage>();
        await AttachAsync(page, "prod");
        Type(page, "first question");

        var first = ClickAsync(page.Find("[data-testid=send]"));
        await WaitForConditionAsync(() => _gateway.CallCount == 1);

        // The send button is a stop button now, so there is nothing to click twice — which is the
        // guard. Driving the key path instead proves it holds there too.
        Assert.Empty(page.FindAll("[data-testid=send]"));
        await page.InvokeAsync(() => page.FindComponent<Composer>().Instance.SendFromEditor());

        Assert.Equal(1, _gateway.CallCount);

        _gateway.Release(LlmSqlResponse.Generated("SELECT 1"));
        await first;
    }

    [Fact]
    public async Task Open_in_editor_hands_the_sql_to_the_sql_page()
    {
        // The two are separate routes now, so the handoff goes through AppState rather than a tab flip.
        await AddConnectionAsync("prod");
        _gateway.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _provider.NextResult = new QueryResultSet(["id"], [new object?[] { 1 }], false);

        var page = _ctx.RenderComponent<ChatPage>();
        await AttachAsync(page, "prod");
        Type(page, "orders");
        await ClickAsync(page.Find("[data-testid=send]"));

        await ClickAsync(page.FindAll("button").First(b => b.TextContent.Trim() == "Open in editor"));

        var nav = _ctx.Services.GetRequiredService<FakeNavigationManager>();
        Assert.EndsWith("/sql", nav.Uri);
        Assert.Equal("SELECT id FROM orders", _ctx.Services.GetRequiredService<AppState>().TakePendingSql());
    }

    [Fact]
    public async Task The_page_tells_the_sidebar_that_history_changed()
    {
        // The history section is a sibling under MainLayout, so nothing else would ever tell it a chat
        // was created.
        await AddConnectionAsync("prod");
        var notified = 0;
        _ctx.Services.GetRequiredService<AppState>().ChatsChanged += () => notified++;

        var page = _ctx.RenderComponent<ChatPage>();
        await AttachAsync(page, "prod");
        Type(page, "orders");
        await ClickAsync(page.Find("[data-testid=send]"));

        Assert.True(notified > 0);
    }

    [Fact]
    public async Task Two_questions_in_one_chat_each_keep_their_own_grid()
    {
        // _live is keyed by the assistant message's id (see MessageList). A mis-keyed result would hang
        // one question's grid under another's answer instead of its own — invisible in a test that only
        // sends once, which is why the earlier reload test alone did not cover this.
        await AddConnectionAsync("prod");
        var page = _ctx.RenderComponent<ChatPage>();
        await AttachAsync(page, "prod");

        _gateway.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _provider.NextResult = new QueryResultSet(["id"], [new object?[] { 1 }], Truncated: false);
        Type(page, "first question");
        await ClickAsync(page.Find("[data-testid=send]"));

        _gateway.NextResponse = LlmSqlResponse.Generated("SELECT id FROM customers");
        _provider.NextResult = new QueryResultSet(["id"], [new object?[] { 2 }, new object?[] { 3 }], Truncated: false);
        Type(page, "second question");
        await ClickAsync(page.Find("[data-testid=send]"));

        var tables = page.FindAll(".grid-scroll table");
        Assert.Equal(2, tables.Count);
        Assert.Contains("1", tables[0].TextContent);
        Assert.DoesNotContain("2", tables[0].TextContent);
        Assert.DoesNotContain("3", tables[0].TextContent);
        Assert.Contains("2", tables[1].TextContent);
        Assert.Contains("3", tables[1].TextContent);
        Assert.DoesNotContain("1", tables[1].TextContent);
    }

    [Fact]
    public async Task Navigating_to_another_chat_mid_send_drops_the_stale_turn_and_does_not_hijack_navigation()
    {
        // Regression test for the corruption the review flagged: Chat.razor is the same component instance
        // across "/" and "/chat/{Id}" (that reuse is why OnParametersSetAsync's guard exists at all), so
        // nothing used to stop an in-flight turn from landing after the user had already opened a different
        // chat. Before the fix this appended chat A's Q&A into B's transcript, then navigated the user OUT
        // of B and back into A the moment the answer arrived.
        await AddConnectionAsync("prod");
        var chatB = await CreateStoredChatAsync("already there");
        _gateway.Hold();

        var page = _ctx.RenderComponent<ChatPage>();
        await AttachAsync(page, "prod");
        Type(page, "new chat question");
        var send = ClickAsync(page.Find("[data-testid=send]"));
        await WaitForConditionAsync(() => _gateway.CallCount == 1);

        // The same instance being handed a different Id is exactly what the router does navigating from
        // "/" to a stored chat — bUnit's SetParametersAndRender is that re-parameterization. Routed through
        // InvokeAsync so OnParametersSetAsync's own await (loading chat B) completes before we look at the
        // markup, the same way ClickAsync dispatches an event through the renderer's sync context.
        await page.InvokeAsync(() => page.SetParametersAndRender(p => p.Add(c => c.Id, chatB)));
        Assert.Contains("already there", page.Markup);
        var nav = _ctx.Services.GetRequiredService<FakeNavigationManager>();
        var uriBeforeRelease = nav.Uri;

        _gateway.Release(LlmSqlResponse.Generated("SELECT 1"));
        await send;

        // Chat B's transcript is exactly what it was — the stale turn was not appended to it...
        Assert.Contains("already there", page.Markup);
        Assert.DoesNotContain("new chat question", page.Markup);
        // ...and the stale send did not navigate the user back into the chat it started in.
        Assert.Equal(uriBeforeRelease, nav.Uri);
        // The turn still landed on disk under its own chat, though — nothing about the fix should lose it.
        var chats = await ListChatsAsync();
        Assert.Contains(chats, c => c.Title == "new chat question");
    }

    private async Task<Guid> CreateStoredChatAsync(string question)
    {
        using var scope = _ctx.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<ChatService>();
        var id = await chats.CreateChatAsync(question);
        await chats.AppendMessageAsync(new ChatMessageInput(id, ChatRole.User, question, []));
        return id;
    }

    private async Task<Guid> AddConnectionAsync(string name)
    {
        using var scope = _ctx.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput(name, DatabaseProviderType.Postgres, IsReadOnly: true), "cs");
        return created.Id;
    }

    private async Task<IReadOnlyList<ChatSummary>> ListChatsAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ChatService>().ListHistoryAsync();
    }

    private async Task<ChatDetail?> LoadAsync(Guid id)
    {
        using var scope = _ctx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ChatService>().GetChatAsync(id);
    }

    /// <summary>Opens the attachment menu and picks a database by name, the way a user does.</summary>
    private static async Task AttachAsync(IRenderedComponent<ChatPage> page, string name)
    {
        await ClickAsync(page.Find(".composer .menu-trigger"));
        await ClickAsync(page.FindAll(".composer .menu-item-action")
            .First(r => r.TextContent.Contains(name)));
    }

    private static void Type(IRenderedComponent<ChatPage> page, string text) =>
        page.Find("textarea").Input(text);

    private static Task ClickAsync(AngleSharp.Dom.IElement element) =>
        element.ClickAsync(new MouseEventArgs());

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
