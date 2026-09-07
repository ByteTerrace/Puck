# Puck authoring studio

The studio opens a document from the official build and validates, edits, and
previews it against the real engine — `Puck.World.Browser` running in-browser
(WebAssembly), never a JavaScript reimplementation of engine semantics. The
authenticated storage and audit pages are separate from the studio.

## Run and check

From `src/Puck.Dashboard/src`, run `npm ci` to install the workspace
dependencies. `npm --workspace portal run dev` starts the studio's own dev
server on `http://localhost:61101`; it proxies `/official/*` to a local
`puck official serve` instance (`VITE_PUCK_OFFICIAL_PROXY_TARGET`, default
`http://localhost:61102`) so `VITE_PUCK_OFFICIAL_BASE` can stay the same
relative `/official` path it uses in production. A dry run:

```
dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish
dotnet publish src/Puck.World.Browser -c Release -r browser-wasm
src/Puck.Cli/publish/puck.exe official build --out artifacts/official --channel dev \
  --engine src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle --allow-dirty
src/Puck.Cli/publish/puck.exe official serve --tree artifacts/official --port 61102
npm --workspace portal run dev
```

`VITE_PUCK_OFFICIAL_CHANNEL` selects which channel's manifest the studio
opens (`.env` ships `dev`; production ships `stable` against
`https://puck.byteterrace.com/official`).

`npm --workspace portal run test` (`node --test tests/*.test.cjs`) runs the
regression suite — most of it against the REAL engine and official tree
fixtures, not a stand-in; a handful of tests skip themselves by name when
those local build artifacts are absent (publish `Puck.World.Browser` and run
`puck.exe official build` to produce them, as above). `npm run build`
type-checks (`tsc -b`) and builds both the host and portal, and generates the
module federation declarations. `npm --workspace portal run check:types`
regenerates the schema-derived `WorldDefinition` TypeScript and fails if the
checked-in file has drifted from the schema bundle.

## The official content model

`official/officialClient.ts`'s `loadOfficial` fetches and parses the
channel's manifest (`puck.official.v1` — build info, the world-schema
bundle, every document/composed-world/asset entry, the engine's own file
list), then verifies every object it fetches against the manifest's own
SHA-256 hash before caching it (`official/byteStore.ts`, IndexedDB-backed
with an in-memory fallback under Node). A manifest fetch that fails falls
back to a previously verified offline copy; a hash mismatch refuses by name
and caches nothing. `native/engineBoot.ts`'s `bootEngineFromOfficial` boots
the engine from those same verified files (`'inline'` mode today in the
browser and in tests — see the Worker note under limits) and then requires `engine.version()` to report
the exact `schemaVersion`/`commit` the manifest's own build names — a
mismatch disposes the engine and refuses by name rather than running a
document against an engine build the manifest did not vouch for.

## The studio machine

`machines/studioMachine.ts` (its own header states the full state shape) is
the one state machine the whole studio is bound to, through
`context/StudioContext.tsx`'s hooks — `useStudioDocument`,
`useStudioPreview`, `useStudioBoot`, `useStudioOfficial`, `useStudioEngine`,
`useStudioSelection`, `useStudioGeometry`, `useStudioCanUndo`/
`useStudioCanRedo`, `useStudioIsDirty`. `WorldStudio.tsx` is the only file
that reads `import.meta.env` (Vite's own env, unavailable and syntactically
disallowed under the Node/CommonJS test harness — see `official/officialBase.ts`'s
header): it resolves the official root/channel and mounts
`StudioContext.Provider` around `StudioShell.tsx`, which is the entire
composed UI and carries no `import.meta` of its own, so tests construct a
`StudioMachineInput` by hand and render it directly (`tests/shell.test.cjs`,
`tests/workbench.test.cjs`).

A document's `role` — world / basis / fragment / shard — is read off the
manifest when opened from there, or off the document's own content
(`document/documentRole.ts`) when pasted or imported. Validation
(`machines/studio/document.ts`'s `validateDocument`) runs `engine.parse`
alone for a standalone world document, and `engine.composeTree` against the
real island for anything that composes (a basis/shard root under its own
name, a bare fragment composed over `puck.world.json` — the only host that
answers a fragment's own island-scoped references correctly). Every 64-bit
engine value — row cell values, tick numbers, hashes-as-strings, write
old/new — is a `bigint` in TypeScript, decoded by `native/engineTypes.ts`'s
facade; `document/jsonText.ts` preserves an out-of-range integer literal in
authored JSON as an exact `bigint` leaf rather than rounding it through
`JSON.parse`/`JSON.stringify`.

## Sections, JSON, Spatial, State, Preview, Console

The tool-tab strip (`StudioShell.tsx`) is bound entirely to the machine —
no props flow down from a page-level state.

- **Sections** (`authoring/AuthoringWorkspace.tsx`) renders the schema
  bundle as a form: `forms/SectionExplorer` lists every root section in
  schema order, `forms/SectionForm` (`forms/SchemaNode` underneath) edits
  the selected one, and the root's reserved `$`/`_`-prefixed extension keys
  get their own always-present entry (`forms/ExtensionsEditor`). Every edit
  becomes an `EDIT_DOCUMENT` event — one document revision, undoable.
- **JSON** (`authoring/JsonEditor.tsx`) is a CodeMirror 6 editor over the
  document's raw text. Typing dispatches `SET_TEXT_DRAFT` (a live, not yet
  revisioned, edit of `document.text`); **Apply** dispatches `APPLY_TEXT`
  (intake-checks, parses, becomes a revision); **Discard** resets the draft
  back to the last applied revision's own serialized text. A diagnostic is
  best-effort mapped to a line by searching the text for its path's leading
  quoted key segments (`lineForDiagnosticPath`) and highlighted there; one
  whose path cannot be textually anchored (an array index, mainly) lists
  below the editor instead.
- **Spatial** (`WorldWorkbench.tsx`) is the topology explorer, viewport
  (`UniversalTopologyView` or, for a volumetric topology, the lazy-loaded
  `SpatialTopology3D`), and cell inspector — geometry always comes from
  `useStudioGeometry()`, the engine's own `cells()` answer, never a
  TypeScript ordinal formula.
- **State** (`StateMatrixView.tsx`) lists every authored `state.world[]`
  row; while a preview session is running, a non-keyed row's live value is
  editable in place through `PREVIEW_WRITE`.
- **Preview** compiles the document (`PREVIEW_START`), ticks it
  (`PREVIEW_TICK`), steps and jumps through its recorded snapshots
  (`PREVIEW_UNDO`/`PREVIEW_REDO`/`JUMP_TO_TICK` — the engine has no
  snapshot-restore of its own, so a history move recompiles fresh and
  replays the write/tick script up to that point), and shows the latest
  snapshot's `JudgeTrace` (`RuleTraceView.tsx`: rules visited by mode,
  writes as row/key/old/new, refusals). `PREVIEW_START` is refused by name
  — not silently withheld — while the document has unresolved diagnostics.
- **Console** (`PuckReplConsole.tsx`) evaluates an expression through
  `engine.evaluate(handle, expression, kind, tick)` against the live
  preview handle, `Int`/`Fixed` selectable; refused by name when no preview
  session is running.

Local drafts (`drafts/DraftsPanel.tsx`, `document/localDrafts.ts`) are a
browser-storage-backed document library independent of the official tree —
up to ten revisions per draft, one atomic write. They are this studio's only
offline library; nothing here uploads, publishes, or generates a share link.

## Limits that remain

- The engine is hosted inline (on the page's main thread). With the AOT
  AppBundle, `dotnet.js` booted inside a module Worker loads every assembly,
  logs `onRuntimeInitialized`, and then never resolves `create()` (verified
  in Chromium on 2026-09-07 with diagnostic tracing; the same boot on the
  main thread resolves in about 250 ms warm). Engine calls therefore block
  the UI for their duration — about two seconds to compose or compile the
  island. `WorldStudio.tsx` is the one switch; the Worker path
  (`engine.worker.ts`, `workerBoot.ts`) stays built and Node-tested.
- One runtime per page: `dotnet.js` refuses a second `create()` in a realm,
  so `workerBoot.ts` memoizes the realm's boot by engine set (React's
  StrictMode double-mount and HMR remounts join it) and refuses a different
  engine set by name until a reload.
- Wasm engine payload: the AOT `Puck.World.Browser` AppBundle is roughly
  40 MB; a first boot fetches and hash-verifies it in full (subsequent boots
  serve from the byte store).
- A real fragment's `PREVIEW_START`/`RESET_WORLD`/`JUMP_TO_TICK` compiles
  the WHOLE composed island (hundreds of KB of JSON), not just the open
  fragment, and can take several seconds even warm — `PREVIEW_START` stays
  an explicit, guarded action rather than something that runs on every
  keystroke.
- The JSON tab's diagnostic-to-line mapping is a textual heuristic over the
  pretty-printed document (leading dotted-key segments only, array indices
  skipped) — it can point at the wrong occurrence of a repeated key name,
  and never claims to be a real JSON-path evaluator.
- `forms/SectionForm` exposes no deeper per-field focus than the root
  section itself — a diagnostic's "reveal" affordance selects the Sections
  tab and that section, not the exact field.
- The old studio's arcade demo input adapters, scenario/Monte Carlo batch
  rollout, ray prober, rule dependency graph, and predicate truth tree are
  gone with the JS-only preview machine they were built over; nothing here
  replaces them yet.

## Layout

Under `src/portal/src`: `machines/studioMachine.ts` (+ `machines/studio/*`)
is the one state machine; `context/StudioContext.tsx` is its React seam;
`native/` is the engine facade (inline and Worker hosting); `official/` is
the official content client; `document/` owns JSON path/text addressing,
document-role classification, intake, and local drafts; `forms/` is the
schema-driven form layer; `authoring/` (`documentTools.ts`,
`presentation.ts`, `jsonReference.ts`, `sceneProjection.ts`) is
topology/row lookup and render-space projection with no ordinal math of its
own — geometry always comes from the engine. `components/world` owns the
composed UI: `WorldStudio.tsx` (the page entry — the one file with
`import.meta.env`), `StudioShell.tsx` (the composed shell everything else
mounts under), `WorldStudioHeader.tsx`/`WorldStudioAlerts.tsx` (build/
document status, diagnostics), the six tab components above, and
`drafts/DraftsPanel.tsx`. Tests live in `src/portal/tests`, run by Node's
own test runner.
