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
        'Ctrl-Enter': () => dotNetRef.invokeMethodAsync('RunFromEditor'),
        'Cmd-Enter': () => dotNetRef.invokeMethodAsync('RunFromEditor'),
      },
    });
    editor.on('change', () => dotNetRef.invokeMethodAsync('OnEditorChanged', editor.getValue()));
    element._cm = editor;
  },
  setValue: (element, value) => {
    const editor = element._cm;
    if (editor && editor.getValue() !== value) editor.setValue(value || '');
  },
};
