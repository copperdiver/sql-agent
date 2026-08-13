// One document-level keydown listener for the whole app. Blazor only hears events on elements it
// rendered, so a shortcut that must work wherever focus is cannot be expressed in Razor at all — the
// same reason sql-editor.js and composer.js exist.
window.sqlAgentShortcuts = {
  bind: function (dotNetRef) {
    // A circuit can reconnect and bind again without ever unbinding first (the DisposeAsync call that
    // would have unbound the old ref races the reconnect, and can lose). Without this, the old listener
    // is orphaned rather than replaced: it stays attached to a dead DotNetObjectReference forever, and
    // every keystroke after that invokes OnEscape/OnSearch on both the dead ref (silently swallowed by
    // the catch below) and the live one.
    this.unbind();

    const onKeyDown = function (e) {
      if (e.key === 'Escape') {
        // Not prevented: an Escape that also dismisses a native autocomplete or an IME candidate list is
        // the browser's business, and this handler only tells C# it happened.
        dotNetRef.invokeMethodAsync('OnEscape').catch(() => {});
        return;
      }
      // Lowercased key, and neither modifier: e.key stays 'K' with Shift held, so a plain 'k'/'K' check
      // also fires this on Ctrl/Cmd+Shift+K, which is Chrome and Firefox's own "reopen closed tab"
      // shortcut — this used to steal it via preventDefault below. Excluding altKey matters on Windows
      // too: AltGr is reported as ctrlKey + altKey both true, not as a distinct modifier, so a check on
      // ctrlKey alone treated every AltGr-shifted character on a European keyboard layout as Ctrl+K.
      if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'k') {
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
