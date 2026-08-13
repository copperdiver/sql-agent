using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

/// <summary>
/// Dialogs are rendered from MainLayout rather than from wherever they are asked for, because below
/// 1024px the sidebar carries a CSS transform and a position:fixed descendant resolves against the
/// transformed element instead of the viewport (Phase A carry-forward item 1). A confirmation opened
/// from the history menu inside the drawer would centre on the drawer and ride off-screen with it.
/// </summary>
public class DialogHostTests
{
    [Fact]
    public void Nothing_renders_until_a_dialog_is_shown()
    {
        using var ctx = new Bunit.TestContext();
        var dialogs = new DialogService();
        ctx.Services.AddSingleton(dialogs);

        var host = ctx.RenderComponent<DialogHost>();

        Assert.Empty(host.Markup.Trim());
    }

    [Fact]
    public void A_shown_dialog_renders_and_a_closed_one_disappears()
    {
        using var ctx = new Bunit.TestContext();
        var dialogs = new DialogService();
        ctx.Services.AddSingleton(dialogs);
        var host = ctx.RenderComponent<DialogHost>();

        // Show is called from another component's event handler in real use, so the host is not the
        // component handling the event — it re-renders only because it subscribed. That is exactly the
        // failure mode this asserts against.
        host.InvokeAsync(() => dialogs.Show(b => b.AddMarkupContent(0, "<p id=\"d\">confirm?</p>")));
        Assert.Single(host.FindAll("#d"));

        host.InvokeAsync(dialogs.Close);
        Assert.Empty(host.FindAll("#d"));
    }

    [Fact]
    public void Showing_a_second_dialog_replaces_the_first()
    {
        // There is one host, so two dialogs would otherwise stack invisibly and the scrim of the second
        // would sit over the first with no way to reach it.
        using var ctx = new Bunit.TestContext();
        var dialogs = new DialogService();
        ctx.Services.AddSingleton(dialogs);
        var host = ctx.RenderComponent<DialogHost>();

        host.InvokeAsync(() => dialogs.Show(b => b.AddMarkupContent(0, "<p id=\"first\">a</p>")));
        host.InvokeAsync(() => dialogs.Show(b => b.AddMarkupContent(0, "<p id=\"second\">b</p>")));

        Assert.Empty(host.FindAll("#first"));
        Assert.Single(host.FindAll("#second"));
    }

    [Fact]
    public void Disposing_the_host_unsubscribes()
    {
        // The host lives in MainLayout for the whole circuit, but bUnit tears components down between
        // tests and a leaked handler would keep a disposed renderer alive — the same leak ShellTests
        // pins for Sidebar's LocationChanged subscription.
        using var ctx = new Bunit.TestContext();
        var dialogs = new DialogService();
        ctx.Services.AddSingleton(dialogs);
        ctx.RenderComponent<DialogHost>();

        ctx.DisposeComponents();

        // Show must not throw into a disposed renderer.
        dialogs.Show(b => b.AddMarkupContent(0, "<p>x</p>"));
    }
}
