namespace SqlAgent.Host.Web;

/// <summary>
/// Keyboard events that have to reach components regardless of where focus is, fed by the single
/// document-level listener in <c>wwwroot/js/shortcuts.js</c>.
///
/// Escape is here because Safari does not focus a <c>&lt;button&gt;</c> on a plain mouse click — a macOS
/// convention — so a popover opened by mouse has nothing for a bubbling Escape to start from and cannot
/// be dismissed from the keyboard. Ctrl/Cmd+K is here because a shortcut that only works when focus
/// happens to be inside the app's own markup is not a shortcut.
///
/// Scoped to the circuit, like AppState and DialogService: one browser tab, one set of subscribers.
/// </summary>
public sealed class ShortcutService
{
    /// <summary>Raised on Escape anywhere in the document. Subscribers attach while they are open and
    /// detach when they close, so a sidebar of twenty menus is not twenty live handlers.</summary>
    public event Action? EscapePressed;

    /// <summary>Raised on Ctrl/Cmd+K anywhere in the document.</summary>
    public event Action? SearchRequested;

    public void RaiseEscape() => EscapePressed?.Invoke();

    public void RaiseSearch() => SearchRequested?.Invoke();
}
