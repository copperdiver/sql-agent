using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Shared;

namespace SqlAgent.Tests;

/// <summary>
/// An unexpected exception must not take the page down with it, and must not leak its message —
/// exception text can contain a connection string. Once tripped, the boundary must also be
/// recoverable: an explicit retry, and navigating away from the page that failed.
/// </summary>
public class WorkAreaBoundaryTests
{
    [Fact]
    public void An_exception_inside_the_work_area_renders_a_retry_prompt()
    {
        using var ctx = new Bunit.TestContext();

        var area = ctx.RenderComponent<WorkArea>(p => p.AddChildContent<ThrowingChild>());

        Assert.Contains("Something went wrong", area.Markup);
    }

    [Fact]
    public void The_exception_message_is_never_rendered()
    {
        using var ctx = new Bunit.TestContext();

        var area = ctx.RenderComponent<WorkArea>(p => p.AddChildContent<ThrowingChild>());

        Assert.DoesNotContain(ThrowingChild.SecretText, area.Markup);
    }

    [Fact]
    public void Clicking_retry_after_a_trip_renders_the_child_content_again()
    {
        using var ctx = new Bunit.TestContext();
        var condition = new FlakyCondition();

        var area = ctx.RenderComponent<WorkArea>(
            p => p.AddChildContent<FlakyChild>(c => c.Add(x => x.Condition, condition)));
        Assert.Contains("Something went wrong", area.Markup);

        // The underlying condition has cleared; Retry calls ErrorBoundary.Recover(), which is the
        // only thing that makes Blazor try ChildContent again — new ChildContent alone does not.
        condition.ShouldThrow = false;
        area.Find("button").Click();

        Assert.DoesNotContain("Something went wrong", area.Markup);
        Assert.Contains(FlakyChild.RecoveredMarker, area.Markup);
    }

    [Fact]
    public void A_location_change_clears_a_tripped_boundary()
    {
        using var ctx = new Bunit.TestContext();
        var condition = new FlakyCondition();

        var area = ctx.RenderComponent<WorkArea>(
            p => p.AddChildContent<FlakyChild>(c => c.Add(x => x.Condition, condition)));
        Assert.Contains("Something went wrong", area.Markup);

        // MainLayout, which hosts WorkArea, is not recreated across route navigation. The only thing
        // that keeps a page failure from taking down every page reached afterward via the header nav
        // is the boundary recovering on its own when the location changes, not only on an explicit Retry.
        condition.ShouldThrow = false;
        var nav = ctx.Services.GetRequiredService<FakeNavigationManager>();
        nav.NavigateTo("connections");

        Assert.DoesNotContain("Something went wrong", area.Markup);
        Assert.Contains(FlakyChild.RecoveredMarker, area.Markup);
    }

    [Fact]
    public void Disposing_the_work_area_unsubscribes_from_navigation_changes()
    {
        using var ctx = new Bunit.TestContext();
        var area = ctx.RenderComponent<WorkArea>(p => p.AddChildContent<FlakyChild>());
        var nav = ctx.Services.GetRequiredService<FakeNavigationManager>();

        Assert.Equal(1, LocationChangedSubscriberCount(nav));

        // bUnit disposes the whole rendered component tree here, the same as the real Blazor renderer
        // does when a component leaves the render tree (e.g. the circuit's page navigates elsewhere).
        ctx.DisposeComponents();

        // If WorkArea.Dispose() did not unsubscribe from NavigationManager.LocationChanged, this would
        // still be 1 and the component would keep itself alive for the rest of the circuit — the same
        // class of leak the SchemaRail/AppState.Changed test pins, applied to NavigationManager here.
        Assert.Equal(0, LocationChangedSubscriberCount(nav));
    }

    private static int LocationChangedSubscriberCount(NavigationManager nav)
    {
        var field = typeof(NavigationManager).GetField("_locationChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var handler = (Delegate?)field!.GetValue(nav);
        return handler?.GetInvocationList().Length ?? 0;
    }
}

/// <summary>Stands in for a component that fails mid-render with a message that must not escape.</summary>
file sealed class ThrowingChild : ComponentBase
{
    public const string SecretText = "Password=super-secret";

    protected override void OnParametersSet() => throw new InvalidOperationException(SecretText);
}

/// <summary>
/// Per-test failure switch for <see cref="FlakyChild"/>. This used to be a <c>static bool</c> on the
/// component itself, which no fixture reset — so whether a test saw a throwing or a healthy child
/// depended on which test ran before it, and the last test in the file only passed because the two
/// recovery tests happened to leave it true. Each test now owns its own instance.
/// </summary>
file sealed class FlakyCondition
{
    public bool ShouldThrow { get; set; } = true;
}

/// <summary>
/// Stands in for a component whose failure condition can clear between renders, so recovery (retry or
/// navigation) can be observed actually re-running ChildContent, not just hiding stale markup.
/// </summary>
file sealed class FlakyChild : ComponentBase
{
    public const string RecoveredMarker = "flaky-child-recovered";

    /// <summary>Defaults to a throwing condition, so a test that does not care can omit it.</summary>
    [Parameter] public FlakyCondition Condition { get; set; } = new();

    protected override void OnParametersSet()
    {
        if (Condition.ShouldThrow) throw new InvalidOperationException("transient failure");
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, RecoveredMarker);
}
