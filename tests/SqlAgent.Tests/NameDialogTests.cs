using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class NameDialogTests
{
    [Fact]
    public void It_opens_with_the_current_name_already_in_the_box()
    {
        // Renaming starts from what the thing is called, so the common edit — fixing one word — does not
        // begin by retyping the whole name.
        using var ctx = new Bunit.TestContext();
        ctx.Services.AddScoped<ShortcutService>();
        // NameDialog now asks Modal to focus its input on open (a real JS interop call, replacing the
        // autofocus attribute Modal used to render) rather than something bUnit's default strict interop
        // mode allows through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "Rename chat")
            .Add(d => d.Label, "Title")
            .Add(d => d.InitialValue, "quarterly revenue"));

        Assert.Equal("quarterly revenue", dialog.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void Saving_reports_the_edited_name()
    {
        using var ctx = new Bunit.TestContext();
        ctx.Services.AddScoped<ShortcutService>();
        // NameDialog now asks Modal to focus its input on open (a real JS interop call, replacing the
        // autofocus attribute Modal used to render) rather than something bUnit's default strict interop
        // mode allows through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var saved = "";

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "Rename chat")
            .Add(d => d.InitialValue, "old")
            .Add(d => d.OnSave, EventCallback.Factory.Create<string>(new object(), v => saved = v)));
        dialog.Find("input").Change("new");
        dialog.Find("[data-testid=name-save]").Click();

        Assert.Equal("new", saved);
    }

    [Fact]
    public void An_empty_name_cannot_be_saved()
    {
        // The services substitute a placeholder for a blank name, but a dialog that accepts one and then
        // shows something the user did not type reads as a bug rather than as a default.
        using var ctx = new Bunit.TestContext();
        ctx.Services.AddScoped<ShortcutService>();
        // NameDialog now asks Modal to focus its input on open (a real JS interop call, replacing the
        // autofocus attribute Modal used to render) rather than something bUnit's default strict interop
        // mode allows through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "New project")
            .Add(d => d.InitialValue, ""));

        Assert.True(dialog.Find("[data-testid=name-save]").HasAttribute("disabled"));

        dialog.Find("input").Change("   ");
        Assert.True(dialog.Find("[data-testid=name-save]").HasAttribute("disabled"));

        dialog.Find("input").Change("quarterly");
        Assert.False(dialog.Find("[data-testid=name-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void Cancelling_reports_nothing()
    {
        using var ctx = new Bunit.TestContext();
        ctx.Services.AddScoped<ShortcutService>();
        // NameDialog now asks Modal to focus its input on open (a real JS interop call, replacing the
        // autofocus attribute Modal used to render) rather than something bUnit's default strict interop
        // mode allows through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var saves = 0;
        var cancels = 0;

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "Rename chat")
            .Add(d => d.InitialValue, "old")
            .Add(d => d.OnSave, EventCallback.Factory.Create<string>(new object(), _ => saves++))
            .Add(d => d.OnCancel, EventCallback.Factory.Create(new object(), () => cancels++)));

        dialog.Find("[data-testid=name-cancel]").Click();

        Assert.Equal(0, saves);
        Assert.Equal(1, cancels);
    }

    [Fact]
    public void The_confirm_button_can_be_labelled_for_what_it_does()
    {
        // "Save" is right for a rename and wrong for a creation. One dialog, two verbs.
        using var ctx = new Bunit.TestContext();
        ctx.Services.AddScoped<ShortcutService>();
        // NameDialog now asks Modal to focus its input on open (a real JS interop call, replacing the
        // autofocus attribute Modal used to render) rather than something bUnit's default strict interop
        // mode allows through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "New project")
            .Add(d => d.ConfirmLabel, "Create")
            .Add(d => d.InitialValue, "quarterly"));

        Assert.Equal("Create", dialog.Find("[data-testid=name-save]").TextContent.Trim());
    }
}
