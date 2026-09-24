import { useEffect, useRef, useState } from "react";
import { Text, UnstyledButton } from "@mantine/core";
import { RiFileCodeLine } from "@remixicon/react";
import { json } from "@codemirror/lang-json";
import { LSPPlugin } from "@codemirror/lsp-client";
import { setDiagnostics, type Diagnostic } from "@codemirror/lint";
import { EditorState, type Extension, type Text as Doc } from "@codemirror/state";
import { EditorView, highlightActiveLine, highlightActiveLineGutter, lineNumbers } from "@codemirror/view";
import { minimalSetup } from "codemirror";
import { filter } from "rxjs";
import { StudioContext, useStudioChecking, useStudioLanguageChannel, useStudioProblems, useStudioWorkspace } from "../../../context/StudioContext";
import { isPuckSource, sourcePath, sourceUri } from "../../../document/sourcePaths";
import type { WorkspaceState } from "../../../machines/studio/types";
import { editorDiagnostics } from "../../../machines/studio/workspace";
import type { SourceDiagnostic } from "../../../native/engineTypes";
import { puckEditorTheme } from "../../../ui/codemirror";
import { EmptyState } from "../../../ui/EmptyState";
import { ExplorerItem } from "../../../ui/ExplorerItem";
import { Kicker } from "../../../ui/Kicker";
import { languageClientFor, type LanguageClient } from "./languageClient";
import { semanticTokens } from "./semanticTokens";
import classes from "./SourceEditor.module.css";

/** One outline entry, from a hierarchical `DocumentSymbol` or a flat `SymbolInformation`. */
export interface OutlineSymbol {
  readonly name: string;
  readonly depth: number;
  readonly start: { readonly line: number; readonly character: number };
}

interface LspSymbol {
  readonly name: string;
  readonly selectionRange?: { readonly start: OutlineSymbol["start"] };
  readonly location?: { readonly range: { readonly start: OutlineSymbol["start"] } };
  readonly children?: readonly LspSymbol[];
}

/** Flattens a `textDocument/documentSymbol` answer into outline rows, depth first. */
export function outlineRows(symbols: readonly LspSymbol[] | null, depth = 0): OutlineSymbol[] {
  return (symbols ?? []).flatMap((symbol) => {
    const start = symbol.selectionRange?.start ?? symbol.location?.range.start ?? { line: 0, character: 0 };
    return [{ name: symbol.name, depth, start }, ...outlineRows(symbol.children ?? null, depth + 1)];
  });
}

const LINT_SEVERITY: Record<SourceDiagnostic["severity"], Diagnostic["severity"]> = { error: "error", warning: "warning", information: "info" };

/** Places one source diagnostic in `doc`: at its 1-based line and column for its length, or across the first line
 * when it is about the whole document (line 0). */
export function lintDiagnostic(doc: Doc, problem: SourceDiagnostic): Diagnostic {
  const line = doc.line(Math.min(Math.max(1, problem.line), doc.lines));
  const from = problem.line === 0 ? line.from : Math.min(line.from + Math.max(0, problem.column - 1), line.to);
  const to = problem.line === 0 ? line.to : Math.min(from + Math.max(1, problem.length), doc.length);
  return { from, to, severity: LINT_SEVERITY[problem.severity], message: problem.code ? `${problem.code}: ${problem.message}` : problem.message };
}

// Each file's editor state (its buffer and undo history) for the open workspace, kept across file switches and
// across the Source tab unmounting. A new workspace starts a new set.
const editorStates = new Map<string, EditorState>();
let editorStatesWorkspace = -1;

function statesFor(workspaceId: number): Map<string, EditorState> {
  if (workspaceId !== editorStatesWorkspace) {
    editorStates.clear();
    editorStatesWorkspace = workspaceId;
  }
  return editorStates;
}

/**
 * The Source tab: the workspace's files, the open file's editor, and its outline. A `.puck` file edits through the
 * engine's language server: completion, hover, diagnostics, formatting (Shift-Alt-F), and the server's own semantic
 * tokens. A workspace file with no `.puck` source opens read-only. Every buffer change reaches the machine as
 * SOURCE_CHANGED; CodeMirror keeps each file's undo history.
 */
export function SourceEditor() {
  const actor = StudioContext.useActorRef();
  const workspace = useStudioWorkspace();
  const channel = useStudioLanguageChannel();
  const containerRef = useRef<HTMLDivElement>(null);
  const viewRef = useRef<EditorView | null>(null);
  const shownRef = useRef<{ readonly workspaceId: number; readonly path: string } | null>(null);
  const [outline, setOutline] = useState<{ path: string; rows: OutlineSymbol[] } | null>(null);

  const versionOf = (uri: string) => {
    const path = sourcePath(uri);
    return (path && actor.getSnapshot().context.workspace?.files[path]?.version) || 0;
  };
  // The client is created by the first effect that needs it (never while rendering), then kept for the channel.
  const languageClient = (): LanguageClient | null => (channel ? languageClientFor(channel, versionOf) : null);

  const stateFor = (current: WorkspaceState, path: string): EditorState => {
    const language = languageClient();
    const puck = isPuckSource(path);
    const extensions: Extension[] = [
      minimalSetup,
      lineNumbers(),
      highlightActiveLine(),
      highlightActiveLineGutter(),
      puckEditorTheme,
      EditorView.contentAttributes.of({ "aria-label": `Source of ${path}` }),
      EditorView.updateListener.of((update) => {
        if (!update.docChanged || !puck) return;
        const file = actor.getSnapshot().context.workspace?.files[path];
        if (!file) return;
        actor.send({ type: "SOURCE_CHANGED", path, version: file.version + 1, text: update.state.doc.toString() });
      }),
      puck
        ? (language ? [language.client.plugin(sourceUri(path), "puck"), semanticTokens(language.client, language.workspace)] : [])
        : [json(), EditorState.readOnly.of(true), EditorView.editable.of(false)],
    ];
    return EditorState.create({ doc: current.files[path].text, extensions });
  };

  useEffect(() => {
    if (!containerRef.current) return;
    const view = new EditorView({ parent: containerRef.current });
    viewRef.current = view;
    return () => {
      const shown = shownRef.current;
      if (shown && shown.workspaceId === editorStatesWorkspace) editorStates.set(shown.path, view.state);
      shownRef.current = null;
      viewRef.current = null;
      view.destroy();
    };
  }, []);

  const workspaceId = workspace?.id ?? -1;
  const active = workspace?.active ?? null;
  useEffect(() => {
    const view = viewRef.current;
    const current = actor.getSnapshot().context.workspace;
    if (!view || !current || !active) return;
    const shown = shownRef.current;
    if (shown && shown.workspaceId === current.id && shown.path === active) return;
    if (shown && shown.workspaceId === current.id) editorStates.set(shown.path, view.state);
    const states = statesFor(current.id);
    view.setState(states.get(active) ?? stateFor(current, active));
    shownRef.current = { workspaceId: current.id, path: active };
    // The editor is rebuilt only when the file or the workspace changes; typing never re-renders it.
  }, [workspaceId, active, channel]);

  // Both tiers, merged for the open file at its current version; `null` keeps what the editor shows until then.
  const problems = StudioContext.useSelector(
    (snapshot) => (snapshot.context.workspace ? editorDiagnostics(snapshot.context.workspace, snapshot.context.workspace.active) : null),
    (left, right) => left === right || (!!left && !!right && left.length === right.length && left.every((item, index) => item === right[index])),
  );
  useEffect(() => {
    const view = viewRef.current;
    if (!view || !problems || shownRef.current?.path !== active) return;
    view.dispatch(setDiagnostics(view.state, problems.map((problem) => lintDiagnostic(view.state.doc, problem))));
  }, [problems, active, workspaceId]);

  const reveal = workspace?.reveal ?? null;
  useEffect(() => {
    const view = viewRef.current;
    if (!view || !reveal || reveal.path !== shownRef.current?.path) return;
    const doc = view.state.doc;
    const line = doc.line(Math.min(Math.max(1, reveal.line), doc.lines));
    const from = Math.min(line.from + Math.max(0, reveal.column - 1), line.to);
    const to = Math.min(from + reveal.length, doc.length);
    view.dispatch({ selection: { anchor: from, head: to }, scrollIntoView: true });
    view.focus();
  }, [reveal?.nonce]);

  useEffect(() => {
    const language = languageClient();
    if (!language || !active || !isPuckSource(active)) return;
    const uri = sourceUri(active);
    const subscription = language.workspace.synced$.pipe(filter((synced) => synced === uri)).subscribe(() => {
      void language.client.request<{ textDocument: { uri: string } }, readonly LspSymbol[] | null>(
        "textDocument/documentSymbol", { textDocument: { uri } },
      ).then((symbols) => setOutline({ path: active, rows: outlineRows(symbols) }), () => undefined);
    });
    return () => subscription.unsubscribe();
  }, [channel, active]);

  const goTo = (symbol: OutlineSymbol) => {
    const view = viewRef.current;
    const plugin = view && LSPPlugin.get(view);
    if (!view || !plugin) return;
    const position = plugin.unsyncedChanges.mapPos(plugin.fromPosition(symbol.start, plugin.syncedDoc));
    view.dispatch({ selection: { anchor: position }, scrollIntoView: true });
    view.focus();
  };

  const rows = (outline && outline.path === active) ? outline.rows : [];

  return (
    <section aria-label="Source" className={classes.workspace}>
      <aside aria-label="Workspace files" className={classes.files}>
        <Kicker c="dimmed" mb="sm" px="xs">Files</Kicker>
        {workspace && <FileTree workspace={workspace} onOpen={(path) => actor.send({ type: "OPEN_FILE", path })} />}
      </aside>
      <div className={classes.editorPane}>
        <SourceStatus />
        <div className={classes.editorFrame}>
          <div className={classes.editor} ref={containerRef} hidden={!workspace} />
          {!workspace && (
            <EmptyState icon={<RiFileCodeLine size={22} />} title="No document open">
              Open a document to edit its source.
            </EmptyState>
          )}
        </div>
      </div>
      <aside aria-label="Outline" className={classes.outline}>
        <Kicker c="dimmed" mb="sm" px="xs">Outline</Kicker>
        {rows.length === 0
          ? <Text size="xs" c="dimmed" px="xs">{active && isPuckSource(active) ? "No symbols yet." : "Nothing to outline."}</Text>
          : (
            <ol className={classes.symbols}>
              {rows.map((symbol, index) => (
                <li key={`${symbol.name}-${index}`}>
                  <UnstyledButton className={classes.symbol} style={{ paddingInlineStart: `calc(${symbol.depth} * var(--mantine-spacing-sm) + var(--mantine-spacing-xs))` }} onClick={() => goTo(symbol)} title={symbol.name}>
                    {symbol.name}
                  </UnstyledButton>
                </li>
              ))}
            </ol>
          )}
      </aside>
    </section>
  );
}

/** The one-line status above the editor: the open file and what the studio knows about it. It holds one line's
 * height whatever it says, so nothing below it moves. */
function SourceStatus() {
  const workspace = useStudioWorkspace();
  const checking = useStudioChecking();
  const problems = useStudioProblems();
  if (!workspace) return <div className={classes.status}><Text size="xs" c="dimmed">No file open.</Text></div>;
  const active = workspace.active;
  const errors = problems.filter((problem) => problem.path === active && problem.severity === "error").length;
  const note = !isPuckSource(active)
    ? "Read-only: this document has no .puck source yet."
    : checking ? "Checking…" : errors > 0 ? `${errors} error${errors === 1 ? "" : "s"}` : "No errors";
  return (
    <div className={classes.status} role="status">
      <Text size="xs" ff="monospace" className={classes.path} title={active}>{active}</Text>
      <Text size="xs" c={errors > 0 && isPuckSource(active) ? "red" : "dimmed"} className={classes.note}>{note}</Text>
    </div>
  );
}

/** The workspace's files by directory. A changed file carries a dot in a slot every row reserves. */
function FileTree({ workspace, onOpen }: { readonly workspace: WorkspaceState; readonly onOpen: (path: string) => void }) {
  const byDirectory = new Map<string, string[]>();
  for (const path of Object.keys(workspace.files).sort()) {
    const slash = path.lastIndexOf("/");
    const directory = slash < 0 ? "" : path.slice(0, slash);
    byDirectory.set(directory, [...(byDirectory.get(directory) ?? []), path]);
  }
  return (
    <div className={classes.tree}>
      {[...byDirectory.entries()].map(([directory, paths]) => (
        <div key={directory}>
          {directory && <Text className={classes.directory}>{directory}/</Text>}
          {paths.map((path) => {
            const file = workspace.files[path];
            const changed = file.text !== file.savedText;
            return (
              <ExplorerItem
                key={path}
                name={path.slice(path.lastIndexOf("/") + 1)}
                selected={path === workspace.active}
                onClick={() => onOpen(path)}
                aside={<span className={classes.changed} aria-label={changed ? "unsaved changes" : undefined}>{changed ? "●" : ""}</span>}
              />
            );
          })}
        </div>
      ))}
    </div>
  );
}

export default SourceEditor;
