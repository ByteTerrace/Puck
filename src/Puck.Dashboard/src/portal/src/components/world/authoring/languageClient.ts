/**
 * The studio's `@codemirror/lsp-client` connection: one client per language server channel, kept for the channel's
 * life so the server is initialized once however often the Source tab mounts.
 *
 * `StudioWorkspace` replaces lsp-client's default workspace for two reasons. A file's version is the studio
 * machine's own count of editor changes (the number SOURCE_CHANGED carries), so a diagnostics publication names a
 * version the machine can compare. And a file the editor stops showing is sent its unsynced text before it closes,
 * so switching files never leaves the server a stale buffer. Each sync is announced on `synced$`, which the
 * semantic-token and outline requests follow.
 */
import { formatKeymap, hoverTooltips, LSPClient, LSPPlugin, serverCompletion, serverDiagnostics, Workspace, type WorkspaceFile } from "@codemirror/lsp-client";
import type { Text } from "@codemirror/state";
import { type EditorView, keymap } from "@codemirror/view";
import { Subject } from "rxjs";
import type { LanguageServerChannel } from "../../../native/engineTypes";
import { channelTransport } from "../../../native/lspTransport";

type WorkspaceFileUpdate = ReturnType<Workspace["syncFiles"]>[number];

/** The machine's current version of the file a URI names. */
export type FileVersion = (uri: string) => number;

class StudioWorkspaceFile implements WorkspaceFile {
  constructor(
    readonly uri: string,
    readonly languageId: string,
    public version: number,
    public doc: Text,
    readonly view: EditorView,
  ) {}

  getView(): EditorView {
    return this.view;
  }
}

export class StudioWorkspace extends Workspace {
  files: StudioWorkspaceFile[] = [];
  /** URIs whose current text the server has just been sent. */
  readonly synced$ = new Subject<string>();
  private readonly versionOf: FileVersion;

  constructor(client: LSPClient, versionOf: FileVersion) {
    super(client);
    this.versionOf = versionOf;
  }

  private announce(uris: readonly string[]): void {
    // After the notifications, which lsp-client sends once its initialization settles.
    if (uris.length > 0) void this.client.initializing.then(() => uris.forEach((uri) => this.synced$.next(uri)));
  }

  syncFiles(): readonly WorkspaceFileUpdate[] {
    const updates: WorkspaceFileUpdate[] = [];
    for (const file of this.files) {
      const plugin = LSPPlugin.get(file.view);
      if (!plugin || plugin.unsyncedChanges.empty) continue;
      updates.push({ changes: plugin.unsyncedChanges, file, prevDoc: file.doc });
      file.doc = file.view.state.doc;
      file.version = this.versionOf(file.uri);
      plugin.clear();
    }
    this.announce(updates.map((update) => update.file.uri));
    return updates;
  }

  openFile(uri: string, languageId: string, view: EditorView): void {
    if (this.getFile(uri)) throw new Error(`'${uri}' is already open in another editor.`);
    const file = new StudioWorkspaceFile(uri, languageId, this.versionOf(uri), view.state.doc, view);
    this.files.push(file);
    this.client.didOpen(file);
    this.announce([uri]);
  }

  closeFile(uri: string, view: EditorView): void {
    const file = this.getFile(uri) as StudioWorkspaceFile | null;
    if (!file || file.view !== view) return;
    if (!file.doc.eq(view.state.doc)) {
      file.doc = view.state.doc;
      file.version = this.versionOf(uri);
      this.client.notification("textDocument/didChange", {
        textDocument: { uri, version: file.version },
        contentChanges: [{ text: file.doc.toString() }],
      });
    }
    this.files = this.files.filter((candidate) => candidate !== file);
    this.client.didClose(uri);
  }
}

const clientCapabilities = {
  textDocument: {
    documentSymbol: { hierarchicalDocumentSymbolSupport: true },
    semanticTokens: { requests: { full: true }, tokenTypes: [], tokenModifiers: [], formats: ["relative"] },
  },
};

export interface LanguageClient {
  readonly client: LSPClient;
  readonly workspace: StudioWorkspace;
}

const clients = new WeakMap<LanguageServerChannel, LanguageClient>();

/** The channel's client, created and connected the first time an editor asks for it. */
export function languageClientFor(channel: LanguageServerChannel, versionOf: FileVersion): LanguageClient {
  const known = clients.get(channel);
  if (known) return known;
  let workspace: StudioWorkspace | null = null;
  const client = new LSPClient({
    rootUri: "file:///worlds/",
    // The language engine diagnoses the cheap source tier only, so it stays responsive while typing; the semantic
    // tier comes from the world engine's check.
    initializationOptions: { diagnostics: "source" },
    // An idle diagnostic unit runs to completion before a request is answered, so allow for a long one.
    timeout: 30_000,
    workspace: (owner) => (workspace = new StudioWorkspace(owner, versionOf)),
    // The editor shows both tiers merged (SourceEditor sets them), so the server's publication alone never replaces
    // them; `serverDiagnostics()` below stays for its version support and its sync after 500 ms of quiet.
    notificationHandlers: { "textDocument/publishDiagnostics": () => true },
    extensions: [
      serverCompletion(),
      hoverTooltips(),
      serverDiagnostics(),
      // In an array: lsp-client passes an extension through only when it is an array or carries `extension`.
      [keymap.of(formatKeymap)],
      { clientCapabilities },
    ],
  }).connect(channelTransport(channel));
  const created = { client, workspace: workspace! };
  clients.set(channel, created);
  return created;
}
