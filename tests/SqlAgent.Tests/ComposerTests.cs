using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ComposerTests
{
    private static Bunit.TestContext NewContext()
    {
        var ctx = new Bunit.TestContext();
        ctx.Services.AddScoped<ShortcutService>();
        // Composer binds its Enter handling in JS, the same shape SqlEditor uses for Ctrl+Enter. bUnit's
        // strict JSInterop needs both calls planned; the `_ => true` matcher accepts any arguments
        // because one of them is an ElementReference the test cannot predict.
        ctx.JSInterop.SetupVoid("sqlAgentComposer.bind", _ => true);
        ctx.JSInterop.SetupVoid("sqlAgentComposer.unbind", _ => true);
        // Any test that changes Value without going through a real DOM input event (SetParametersAndRender
        // with a new Value, exactly as a suggestion chip or a post-send clear would) makes Composer call
        // this; strict bUnit JSInterop needs it planned even for tests that don't care about it themselves.
        ctx.JSInterop.SetupVoid("sqlAgentComposer.resize", _ => true);
        return ctx;
    }

    private static IReadOnlyList<DatabaseConnectionInfo> TwoConnections() =>
    [
        new(Guid.NewGuid(), "analytics", DatabaseProviderType.Postgres, true, true, DateTime.UtcNow, DateTime.UtcNow),
        new(Guid.NewGuid(), "billing", DatabaseProviderType.SqlServer, false, true, DateTime.UtcNow, DateTime.UtcNow),
    ];

    [Fact]
    public void The_attachment_menu_lists_saved_connections_by_name()
    {
        // By name, because the name is what identifies a database everywhere else the agent is used —
        // the MCP tools address them the same way. An id in this list would be meaningless to the user.
        using var ctx = NewContext();
        var connections = TwoConnections();

        var menu = ctx.RenderComponent<AttachmentMenu>(p => p.Add(m => m.Connections, connections));
        menu.Find(".menu-trigger").Click();

        Assert.Contains("analytics", menu.Markup);
        Assert.Contains("billing", menu.Markup);
    }

    [Fact]
    public void Choosing_a_database_reports_it_and_an_already_attached_one_cannot_be_chosen_twice()
    {
        // A second row for a database already in the chips would either duplicate the attachment or
        // silently do nothing; both read as a broken menu.
        using var ctx = NewContext();
        var connections = TwoConnections();
        DatabaseConnectionInfo? chosen = null;

        var menu = ctx.RenderComponent<AttachmentMenu>(p => p
            .Add(m => m.Connections, connections)
            .Add(m => m.AttachedIds, new[] { connections[0].Id })
            .Add(m => m.OnAttach, EventCallback.Factory.Create<DatabaseConnectionInfo>(
                new object(), c => chosen = c)));
        menu.Find(".menu-trigger").Click();

        var rows = menu.FindAll(".menu-item-action");
        Assert.Single(rows);
        rows[0].Click();

        Assert.Equal("billing", chosen!.Name);
    }

    [Fact]
    public void With_no_connections_saved_the_menu_offers_the_way_to_make_one()
    {
        // The empty state is the whole content of the menu here. Without it the popover opens onto
        // nothing and reads as a bug rather than as "you have not set up a database yet".
        using var ctx = NewContext();

        var menu = ctx.RenderComponent<AttachmentMenu>(p => p.Add(m => m.Connections, Array.Empty<DatabaseConnectionInfo>()));
        menu.Find(".menu-trigger").Click();

        Assert.Contains("No databases", menu.Markup);
        Assert.Equal("/connections", menu.Find(".empty a").GetAttribute("href"));
    }

    [Fact]
    public void Chips_render_one_per_attached_database_and_removing_one_reports_it()
    {
        using var ctx = NewContext();
        var removed = default(ChatDatabaseRef);
        var attached = new List<ChatDatabaseRef>
        {
            new(Guid.NewGuid(), "analytics"),
            new(Guid.NewGuid(), "billing"),
        };

        var chips = ctx.RenderComponent<AttachmentChips>(p => p
            .Add(c => c.Databases, attached)
            .Add(c => c.OnRemove, EventCallback.Factory.Create<ChatDatabaseRef>(
                new object(), d => removed = d)));

        Assert.Equal(2, chips.FindAll(".chip").Count);
        chips.FindAll(".chip-remove")[1].Click();

        Assert.Equal("billing", removed!.Name);
    }

    [Fact]
    public void A_chip_for_a_deleted_connection_renders_and_removes_like_any_other()
    {
        // ConnectionId is null when the connection behind an attachment has been deleted; the id, never
        // the name, is what proves a connection still exists. Nothing in AttachmentChips reads the id
        // today, but pinning this here means a future change that starts keying chip identity off it
        // fails loudly instead of silently dropping the attachment from a reloaded transcript.
        using var ctx = NewContext();
        var removed = default(ChatDatabaseRef);
        var attached = new List<ChatDatabaseRef> { new(null, "deleted-connection") };

        var chips = ctx.RenderComponent<AttachmentChips>(p => p
            .Add(c => c.Databases, attached)
            .Add(c => c.OnRemove, EventCallback.Factory.Create<ChatDatabaseRef>(
                new object(), d => removed = d)));

        Assert.Single(chips.FindAll(".chip"));
        chips.Find(".chip-remove").Click();

        Assert.Equal("deleted-connection", removed!.Name);
    }

    [Fact]
    public void Chips_on_a_sent_message_carry_no_remove_button()
    {
        // A sent message's attachments are history. Offering an × on them would imply the record can be
        // edited after the fact.
        using var ctx = NewContext();

        var chips = ctx.RenderComponent<AttachmentChips>(p => p
            .Add(c => c.Databases, new List<ChatDatabaseRef> { new(Guid.NewGuid(), "analytics") })
            .Add(c => c.ReadOnly, true));

        Assert.Single(chips.FindAll(".chip"));
        Assert.Empty(chips.FindAll(".chip-remove"));
    }

    [Fact]
    public void Send_is_disabled_for_blank_text_and_reports_the_question_otherwise()
    {
        using var ctx = NewContext();
        var sends = 0;

        var composer = ctx.RenderComponent<Composer>(p => p
            .Add(c => c.Value, "   ")
            .Add(c => c.OnSend, EventCallback.Factory.Create(new object(), () => sends++)));

        Assert.True(composer.Find("[data-testid=send]").HasAttribute("disabled"));

        composer.SetParametersAndRender(p => p.Add(c => c.Value, "how many orders"));
        composer.Find("[data-testid=send]").Click();

        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Enter_without_shift_sends_through_the_same_path_as_the_button()
    {
        // composer.js calls this [JSInvokable] on Enter, exactly as sql-editor.js calls SqlEditor's
        // RunFromEditor on Ctrl+Enter. Invoking it directly drives the identical path a real keypress
        // would, without a JS engine to press the key.
        using var ctx = NewContext();
        var sends = 0;
        var composer = ctx.RenderComponent<Composer>(p => p
            .Add(c => c.Value, "how many orders")
            .Add(c => c.OnSend, EventCallback.Factory.Create(new object(), () => sends++)));

        await composer.InvokeAsync(() => composer.Instance.SendFromEditor());

        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Enter_on_blank_text_or_while_busy_sends_nothing()
    {
        // The button carries a disabled attribute; the key handler bypasses it entirely, so the rule has
        // to live in the component. Same shape as WorkspaceTests' Ctrl+Enter guards on the SQL page.
        using var ctx = NewContext();
        var sends = 0;
        var composer = ctx.RenderComponent<Composer>(p => p
            .Add(c => c.Value, "   ")
            .Add(c => c.OnSend, EventCallback.Factory.Create(new object(), () => sends++)));

        await composer.InvokeAsync(() => composer.Instance.SendFromEditor());
        Assert.Equal(0, sends);

        composer.SetParametersAndRender(p => p
            .Add(c => c.Value, "how many orders")
            .Add(c => c.Busy, true));
        await composer.InvokeAsync(() => composer.Instance.SendFromEditor());

        Assert.Equal(0, sends);
    }

    [Fact]
    public void Value_changed_programmatically_with_no_DOM_input_event_resizes_the_textarea_via_js()
    {
        // A suggestion chip filling the box, or Chat.razor clearing it after a send, sets Value from C#
        // with no corresponding DOM 'input' event — composer.js's own listener, which handles the live
        // typing case, never fires for either. Composer.razor has to notice the mismatch itself and call
        // sqlAgentComposer.resize, or the box keeps whatever height the last keystroke left it at.
        using var ctx = NewContext();

        var composer = ctx.RenderComponent<Composer>(p => p.Add(c => c.Value, "how many orders"));
        composer.SetParametersAndRender(p => p.Add(c => c.Value, "select every order"));

        ctx.JSInterop.VerifyInvoke("sqlAgentComposer.resize");
    }

    [Fact]
    public void Typing_does_not_trigger_a_second_redundant_resize_call()
    {
        // composer.js's own 'input' listener already resizes the box live for a keystroke, before the
        // resulting render even happens. Wiring ValueChanged back into Value here, synchronously, mimics
        // what a real @bind-Value parent does: by the time the render that keystroke causes actually
        // happens, Value already matches what OnInput already recorded as reflected. If Composer.razor
        // could not tell that apart from "Value changed some other way", every keystroke would cost a
        // second, pointless JS round trip on top of the one composer.js already made live.
        using var ctx = NewContext();
        IRenderedComponent<Composer>? composerRef = null;
        var composer = ctx.RenderComponent<Composer>(p => p
            .Add(c => c.Value, "")
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<string>(new object(),
                v => composerRef!.SetParametersAndRender(p2 => p2.Add(c => c.Value, v)))));
        composerRef = composer;

        composer.Find("textarea").Input("how many orders");

        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "sqlAgentComposer.resize");
    }

    [Fact]
    public void While_a_question_is_in_flight_send_becomes_stop()
    {
        // The same cancellation the SQL page has always offered, in the place a chat user looks for it.
        using var ctx = NewContext();
        var stops = 0;

        var composer = ctx.RenderComponent<Composer>(p => p
            .Add(c => c.Value, "how many orders")
            .Add(c => c.Busy, true)
            .Add(c => c.OnStop, EventCallback.Factory.Create(new object(), () => stops++)));

        Assert.Empty(composer.FindAll("[data-testid=send]"));
        composer.Find("[data-testid=stop]").Click();

        Assert.Equal(1, stops);
    }
}
