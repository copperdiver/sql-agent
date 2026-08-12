using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Pages;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class SettingsPageTests
{
    /// <summary>A gateway that claims to be configured, standing in for a real provider once one is
    /// wired. Its GenerateSqlAsync is never called by these tests — only IsConfigured is under test.</summary>
    private sealed class ConfiguredGatewayStub : ILlmSqlGateway
    {
        public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException("not exercised by these tests");
    }

    /// <summary>Mirrors the host's real placeholder: no provider is wired, so IsConfigured is false.</summary>
    private sealed class UnconfiguredGatewayStub : ILlmSqlGateway
    {
        public bool IsConfigured => false;

        public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("No LLM provider is configured on this server.");
    }

    private static Bunit.TestContext NewContext(ILlmSqlGateway gateway)
    {
        var ctx = new Bunit.TestContext();
        ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SqlAgent:Web:Port"] = "5150" })
            .Build());
        ctx.Services.AddSingleton<HostInfo>();
        ctx.Services.AddSingleton(gateway);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
        return ctx;
    }

    [Fact]
    public void An_unconfigured_provider_is_reported_plainly_with_a_pointer_to_the_runbook()
    {
        // The composer's model selector links here. Saying "not configured" and naming the runbook is
        // the honest version; a page that looked configurable would send the user hunting for a form
        // that does not exist.
        using var ctx = NewContext(new UnconfiguredGatewayStub());

        var page = ctx.RenderComponent<Settings>();

        Assert.Contains("No model configured", page.Markup);
        Assert.Contains("runbook", page.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_configured_provider_is_reported_as_configured()
    {
        // Asserting only the absence of "No model configured" was not enough to cover anything: deleting
        // the whole `@if (Llm.IsConfigured) { <Badge>Configured</Badge> }` branch from Settings.razor
        // left this test green, even though that branch is the only rendered evidence of the configured
        // path — the seam a later phase's model selector reads. So assert what is present, not just what
        // is absent, and pin the unconfigured page's runbook pointer as absent too: a page that both
        // says "Configured" and sends the user to the runbook is the failure this pair rules out.
        using var ctx = NewContext(new ConfiguredGatewayStub());

        var page = ctx.RenderComponent<Settings>();

        Assert.Contains("Configured", page.Markup);
        Assert.DoesNotContain("No model configured", page.Markup);
        Assert.DoesNotContain("runbook", page.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_gateway_that_says_nothing_is_treated_as_configured()
    {
        // IsConfigured is a default interface member returning true, so a future real provider does not
        // have to remember to implement it to be usable. Cast to the interface: a DIM is reachable only
        // through the interface-typed reference, not through the implementing class's own type -- calling
        // it on the concrete ConfiguredGatewayStub type directly is a compile error (CS1061).
        ILlmSqlGateway gateway = new ConfiguredGatewayStub();
        Assert.True(gateway.IsConfigured);
    }

    [Fact]
    public void The_environment_panel_reports_version_port_and_store_location()
    {
        using var ctx = NewContext(new UnconfiguredGatewayStub());

        var page = ctx.RenderComponent<Settings>();

        Assert.Contains("5150", page.Markup);
        Assert.Contains("Store", page.Markup);
        Assert.Contains("Version", page.Markup);
    }

    [Fact]
    public void The_theme_control_is_available_on_the_page_as_well_as_in_the_menu()
    {
        using var ctx = NewContext(new UnconfiguredGatewayStub());

        var page = ctx.RenderComponent<Settings>();

        Assert.Contains("System", page.Markup);
        Assert.Contains("Light", page.Markup);
        Assert.Contains("Dark", page.Markup);
    }
}
