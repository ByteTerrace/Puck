/**
 * The one spelling of a workspace file's identity. A source file is named by its worlds-relative path
 * (`games/klondike.puck`), exactly as the official manifest's `sources[]` names it; the language server names the
 * same file by a `file:///worlds/<path>` URI.
 */

const URI_PREFIX = "file:///worlds/";

/** The language server URI for a worlds-relative path. */
export function sourceUri(path: string): string {
  return URI_PREFIX + path.split("/").map(encodeURIComponent).join("/");
}

/** The worlds-relative path a language server URI names, or `null` for a URI outside the workspace. */
export function sourcePath(uri: string): string | null {
  return uri.startsWith(URI_PREFIX) ? uri.slice(URI_PREFIX.length).split("/").map(decodeURIComponent).join("/") : null;
}

/** Whether a workspace file is `.puck` source, which the studio edits, rather than JSON it can only show. */
export function isPuckSource(path: string): boolean {
  return path.endsWith(".puck");
}
