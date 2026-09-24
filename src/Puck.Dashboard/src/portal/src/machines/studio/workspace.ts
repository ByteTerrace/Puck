/**
 * The source workspace: what opening a document reads, how the studio's edits reach each engine, and the questions
 * the machine asks of a workspace's current revision. The machine's reducers stay pure over these helpers; only
 * `openWorkspace` and `syncWorkspace` touch an engine.
 */
import { parseDocumentText } from "../../document/jsonText";
import { isPuckSource } from "../../document/sourcePaths";
import type { DraftFiles, StudioDraft } from "../../document/localDrafts";
import type { SourceComposeResult, SourceDiagnostic, WorldEngine } from "../../native/engineTypes";
import type { OfficialLoad } from "../../official/officialClient";
import type { FileDiagnostics, IslandCheck, WorkspaceFile, WorkspaceState } from "./types";

/** A workspace as it opens, before the machine numbers it. */
export type OpenedWorkspace = Pick<WorkspaceState, "documentName" | "role" | "draftId" | "entry" | "root" | "rootRefusal" | "files" | "active">;

/** The source the island check composes for a fragment: the manifest's composed island root's own source. */
export function islandRootSource(official: OfficialLoad): string | null {
  const root = official.manifest.composed[0];
  if (!root) return null;
  return official.manifest.documents.find((document) => document.name === root.name)?.source ?? null;
}

/**
 * Reads every `sources[]` file of the official build (hash-verified, from the byte store after the first read),
 * lays a draft's files over it, and picks the document's own source as the file to open. The language engine's
 * mount is the machine's next step, so a mount failure can be reported without losing the opened workspace.
 */
export async function openWorkspace(official: OfficialLoad, documentName: string, draft: StudioDraft | null): Promise<OpenedWorkspace> {
  const entry = official.manifest.documents.find((document) => document.name === documentName);
  if (!entry) throw new Error(`the official build names no document '${documentName}'.`);

  const texts = await Promise.all(official.manifest.sources.map(async (source) => [source.name, await official.sources.get(source.name)] as const));
  const overlay: DraftFiles = draft?.revisions[0]?.files ?? {};
  const files: Record<string, WorkspaceFile> = {};
  for (const [path, text] of texts) {
    const current = Object.hasOwn(overlay, path) ? overlay[path] : text;
    files[path] = { text: current, savedText: current, officialText: text, version: 0 };
  }
  for (const [path, text] of Object.entries(overlay)) {
    if (!Object.hasOwn(files, path)) files[path] = { text, savedText: text, officialText: "", version: 0 };
  }
  if (!Object.hasOwn(files, entry.source)) {
    throw new Error(`the official build's sources[] has no '${entry.source}', the source of '${documentName}'.`);
  }

  const { root, rootRefusal } = compositionRoot(official, entry.source, entry.role);
  return {
    documentName,
    role: entry.role,
    draftId: draft?.id ?? null,
    entry: entry.source,
    root,
    rootRefusal,
    files,
    active: entry.source,
  };
}

/** What the island check and the preview compose for a document authored in `source`. */
function compositionRoot(official: OfficialLoad, source: string, role: string): { root: string | null; rootRefusal: string | null } {
  // A composition source declares several worlds; none of them has a source of its own to compose by.
  if (official.manifest.documents.filter((document) => document.source === source).length > 1) {
    return { root: null, rootRefusal: `${source} declares several worlds; the studio composes a source that declares one.` };
  }
  if (role !== "fragment") return { root: source, rootRefusal: null };
  const island = islandRootSource(official);
  return island
    ? { root: island, rootRefusal: null }
    : { root: null, rootRefusal: "the official build names no composed island root for this fragment to compose under." };
}

/** Every file's current text, keyed by path: what an engine mounts. */
export function workspaceTexts(files: Readonly<Record<string, WorkspaceFile>>): Record<string, string> {
  return Object.fromEntries(Object.entries(files).map(([path, file]) => [path, file.text]));
}

/** The files a draft saves: every file whose text differs from the official build's. */
export function draftFiles(files: Readonly<Record<string, WorkspaceFile>>): Record<string, string> {
  return Object.fromEntries(Object.entries(files).filter(([, file]) => file.text !== file.officialText).map(([path, file]) => [path, file.text]));
}

/** Whether any file differs from its saved text. */
export function workspaceDirty(workspace: WorkspaceState | null): boolean {
  return !!workspace && Object.values(workspace.files).some((file) => file.text !== file.savedText);
}

/** A file's diagnostics count while they describe the file's current version. */
function current(file: WorkspaceFile | undefined, diagnostics: FileDiagnostics): boolean {
  return file !== undefined && diagnostics.version === file.version;
}

/** Every current source-tier diagnostic, file by file. */
export function currentDiagnostics(workspace: WorkspaceState): SourceDiagnostic[] {
  return Object.entries(workspace.diagnostics).flatMap(([path, diagnostics]) =>
    current(workspace.files[path], diagnostics) ? diagnostics.items : []);
}

/** The island check, when it describes the current revision. */
function currentIsland(workspace: WorkspaceState): IslandCheck | null {
  return (workspace.island && workspace.island.revision === workspace.revision) ? workspace.island : null;
}

const identity = (diagnostic: SourceDiagnostic) =>
  `${diagnostic.path}\u0000${diagnostic.line}\u0000${diagnostic.column}\u0000${diagnostic.code}\u0000${diagnostic.message}`;

/** The union of diagnostic lists, each finding once: both tiers report a source-tier warning. */
function union(...lists: readonly (readonly SourceDiagnostic[])[]): SourceDiagnostic[] {
  const seen = new Set<string>();
  return lists.flat().filter((diagnostic) => {
    const key = identity(diagnostic);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** Every problem the workspace reports at its current revision: the source tier, and the semantic tier (the open
 * source's compile and the composed world's validation). */
export function workspaceProblems(workspace: WorkspaceState): SourceDiagnostic[] {
  const island = currentIsland(workspace);
  return union(currentDiagnostics(workspace), island?.compile?.result.diagnostics ?? [], island?.composeDiagnostics ?? []);
}

/**
 * What the editor shows for one file: the union of the source tier and, when the island check describes this
 * revision, the semantic tier. `null` while the source tier has not diagnosed the file's current version, so the
 * editor keeps what it shows, mapped through the edits since.
 */
export function editorDiagnostics(workspace: WorkspaceState, path: string): SourceDiagnostic[] | null {
  const source = workspace.diagnostics[path];
  if (!source || source.version !== workspace.files[path]?.version) return null;
  const island = currentIsland(workspace);
  const semantic = (island?.compile?.path === path) ? island.compile.result.diagnostics : [];
  return union(source.items, semantic, island?.composeDiagnostics ?? []).filter((diagnostic) => diagnostic.path === path);
}

/** Whether the language server has diagnosed the open `.puck` file at its current version. A JSON file the editor
 * only shows has nothing to wait for. */
export function diagnosesCurrent(workspace: WorkspaceState): boolean {
  if (!isPuckSource(workspace.active)) return true;
  const diagnostics = workspace.diagnostics[workspace.active];
  return !!diagnostics && diagnostics.version === workspace.files[workspace.active]?.version;
}

/** Whether the current revision's source tier is diagnosed and free of errors: the point the world engine's check —
 * the semantic tier and the composition — may run. */
export function revisionClean(workspace: WorkspaceState): boolean {
  return diagnosesCurrent(workspace) && !currentDiagnostics(workspace).some((diagnostic) => diagnostic.severity === "error");
}

/** Whether the island check passed at the current revision: no semantic errors, and the root composed valid. */
export function islandClean(workspace: WorkspaceState): boolean {
  return currentIsland(workspace)?.ok ?? false;
}

/** Accepts an island check, parsing its composed document so 64-bit literals stay exact. */
export function acceptIsland(check: Omit<IslandCheck, "value">): IslandCheck {
  return { ...check, value: check.composed === null ? null : parseDocumentText(check.composed) };
}

// What each world engine was last given, so a later job writes only the files that changed since.
const mounted = new WeakMap<WorldEngine, { workspaceId: number; texts: Map<string, string> }>();

/** Brings the world engine's source workspace up to `workspace`: a full mount for a workspace it has not seen, and
 * one write per changed file after that. Only the machine writes to the world engine, so the record stays exact. */
export async function syncWorkspace(engine: WorldEngine, workspace: Pick<WorkspaceState, "id" | "files">): Promise<void> {
  const known = mounted.get(engine);
  if (!known || known.workspaceId !== workspace.id) {
    const texts = workspaceTexts(workspace.files);
    mounted.delete(engine);
    await engine.mountSources(texts);
    mounted.set(engine, { workspaceId: workspace.id, texts: new Map(Object.entries(texts)) });
    return;
  }
  for (const [path, file] of Object.entries(workspace.files)) {
    if (known.texts.get(path) === file.text) continue;
    await engine.writeSource(path, file.text);
    known.texts.set(path, file.text);
  }
}

/**
 * One island check on the world engine: sync the workspace, compile the open source in full (the semantic tier, with
 * its IR and source map), and — only when that found no errors — compose and validate the workspace's root.
 */
export async function checkWorkspace(
  engine: WorldEngine,
  workspace: Pick<WorkspaceState, "id" | "files" | "revision" | "active">,
  root: string,
): Promise<Omit<IslandCheck, "value">> {
  await syncWorkspace(engine, workspace);
  const compile = isPuckSource(workspace.active) ? { path: workspace.active, result: await engine.compileSource(workspace.active) } : null;
  if (compile?.result.diagnostics.some((diagnostic) => diagnostic.severity === "error")) {
    return { revision: workspace.revision, compile, ok: false, composed: null, composeDiagnostics: [] };
  }
  const result: SourceComposeResult = await engine.composeSource(root);
  return { revision: workspace.revision, compile, ok: result.ok, composed: result.composed, composeDiagnostics: result.diagnostics };
}

/** Writes every file the editor has changed into the language engine, so a compile there sees the buffer even when
 * the language server has not been sent the newest text yet. */
export async function writeChangedSources(engine: WorldEngine, workspace: Pick<WorkspaceState, "files">): Promise<void> {
  for (const [path, file] of Object.entries(workspace.files)) {
    if (file.version > 0) await engine.writeSource(path, file.text);
  }
}
