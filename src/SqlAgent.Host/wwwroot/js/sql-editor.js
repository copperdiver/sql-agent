// Bridges CodeMirror to the Blazor component. The editor owns the text while it is focused; .NET is
// notified on every change so @bind-style flow still works, and Ctrl+Enter runs the query.
window.sqlAgentEditor = {
  create: (element, dotNetRef, initialValue) => {
    const editor = CodeMirror(element, {
      value: initialValue || '',
      mode: 'text/x-sql',
      lineNumbers: true,
      viewportMargin: Infinity,
      extraKeys: {
        // .catch() swallows a rejection from calling into a DotNetObjectReference that was disposed after
        // this key handler fired but before invokeMethodAsync's message reached .NET (a stray keystroke
        // racing a tab switch, for instance) — without it, that shows up as an unhandled promise rejection
        // in the browser console for something the user can't do anything about.
        'Ctrl-Enter': () => dotNetRef.invokeMethodAsync('RunFromEditor').catch(() => {}),
        'Cmd-Enter': () => dotNetRef.invokeMethodAsync('RunFromEditor').catch(() => {}),
      },
    });
    const onChange = () => dotNetRef.invokeMethodAsync('OnEditorChanged', editor.getValue()).catch(() => {});
    editor.on('change', onChange);
    element._cm = editor;
    element._cmOnChange = onChange;
  },
  setValue: (element, value) => {
    const editor = element._cm;
    if (editor && editor.getValue() !== value) editor.setValue(value || '');
  },
  // Detaches the CodeMirror instance created in create() so it and the DotNetObjectReference its
  // closures captured can be garbage collected. CodeMirror 5 (constructed this way, not via
  // fromTextArea) has no built-in destroy: the documented way to tear one down is to stop referencing it
  // and let its DOM go. Explicitly off()-ing the 'change' listener and removing the wrapper matters
  // because the SQL page can be unmounted and remounted repeatedly (navigating to the chat page and back),
  // and without this each remount would leak one editor instance plus one live 'change' closure still
  // holding a DotNetObjectReference into a component that no longer exists.
  destroy: (element) => {
    const editor = element._cm;
    if (!editor) return;
    if (element._cmOnChange) editor.off('change', element._cmOnChange);
    const wrapper = editor.getWrapperElement();
    if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
    element._cm = null;
    element._cmOnChange = null;
  },
};
