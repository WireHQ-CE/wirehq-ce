/** Trigger a browser download for in-memory text (e.g. a rendered wg-quick config). */
export function downloadText(filename: string, content: string) {
  download(filename, new Blob([content], { type: 'application/octet-stream' }));
}

export function download(filename: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Filesystem-safe `.conf` filename from a peer name. */
export function confFilename(name: string) {
  const slug = name.replace(/[^a-z0-9-_]+/gi, '-').replace(/^-+|-+$/g, '').toLowerCase();
  return `${slug || 'peer'}.conf`;
}
