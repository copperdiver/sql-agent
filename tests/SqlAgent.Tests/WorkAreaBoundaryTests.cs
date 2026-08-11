using Bunit;
using Microsoft.AspNetCore.Components;
using SqlAgent.Host.Components.Shared;

namespace SqlAgent.Tests;

/// <summary>
/// An unexpected exception must not take the page down with it, and must not leak its message —
/// exception text can contain a connection string.
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
}

/// <summary>Stands in for a component that fails mid-render with a message that must not escape.</summary>
file sealed class ThrowingChild : ComponentBase
{
    public const string SecretText = "Password=super-secret";

    protected override void OnParametersSet() => throw new InvalidOperationException(SecretText);
}
