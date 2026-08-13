using Microsoft.AspNetCore.Components;

namespace SqlAgent.Host.Web;

/// <summary>
/// The one dialog the circuit is currently showing.
///
/// It exists because of where dialogs get asked for. Below 1024px the sidebar is a drawer carrying a CSS
/// transform, and a transform makes the element a containing block for position:fixed descendants — so a
/// Modal rendered inside the sidebar centres on the drawer rather than the viewport, overhangs it, and
/// rides off-screen when the drawer closes while still considering itself open. Rendering every dialog
/// from MainLayout, outside that subtree, avoids the problem without a portal.
///
/// Scoped to the circuit, like AppState: one dialog per browser tab.
/// </summary>
public sealed class DialogService
{
    public RenderFragment? Current { get; private set; }

    /// <summary>Raised when <see cref="Current"/> changes. DialogHost subscribes; it is not the
    /// component handling the event that opened the dialog, so nothing else would re-render it.</summary>
    public event Action? Changed;

    /// <summary>Shows a dialog, replacing any dialog already open. One host means a second dialog would
    /// otherwise stack invisibly behind the first.</summary>
    public void Show(RenderFragment dialog)
    {
        Current = dialog;
        Changed?.Invoke();
    }

    public void Close()
    {
        Current = null;
        Changed?.Invoke();
    }
}
