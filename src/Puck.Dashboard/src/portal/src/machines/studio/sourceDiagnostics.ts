/**
 * The language server's diagnostics as the studio machine reads them: every `textDocument/publishDiagnostics`
 * notification becomes the newest source-tier diagnostics for one workspace file, at the file version the server
 * says they describe. A publication older than one already seen for the same file is dropped, so a slow unit can
 * never overwrite a newer answer. The editor shows these merged with the world engine's semantic tier
 * (`workspace.ts`'s `editorDiagnostics`), never the server's publication alone.
 */
import { filter, map, type Observable, scan } from "rxjs";
import { sourcePath } from "../../document/sourcePaths";
import type { LspMessage, SourceDiagnostic, SourceSeverity } from "../../native/engineTypes";

/** One file's newest diagnostics. */
export interface PublishedDiagnostics {
  readonly path: string;
  readonly version: number | null;
  readonly diagnostics: readonly SourceDiagnostic[];
}

interface LspPosition { readonly line: number; readonly character: number }
interface LspDiagnostic {
  readonly range: { readonly start: LspPosition; readonly end: LspPosition };
  readonly severity?: number;
  readonly code?: string | number;
  readonly message: string;
}
interface PublishParams {
  readonly uri: string;
  readonly version?: number | null;
  readonly diagnostics: readonly LspDiagnostic[];
}

const SEVERITY: Readonly<Record<number, SourceSeverity>> = { 1: "error", 2: "warning", 3: "information", 4: "information" };

/** Converts one LSP diagnostic (0-based range) into the studio's source diagnostic (1-based line and column). */
export function toSourceDiagnostic(path: string, diagnostic: LspDiagnostic): SourceDiagnostic {
  const { start, end } = diagnostic.range;
  return {
    code: diagnostic.code === undefined ? "" : String(diagnostic.code),
    severity: SEVERITY[diagnostic.severity ?? 1] ?? "error",
    message: diagnostic.message,
    path,
    line: start.line + 1,
    column: start.character + 1,
    length: (end.line === start.line) ? Math.max(0, end.character - start.character) : 1,
  };
}

/** The newest diagnostics per workspace file, one emission per accepted publication. */
export function sourceDiagnostics(messages$: Observable<LspMessage>): Observable<PublishedDiagnostics> {
  return messages$.pipe(
    filter((message) => message.method === "textDocument/publishDiagnostics"),
    map((message): PublishedDiagnostics | null => {
      const params = message.params as PublishParams;
      const path = sourcePath(params.uri);
      if (path === null) return null;
      return {
        path,
        version: params.version ?? null,
        diagnostics: params.diagnostics.map((diagnostic) => toSourceDiagnostic(path, diagnostic)),
      };
    }),
    filter((published): published is PublishedDiagnostics => published !== null),
    scan(({ newest }, published) => {
      const known = newest.get(published.path);
      const stale = known !== undefined && known !== null && published.version !== null && published.version < known;
      if (!stale) newest.set(published.path, published.version);
      return { newest, accepted: stale ? null : published };
    }, { newest: new Map<string, number | null>(), accepted: null as PublishedDiagnostics | null }),
    map(({ accepted }) => accepted),
    filter((published): published is PublishedDiagnostics => published !== null),
  );
}
