// Turns an in-memory string into a file download. Kept in JS because a browser cannot be handed
// bytes from .NET without either a blob URL or a round trip through the server.
window.sqlAgentDownload = (filename, mimeType, content) => {
  const url = URL.createObjectURL(new Blob([content], { type: mimeType }));
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
};
