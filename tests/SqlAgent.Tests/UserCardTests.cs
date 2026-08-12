using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class UserCardTests
{
    private static Bunit.TestContext NewContext()
    {
        var ctx = new Bunit.TestContext();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlAgent:Storage:ConnectionString"] = "Data Source=/tmp/sqlagent-test/sqlagent.db",
                ["SqlAgent:Web:Port"] = "5150",
            })
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(config);
        ctx.Services.AddSingleton<HostInfo>();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
        return ctx;
    }

    [Fact]
    public void The_card_shows_the_os_account_and_machine()
    {
        // There is no user model here: the host is single-user and loopback-only, authenticated by a
        // launch token. Showing the OS account is true without inventing an identity.
        using var ctx = NewContext();

        var card = ctx.RenderComponent<UserCard>();

        Assert.Contains(Environment.UserName, card.Markup);
        Assert.Contains(Environment.MachineName, card.Markup);
    }

    [Fact]
    public void There_is_no_sign_out_action()
    {
        // Nothing to sign out of. An item that appears to end a session but cannot would be a lie about
        // the security model.
        using var ctx = NewContext();
        var card = ctx.RenderComponent<UserCard>();

        card.Find(".user-card-trigger").Click();

        Assert.DoesNotContain("Sign out", card.Markup);
    }

    [Fact]
    public void The_menu_offers_settings_theme_and_about()
    {
        using var ctx = NewContext();
        var card = ctx.RenderComponent<UserCard>();

        card.Find(".user-card-trigger").Click();

        Assert.Contains("Settings", card.Markup);
        Assert.Contains("Theme", card.Markup);
        Assert.Contains("About", card.Markup);
    }

    [Fact]
    public void About_reports_the_port_and_store_location_from_configuration()
    {
        using var ctx = NewContext();
        var card = ctx.RenderComponent<UserCard>();
        card.Find(".user-card-trigger").Click();

        // Not .menu-item (the row's own outer div): MenuItem's onclick lives on the nested
        // .menu-item-action button, and bUnit's click dispatch bubbles UP from the element you click
        // to its ancestors, never down into descendants -- clicking the outer div finds no handler on
        // it or above it (Menu's own elements carry no onclick) and throws MissingEventHandlerException.
        // The actual click target has to be the button that owns the handler, same as a real pointer
        // click landing on it would be.
        card.FindAll(".menu-item-action").Single(i => i.TextContent.Contains("About")).Click();

        Assert.Contains("5150", card.Markup);
        Assert.Contains("sqlagent-test", card.Markup);
    }

    [Fact]
    public void Host_info_derives_initials_from_the_account_name()
    {
        var config = new ConfigurationBuilder().Build();
        var info = new HostInfo(config);

        Assert.False(string.IsNullOrWhiteSpace(info.Initials));
        Assert.True(info.Initials.Length <= 2);
    }

    [Fact]
    public void Host_info_falls_back_to_the_default_port_when_none_is_configured()
    {
        var info = new HostInfo(new ConfigurationBuilder().Build());

        Assert.Equal(LoopbackUrl.DefaultPort, info.Port);
    }
}
