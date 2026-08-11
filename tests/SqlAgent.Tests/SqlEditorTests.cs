using Bunit;
using SqlAgent.Host.Components.Shared;

namespace SqlAgent.Tests;

/// <summary>
/// Direct coverage of the JS interop <see cref="SqlEditor"/> performs. <see cref="WorkspaceTests"/>
/// exercises the SQL tab end to end and proves <c>sqlAgentEditor.create</c> fires on first render; these
/// tests isolate the component itself and prove the other half of the contract: that setting
/// <see cref="SqlEditor.Value"/> from outside the editor (not via <see cref="SqlEditor.OnEditorChanged"/>,
/// which is what the editor's own keystrokes go through) actually reaches the browser via
/// <c>sqlAgentEditor.setValue</c> — the exact plumbing a future "open in editor" action from the chat tab
/// will depend on — and that the anti-echo guard in <see cref="SqlEditor.OnAfterRenderAsync"/> prevents
/// the user's own keystrokes from being pushed straight back into the editor.
/// </summary>
public class SqlEditorTests
{
    [Fact]
    public void First_render_creates_the_editor_with_the_initial_value()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.SetupVoid("sqlAgentEditor.create", _ => true);
        ctx.JSInterop.SetupVoid("sqlAgentEditor.destroy", _ => true); // fires when ctx disposes the component below

        ctx.RenderComponent<SqlEditor>(p => p.Add(e => e.Value, "SELECT 1"));

        var invocation = ctx.JSInterop.VerifyInvoke("sqlAgentEditor.create");
        Assert.Equal("SELECT 1", invocation.Arguments[2]);
    }

    [Fact]
    public void Changing_Value_after_the_editor_is_mounted_pushes_it_into_the_browser_via_setValue()
    {
        // This is the path a future "open in editor" action (parent sets Value programmatically) relies
        // on: the editor is already mounted, so OnAfterRenderAsync's non-first-render branch must call
        // sqlAgentEditor.setValue with the new text.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.SetupVoid("sqlAgentEditor.create", _ => true);
        ctx.JSInterop.SetupVoid("sqlAgentEditor.setValue", _ => true);
        ctx.JSInterop.SetupVoid("sqlAgentEditor.destroy", _ => true);

        var editor = ctx.RenderComponent<SqlEditor>(p => p.Add(e => e.Value, "SELECT 1"));
        editor.SetParametersAndRender(p => p.Add(e => e.Value, "SELECT 2"));

        var invocation = ctx.JSInterop.VerifyInvoke("sqlAgentEditor.setValue");
        Assert.Equal("SELECT 2", invocation.Arguments[1]);
    }

    [Fact]
    public async Task The_editors_own_keystrokes_are_not_echoed_back_via_setValue()
    {
        // OnEditorChanged is what the browser calls on every keystroke (see sql-editor.js's 'change'
        // listener). If this fed back into setValue, every keystroke would fight the user's own caret.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.SetupVoid("sqlAgentEditor.create", _ => true);
        ctx.JSInterop.SetupVoid("sqlAgentEditor.destroy", _ => true);

        var editor = ctx.RenderComponent<SqlEditor>(p => p.Add(e => e.Value, ""));
        await editor.InvokeAsync(() => editor.Instance.OnEditorChanged("SELECT 1"));
        editor.Render();

        Assert.Empty(ctx.JSInterop.Invocations.Where(i => i.Identifier == "sqlAgentEditor.setValue"));
    }
}
