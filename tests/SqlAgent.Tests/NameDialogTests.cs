using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Host.Components.Shared.Ui;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class NameDialogTests
{
    [Fact]
    public void An_arriving_error_asks_the_dialog_to_focus_the_field_again()
    {
        // The defect this closes: the caller re-shows the dialog with an error, Blazor reuses the
        // instance, firstRender is false, and focus stays wherever the failed Save left it.
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<ShortcutService>();

        var dialog = ctx.Render<NameDialog>(p => p.Add(d => d.Title, "New project"));
        var before = dialog.FindComponent<Modal>().Instance.FocusSignal;

        dialog.Render(p => p.Add(d => d.Error, "That name is already taken."));

        Assert.NotEqual(before, dialog.FindComponent<Modal>().Instance.FocusSignal);
        Assert.Contains("already taken", dialog.Markup);
    }

    [Fact]
    public void An_unchanged_error_does_not_keep_asking()
    {
        // A re-render for any other reason — a keystroke elsewhere, a parent's state change — must not
        // yank focus back into the field while the user is somewhere else in the dialog.
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<ShortcutService>();

        var dialog = ctx.Render<NameDialog>(p => p
            .Add(d => d.Title, "New project")
            .Add(d => d.Error, "That name is already taken."));
        var after = dialog.FindComponent<Modal>().Instance.FocusSignal;

        dialog.Render(p => p.Add(d => d.Error, "That name is already taken."));

        Assert.Equal(after, dialog.FindComponent<Modal>().Instance.FocusSignal);
    }

    [Fact]
    public void It_opens_with_the_current_name_already_in_the_box()
    {
        // Renaming starts from what the thing is called, so the common edit — fixing one word — does not
        // begin by retyping the whole name.
        using var ctx = new Bunit.BunitContext();
        ctx.Services.AddScoped<ShortcutService>();
        // NameDialog now asks Modal to focus its input on open (a real JS interop call, replacing the
        // autofocus attribute Modal used to render) rather than something bUnit's default strict interop
        // mode allows through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var dialog = ctx.Render<NameDialog>(p => p
            .Add(d => d.Title, "Rename chat")
            .Add(d => d.Label, "Title")
            .Add(d => d.InitialValue, "quarterly revenue"));

        Assert.Equal("quarterly revenue", dialog.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void Saving_reports_the_edited_name()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.Services.AddScoped<ShortcutService>();
        // NameDialog now asks Modal to focus its input on open (a real JS interop call, replacing the
        // autofocus attribute Modal used to render) rather than something bUnit's default strict interop
        // mode allows through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var saved = "";

        var dialog = ctx.Render<NameDialog>(p => p
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
        using var ctx = new Bunit.BunitContext();
        ctx.Services.AddScoped<ShortcutService>();
        // NameDialog now asks Modal to focus its input on open (a real JS interop call, replacing the
        // autofocus attribute Modal used to render) rather than something bUnit's default strict interop
        // mode allows through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var dialog = ctx.Render<NameDialog>(p => p
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
        using var ctx = new Bunit.BunitContext();
        ctx.Services.AddScoped<ShortcutService>();
        // NameDialog now asks Modal to focus its input on open (a real JS interop call, replacing the
        // autofocus attribute Modal used to render) rather than something bUnit's default strict interop
        // mode allows through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var saves = 0;
        var cancels = 0;

        var dialog = ctx.Render<NameDialog>(p => p
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
        using var ctx = new Bunit.BunitContext();
        ctx.Services.AddScoped<ShortcutService>();
        // NameDialog now asks Modal to focus its input on open (a real JS interop call, replacing the
        // autofocus attribute Modal used to render) rather than something bUnit's default strict interop
        // mode allows through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var dialog = ctx.Render<NameDialog>(p => p
            .Add(d => d.Title, "New project")
            .Add(d => d.ConfirmLabel, "Create")
            .Add(d => d.InitialValue, "quarterly"));

        Assert.Equal("Create", dialog.Find("[data-testid=name-save]").TextContent.Trim());
    }
}
