// One document-level keydown listener for the whole app. Blazor only hears events on elements it
// rendered, so a shortcut that must work wherever focus is cannot be expressed in Razor at all — the
// same reason sql-editor.js and composer.js exist.
window.sqlAgentShortcuts = {
  bind: function (dotNetRef) {
    const onKeyDown = function (e) {
      if (e.key === 'Escape') {
        // Not prevented: an Escape that also dismisses a native autocomplete or an IME candidate list is
        // the browser's business, and this handler only tells C# it happened.
        dotNetRef.invokeMethodAsync('OnEscape').catch(() => {});
        return;
      }
      if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
        // Prevented: Chrome puts Ctrl+K in the address bar and Firefox in its search field, so without
        // this the app's own shortcut loses to the browser's every time.
        e.preventDefault();
        dotNetRef.invokeMethodAsync('OnSearch').catch(() => {});
      }
    };

    document.addEventListener('keydown', onKeyDown);
    // Kept so unbind removes exactly this listener; a circuit that reconnects must not leave the old one
    // attached to a dead DotNetObjectReference. The catch above covers the window between a dropped
    // circuit and the unbind that follows it.
    window._sqlAgentShortcutHandler = onKeyDown;
  },

  unbind: function () {
    if (!window._sqlAgentShortcutHandler) return;
    document.removeEventListener('keydown', window._sqlAgentShortcutHandler);
    delete window._sqlAgentShortcutHandler;
  },
};
