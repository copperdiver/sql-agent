// Enter-to-send and auto-grow, in JS for the same reason sql-editor.js exists: neither is expressible
// in Blazor alone. @onkeydown:preventDefault is evaluated when the element renders, not per keystroke,
// so it cannot tell Enter from Shift+Enter — and without preventDefault the browser inserts the newline
// before the handler can stop it, sending a question with a stray line break on the end.
window.sqlAgentComposer = {
  bind: function (textarea, dotNetRef) {
    if (!textarea) return;

    const onKeyDown = function (e) {
      // Shift+Enter is a newline, as in every chat composer. IME composition (Japanese, Chinese,
      // Korean) also raises Enter to accept a candidate; isComposing tells that apart from a send.
      if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
      e.preventDefault();
      // .catch() swallows a rejection from calling into a DotNetObjectReference that was disposed after
      // this key handler fired but before invokeMethodAsync's message reached .NET (a stray keystroke
      // racing a tab switch or a dropped circuit, for instance) — without it, that shows up as an
      // unhandled promise rejection in the browser console for something the user can't do anything
      // about. Same reasoning as sql-editor.js's Ctrl-Enter handler.
      dotNetRef.invokeMethodAsync('SendFromEditor').catch(() => {});
    };

    const onInput = function () {
      // Auto-grow to content, capped at 40vh so a pasted essay cannot push the transcript off screen.
      textarea.style.height = 'auto';
      const cap = window.innerHeight * 0.4;
      textarea.style.height = Math.min(textarea.scrollHeight, cap) + 'px';
    };

    textarea.addEventListener('keydown', onKeyDown);
    textarea.addEventListener('input', onInput);
    // Kept on the element so unbind can remove exactly these listeners; a component that is disposed
    // and re-created (navigating between chats) must not leave the old ones attached to a dead
    // DotNetObjectReference.
    textarea._sqlAgentComposer = { onKeyDown: onKeyDown, onInput: onInput };
    onInput();
  },

  unbind: function (textarea) {
    const handlers = textarea && textarea._sqlAgentComposer;
    if (!handlers) return;
    textarea.removeEventListener('keydown', handlers.onKeyDown);
    textarea.removeEventListener('input', handlers.onInput);
    delete textarea._sqlAgentComposer;
  },
};
