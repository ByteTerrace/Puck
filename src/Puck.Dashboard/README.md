# Puck.Dashboard

Puck.Dashboard hosts World Studio and the authenticated web portal. The studio
opens a document's `.puck` source from the official build, edits it with the
engine's own language server, and checks, composes, and previews it with the
real engine—`Puck.World.Browser` running in-browser (WebAssembly), never a
JavaScript reimplementation of the language or of engine semantics. The
authenticated storage and audit pages are separate from the studio.

The [web workspace](src/README.md) maps the host, portal, and shared packages.

The website also contains a Documentation page at `/docs`.
`docs.byteterrace.com` opens it directly; `puck.byteterrace.com` opens World Studio.
The page embeds the documentation overview and links to the generated API reference.
Azure CI builds both into the same website release; see the
[deployment contract](../../docs/development/ci.md#azure-production-deployment).

## Run and check

From `src/Puck.Dashboard/src`, run `npm ci` to install the workspace
dependencies. `npm --workspace portal run dev` starts the studio's own dev
server on `http://localhost:61101`; it proxies `/official/*` to a local
`puck official serve` instance (`VITE_PUCK_OFFICIAL_PROXY_TARGET`, default
`http://localhost:61102`) so `VITE_PUCK_OFFICIAL_BASE` can stay the same
relative `/official` path it uses in production.

In Bash on Windows, run these commands from the repository root to prepare
the local content tree and start its server. Leave the server running:

```bash
dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish
dotnet publish src/Puck.World.Browser -c Release
src/Puck.Cli/publish/puck.exe official build --tree artifacts/official --channel dev \
  --engine src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle --allow-dirty
src/Puck.Cli/publish/puck.exe official serve --tree artifacts/official --port 61102
```

In a second terminal at `src/Puck.Dashboard/src`, start the portal:

```bash
npm --workspace portal run dev
```

To work inside the signed-in dashboard as well, start the host in a third
terminal. It runs on `http://localhost:61100`, loads the portal from its dev
server, and proxies `/official/*` the same way, so the studio boots under the
host too:

```bash
npm --workspace host run dev
```

Served on separate ports, the host and portal are different origins. The
engine worker handles that by starting from a same-origin module blob when
its script is on another origin; in production the portal lives under the
host's own `/portal` path, and the worker starts directly. `npm --workspace
portal run preview` and `npm --workspace host run preview` serve the
production builds with the same `/official` proxy. A production build
targets the `stable` channel, so a local preview boots only against a tree
built for that channel.

`VITE_PUCK_OFFICIAL_CHANNEL` selects which channel's manifest the studio
opens (`.env.development` ships `dev`; production ships `stable` against
`https://puck.byteterrace.com/official`).

`npm --workspace portal run test` (`node --test tests/*.test.cjs`) runs the
regression suite. The studio's own tests run over a small `.puck` workspace of
their own (`src/portal/tests/fixtures/workspace`), served as an in-memory
official build with every object hash-verified; they drive the real machine,
streams, and components with fake engines that count every engine call. The
engine tests run the published engine itself: each skips by name when there is
no local AppBundle (publish `Puck.World.Browser`), when the AppBundle predates
the source exports, or, for a test that needs the game's compiled worlds, when
no official tree has been built (`puck.exe official build`, as above). A test
that composes the game's island mounts the tree's `sources[]` the way the
studio does. No test
reads the game's worlds directory, which holds sources, not documents. Every
test file loads the real sources through `src/portal/tests/support/register.cjs`,
which transpiles TypeScript on require and resolves a CSS module's class names
to their own keys, so markup assertions read the names the source uses.

`npm run build` type-checks (`tsc -b`) and builds both the host and portal.
Each application's `tsconfig.json` references an app project (the browser
sources) and a node project (`vite.config.ts` and the shared
`src/build/strictBuild.ts`), and both extend `tsconfig.base.json`. The production
build treats warnings as errors, as the .NET build does: any Rolldown or Vite
warning fails it unless the application's Vite configuration names that
warning as accepted, with its reason. The portal accepts exactly one, React
Three Fiber's probe for `React.act`, which production React leaves undefined
on purpose. The one Rolldown check turned off is its plugin-timing advisory,
because it fires only past a wall-clock threshold and so cannot fail a build
deterministically.

The portal types a world document with
`portal/src/document/worldDefinition.generated.ts`. Nothing in this workspace
produces that file: `puck schema` writes it from the world document's JSON
Schema at the same time as the schema files, and `puck schema --check` fails
when it no longer matches, as it does for the schema itself. After changing
the document model, run `puck schema` and commit the regenerated file; never
edit it by hand.

In CI, the engine-boot tests serve their official tree with the producer's CLI
artifact installed by `setup-puck`. Local runs use a published CLI, the
repository's installed CLI, or `puck` on `PATH`; these checks never compile
another CLI, so the CLI they find must be built from this checkout's sources.

## Versions

The workspace manifest, `src/package.json`, holds the dashboard's one
version; the host and portal ship together under it, and the host reports it
to MSAL's telemetry. Every direct dependency declares a caret range whose
floor is the version the lockfile installs, and a package both applications
use declares the same range in each. The portal declares `apache-arrow` at the
major version DuckDB-Wasm depends on, so the results table reads the same Arrow
classes the engine produces. One package is pinned exactly because its exact
bytes matter: `@duckdb/duckdb-wasm`, whose worker and WebAssembly files the
portal bundles by URL. It is a Puck build, installed from a release tarball
rather than npm (its extensions are pinned separately; see
[Querying storage with DuckDB](#querying-storage-with-duckdb)). npm installs a
URL dependency only because `src/.npmrc` sets `allow-remote=root`, which
admits one a workspace `package.json` names directly and never one that
arrives transitively.
The root's one override pins `adm-zip` under the federation plugin to
its patched release. Platform-specific packages, such as Rolldown's native
bindings, are left to the packages that choose them, so one lockfile installs
on Windows and on CI's Linux runners alike.

## Theme and styling

The portal's look comes from Puck's brand, not from Mantine's defaults.
`portal/src/theme/palette.ts` turns the three hue families in
[`branding/tokens.css`](../../branding/tokens.css) into Mantine color ramps:
the violet-neutral ground replaces Mantine's `gray` and `dark`, coral is the
primary color, and jade is its complement. Because the ground ramps stand in
for Mantine's own neutrals, every Mantine surface follows the brand in both
color schemes without overriding component styles. `theme/theme.ts` holds the
theme itself and a CSS variables resolver that adds an elevation scale:
`--puck-surface-canvas` for the page, `--puck-surface-raised` for bars,
panels, and dialogs, `--puck-surface-sunken` for wells such as code and
viewports, and `--puck-line` for hairline borders.

Type follows three roles. Headings and long-form prose use Lora, the brand's
`--font-body`; interface text uses the system sans; labels, data, and code
use JetBrains Mono. `theme/global.css` imports the canonical tokens and the
documentation site's self-hosted font files, so the dashboard loads no fonts
from a third party. The branding manifest checks that wiring (`puck branding
--check`).

Mantine's stylesheet loads into the `mantine` cascade layer, and each
component keeps its own rules in a CSS module beside it, written with
Mantine's PostCSS preset (`light-dark()`, `rem()`, and the hover and
breakpoint mixins). A module rule therefore overrides Mantine without
`!important`. `portal/src/ui` holds the small shared pieces: the `Kicker`
overline, empty, loading, and error states, the explorer entry, and the
CodeMirror theme every editor uses. The theme also colors `.puck` source: the
language server's semantic tokens arrive as `cm-puck-<type>` classes, styled in
the same roles as the syntax colors, so both color schemes follow the brand.
React Compiler runs over both applications' sources, so components do not add
`useMemo` or `useCallback` for speed.

## Federation

The host and portal are one application in two builds, joined by module
federation. The portal exposes one module, `portal/portal-app`, whose
contract is the `PortalModule` type in `shared/interfaces.tsx`: its default
export renders the portal and takes the host's context. The portal's
`App.tsx` is checked against that type, and the host loads it through it,
so a change to either side that breaks the contract fails the type check.

Both builds declare the same share, `src/build/federationShare.ts`: `react`,
`react-dom`, `xstate`, and `rxjs`, as strict-version singletons. The host
provides each one and the portal uses the host's instance, so a live object
the host hands across (the account setup actor) is created and read by the
same XState. The portal served on its own falls back to its bundled copies.
Every runtime dependency both applications declare belongs in the share; the
[federation test](src/portal/tests/federation.test.cjs) holds that rule, and
`strictVersion` turns any version skew into a load error that names the
package. Everything else stays private to the application that uses it. The host registers the portal at
runtime (`host/src/remote.tsx`) by the module-federation manifest the portal's build emits, so
its location can follow the page: beneath the host at `/portal` in
production, the portal's dev server in development, or a `portalBaseUrl`
query parameter. The host starts that download before sign-in finishes, and
a portal that fails to load shows an error with a retry rather than a blank
page.

## Streams

RxJS carries the dashboard's events over time: work where a newer request
supersedes an older one, several sources feed one outcome, or a protocol
polls. Each stream lives in a plain module with its sources passed in, so
its timing rules are tested in Node without a browser or a server; a
component subscribes in an effect, or reads a current value through
`src/portal/src/streams/useObservableValue.ts`.

- `native/workerClient.ts`: the engine worker's replies, matched to calls by
  id, and one terminal close that settles every call.
- `native/languagePump.ts`: the language server's pump. Client messages go
  first, a burst of full-text changes to one file coalesces to the newest,
  and pending diagnostic work runs one unit per step only while nothing else
  is queued, stopping when the server says nothing is pending. It runs on the
  page's side over the engine's `lsp` and `lspIdle` calls, the same way for
  both hosts.
- `native/lspTransport.ts`: `@codemirror/lsp-client`'s transport over that
  channel. It adds no delay: lsp-client syncs after 500 ms of quiet and before
  every request.
- `machines/studio/sourceDiagnostics.ts`: the server's `publishDiagnostics`
  as the newest source-tier diagnostics per file and version; an older
  version never replaces a newer one.
- `machines/studio/islandCheck.ts`: the world engine's check of each clean
  revision (the full compile and the composition). One runs at a time, the
  newest ready revision waits and replaces an older waiter, and a result for a
  superseded revision is dropped.
- `components/data/storageStreams.ts`: Cloud Storage's listings, reloads,
  and queries, where the latest wins and a superseded query is cancelled in
  the engine, plus share-link announcements. The account's endpoint is resolved
  once and kept; a failed resolution is retried by the next reload, which the
  file list's error offers.
- `components/auditJournal.ts`: the audit trail's journal batches,
  downloaded a few at a time.
- `shell/sectionStore.ts`: the portal's current section, following browser
  history through the studio's unsaved-edits guard.
- `shared/onboarding.ts`: the host's self-onboarding protocol, which the
  account setup machine runs.

One-shot requests stay promises.

## State machines

XState owns the dashboard's lifecycles: behavior where what may happen next
depends on where things stand, so an event that makes no sense in the current
state is refused by the machine rather than guarded in a component. Each
machine does its outside work (the engine, the network, storage) in invoked
actors, so a failure is a transition and the state says what is in flight;
its `assign` actions stay pure. Components ask a machine questions through
selectors (`snapshot.can(...)` for "would this event be taken now", tags for
"is something running") rather than naming its states. A button's enabled
state is therefore the machine's own answer: an event the machine would not
take (a draft save with no document open, a preview step past the recorded
snapshots) is refused by a guard, never only by a disabled control.

- `portal/src/machines/studioMachine.ts`: the studio, described below. It owns
  both engines' lifetimes, the source workspace, draft saves and deletes, and
  the preview; the diagnostics and island-check streams run as invoked
  `fromEventObservable` actors while a workspace is open and feed it events.
- `shared/onboarding.ts`'s `onboardingMachine`: the signed-in account's
  setup, one actor per page created by the host's `main.tsx`. It waits for
  sign-in, runs the RxJS protocol as an invoked observable, and ends ready
  or failed; the portal's account controls read its `busy` tag, offer a retry
  when `RETRY` would be taken, and send it.

The two libraries meet at that seam: a machine decides when something runs,
and a stream describes how it unfolds over time.

## Querying storage with DuckDB

The Cloud Storage page runs SQL over the user's files with DuckDB, compiled to
WebAssembly and running in a worker in the browser
(`components/data/duckDb.ts`). Nothing about a query leaves the page except
the file reads themselves.

**The engine is a Puck build of DuckDB-Wasm.** Upstream DuckDB-Wasm's HTTP
client ignores an HTTP secret's `BEARER_TOKEN`, which is how the engine reads
storage with the user's token. Until the fix ships upstream, the portal
installs `@duckdb/duckdb-wasm` `1.33.1-dev64.0.puck.1` from the
[release on Kittoes0124/duckdb-wasm](https://github.com/Kittoes0124/duckdb-wasm/releases/tag/v1.33.1-dev64.0.puck.1):
npm's `1.33.1-dev64.0` (DuckDB v1.5.5) rebuilt by the fork's CI with that one
patch. Apart from the patched WebAssembly, the generated glue around it, and
the version, the package is byte-identical to npm's, and the lockfile pins its
hash. When upstream releases the fix, go back to npm's package and delete the
release.

**The engine and its extensions ship with the portal.** The package holds
only DuckDB's core; Parquet, JSON, httpfs, and ICU are extensions DuckDB
normally downloads from its own repository the first time a query needs one.
The portal serves them itself instead. `portal/duckdb-extensions.json` pins
each extension file by SHA-256 for the DuckDB version the engine reports, and
the build (`src/build/duckdbExtensions.ts`) downloads each file once, refuses
any whose hash differs, and writes it under `duckdb-extensions/` in the portal
output. When the engine starts, it points DuckDB's extension repository at
that directory, so a query never depends on a third party. An extension the
portal does not serve fails to load; community extensions are refused; SQL
that names a repository itself (`INSTALL spatial FROM core`) still reaches it.
Upgrading `@duckdb/duckdb-wasm` means re-pinning the manifest for the new
DuckDB version: extensions load only into the version they were built for,
and the [extension test](src/portal/tests/duckdbExtensions.test.cjs) fails until the two agree.

**How reads are authorized.** Files are read over HTTP by DuckDB's httpfs
extension, which fetches only the byte ranges a query needs; a Parquet query
over one column reads that column's chunks and the file footer, not the whole
file. Before a query runs, the portal creates one HTTP secret for each
ByteTerrace storage account the query names
(`components/data/storageSecrets.ts`), holding the user's current storage
token as its `BEARER_TOKEN` and scoped to that account, so the token goes to
those accounts and nowhere else. `duckdb_secrets()` shows the token redacted.
A query that names no ByteTerrace storage gets no secret, and neither does
one run while nobody is signed in; its reads go out without a token, which a
public file allows. The secrets also send `x-ms-version`, which Azure Storage
requires on requests authorized this way. The
[secret test](src/portal/tests/storageSecrets.test.cjs) checks which hosts get
a secret and how the token is quoted.

**Every read is fresh.** Storage responses carry no `Cache-Control`, and
browsers cache such responses on their own, so a file overwritten a moment
ago could be read from the old copy. Every storage request carries
`Cache-Control: no-cache`, so the browser asks storage rather than answering
from its cache.

**What the table shows.** Results stream from the engine and stop at 1,000
rows, and the table says when a query returned more. Each value appears
exactly as DuckDB itself would print it (`CAST(value AS VARCHAR)`): decimals
with their scale, timestamps to the microsecond, intervals, lists, structs,
and maps, all read from the result's typed columns
(`components/data/queryResult.ts`). The [result test](src/portal/tests/queryResult.test.cjs) checks this
against DuckDB's own printer. `TIMESTAMPTZ` values print in UTC, and the
session runs in UTC so SQL that formats a time agrees with the table.
Starting a new run cancels the previous one in the engine, not only on the
page.

**Share links are untrusted input.** A share link arrives in a URL fragment
anyone can write. The page opens, queries, or downloads it only when it points
at a ByteTerrace storage account, and a starter query quotes the link as one
SQL string, so a crafted link cannot end the string and run SQL of its own.

## The official content model

`official/officialClient.ts`'s `loadOfficial` fetches and parses the
channel's manifest (`puck.official.manifest.v1`—build info, the world-schema
bundle, the authoring workspace's `sources[]`, every document, composed-world,
and asset entry, and the engine's own file list), then verifies every object
it fetches against the manifest's own SHA-256 hash before caching it
(`official/byteStore.ts`, Cache Storage-backed with an in-memory fallback
under Node). A stored object is checked against its hash again every time it
is read back (`readThroughVerified`, which the engine boot uses too): a
tampered copy is fetched again and replaced, and refused by name when it cannot
be. A manifest fetch that fails falls back to a previously verified offline
copy; a hash mismatch refuses by name and caches nothing; a manifest with no
`sources[]` is refused by name, because the studio edits a document through
its sources. So is a manifest naming any object hash not spelled
`sha256/<64 lowercase hex>`, the engine's own pin grammar: the refusal names the
field before any byte is fetched.

`sources[]` is the authoring workspace: every `.puck` file under the worlds
directory, its sidecar locks, and every `.world.json` document that has no
`.puck` source, each named by its worlds-relative path
(`games/klondike.puck`). A `documents[]` entry is named by its document name
(`games/klondike`, `puck`), which the studio treats as opaque, and names the
`sources[]` file that authors it; `composed[]` names the island root. The
studio finds the island root through `composed[]`, never by a file name.

`native/engineBoot.ts`'s `bootEngineFromOfficial` boots the engine from those
same verified files (a dedicated Worker in the browser, inline in Node tests).
The engine's `dotnet.native.wasm` (about 45 MB) is compiled once per session.
The first engine hands dotnet.js the wasm's content-addressed object URL,
fetched with the manifest's hash as its Subresource Integrity value, so the
browser verifies the bytes and both its HTTP cache and its wasm code cache can
apply. The studio hashes the same bytes with the official client's own
verification as they stream, because a fetch may ignore `integrity`: the
compile answers only once they match, and only matching bytes are copied into
the byte store. When the network fails, the stored copy is verified the same
way before it answers. Every mismatch, and a failed fetch with nothing stored,
is refused by name. That engine
hands back the module dotnet.js compiled, and the second engine boots from it:
its wasm download is an empty stand-in that compiles to the shared module, so
it instantiates and compiles nothing. A caller-supplied `instantiateWasm` would
bypass dotnet.js' own binding of its runtime imports, so the module travels
through dotnet.js' compile instead (`native/workerBoot.ts`). Each worker counts
its WebAssembly calls (`native/wasmCounts.ts`); a development build exposes the
studio as `window.__puckStudio`, so `context.languageEngine.wasmCounts()` and
`context.worldEngine.wasmCounts()` show one compile and no compile. After boot,
`bootEngineFromOfficial` requires `engine.version()` to report the manifest's
`build.worldSchema` and the commit its world schema bundle was generated at
(the bundle's `x-puck.commit`, read by `generatorCommit`). A mismatch disposes
the engine and refuses by name rather than running a source against an engine
build the manifest did not vouch for. The manifest's `build.commit` names the
worlds tree it was built from, not an engine build, so it takes no part in that
check. The studio's build line shows it, abbreviated, marked `+ local edits`
when `build.dirty` is set, and as `worlds tree outside git` when it is `none`. `native/engineTypes.ts` is the one place
every engine answer is decoded, source and language calls included; a call an
older engine build lacks throws `EngineCapabilityMissing` by name.

## The studio machine

`machines/studioMachine.ts` is the one state machine the whole studio is bound
to, through `context/StudioContext.tsx`'s hooks (`useStudioWorkspace`,
`useStudioProblems`, `useStudioChecking`, `useStudioCompiled`,
`useStudioComposition`, `useStudioPreview`, and the questions
`useStudioCanSaveDraft`, `useStudioCanStartPreview`, and their neighbours).
`WorldStudio.tsx` is the only file that reads `import.meta.env` (Vite's own
env, unavailable and syntactically disallowed under the Node/CommonJS test
harness—see `official/officialBase.ts`'s header): it resolves the official
root/channel and mounts `StudioContext.Provider` around `StudioShell.tsx`,
which is the entire composed UI and carries no `import.meta` of its own, so
tests construct a `StudioMachineInput` by hand and render it directly
([shell tests](src/portal/tests/shell.test.cjs) and
[workbench tests](src/portal/tests/workbench.test.cjs)).

**Two engines.** Both boot from the same verified official bytes, and the
second from the first one's compiled module. The *language engine* boots with
the studio and hosts the language server. The *world engine* stays dormant
until the first revision is ready for an island check, then boots and hosts the
check, the preview, the console, and geometry. In the browser each is its own
Worker, so a long check never delays completion; the machine owns both
lifetimes and disposes them when the studio stops. Inline hosting keeps one
.NET runtime per JS realm, so under a single Node test the two share it.
An engine that refuses to boot never takes the studio with it. Without the
language engine the workspace still opens, edits, and saves drafts, with no
diagnostics; without the world engine the language engine still diagnoses.
Either way the preview cannot start, the preview area names that engine's
reason, and the refused engine is not booted again. Only an official build that does not load or
verify refuses the boot itself, and then nothing opens.

**The workspace.** Opening an official document reads every `sources[]` file
(hash-verified, from the byte store after the first read), mounts them into
the language engine, and opens the document's own source; the world engine
receives the workspace before its first job and, after that, only the files
that changed. Opening a draft does the same with the draft's files laid over
the official build. A newer open replaces one still reading; a failed open
keeps the current workspace and says why. A document with no `.puck` source
opens read-only.

**The edit loop.** CodeMirror owns each file's buffer and undo history. Every
change reaches the machine as `SOURCE_CHANGED {path, version, text}`, which it
records with pure `assign`; the version is the machine's count of changes to
that file, and the language server sees the same numbers. lsp-client syncs
the buffer after 500 ms of quiet and before every request; the server writes
the text through into its workspace and diagnoses only when idle, at the
latest version. Two streams run as invoked actors while a workspace is open:
the server's source-tier diagnostics become `DIAGNOSTICS` events, and the
island check becomes `ISLAND_CHECKED`. The `checking` tag answers "source-tier
diagnostics are pending for the open file's current version".

**The island check.** Once a revision's source tier is diagnosed with no
errors, the world engine compiles the open source in full—the semantic tier,
with its IR and source map—and, when that finds no errors, composes and
validates the workspace's root: the manifest's composed island root for a fragment, the
document's own source otherwise. One check runs at a time; a result for a
superseded revision is dropped. The check's compile is also the Compiled and
Sections views' IR at that revision, so showing them costs nothing more. The
composed world's findings join the semantic tier: an error blocks the preview,
and an `information` finding (a check the browser defers because it has no
machine catalog) shows as information and never does. The engine attributes a
composed-world finding to the root at line 0 even when an imported file caused
it, so the studio shows it as a whole-document problem of the root. The
newest clean composition feeds the Spatial and State views, and it outlives a
failed check so an error in progress does not empty them.

**Preview.** `PREVIEW_START` is taken only when the current revision is clean
at both tiers and its root composed. While the official build or an engine the
preview needs is known to have refused, the start is not taken at all, so
`can` answers false and the button is disabled from the moment the refusal is
known; the reason shows where a refused start does. Otherwise a start is
refused with the reason (still checking, errors, the check not finished). It
compiles the check's composition on the world engine—no second
composition—and a newer source change stops it. Every 64-bit engine value is a
`bigint` in TypeScript, and `document/jsonText.ts` keeps an out-of-range
integer literal in compiled or composed JSON exact.

**Drafts.** A draft is the set of files whose text differs from the official
build, over the document it opens on, in the browser-storage library
(`document/localDrafts.ts`): up to ten revisions per draft, one atomic write,
at most 2 MB. Dirty means a file differs from its saved text, which the
section store's unsaved-edits guard checks before leaving the studio.

## Studio views

The tool-tab strip (`StudioShell.tsx`) is bound entirely to the machine—no
props flow down from a page-level state. Every view keeps a fixed frame, so a
diagnostic arriving, a file opening, or a check finishing moves nothing
around it.

- **Source** (`authoring/SourceEditor.tsx`) is the workspace's files, the open
  file in CodeMirror 6, and its outline. A `.puck` file edits through
  `@codemirror/lsp-client` against the language engine: completion, hover,
  formatting (Shift-Alt-F), the outline from `textDocument/documentSymbol`,
  and highlighting from the server's own semantic tokens
  (`authoring/semanticTokens.ts`), requested after each sync. The studio has
  no grammar or tokenizer of its own for `.puck`. The editor shows both
  diagnostic tiers merged per file and version, which it sets itself rather
  than letting the server's publication alone replace them. A file with no
  `.puck` source opens read-only with a notice that it has no source yet. The
  panel stays mounted while another tab is shown, so the open file keeps its
  diagnostics and check running.
- **Compiled** (`authoring/CompiledViews.tsx`) is the open source's compiled
  document, read-only. Putting the cursor on a value names its JSON pointer
  and the source span the source map resolves it to (the value's own, or its
  nearest mapped ancestor's); **Go to source** shows it in the editor. It is
  fetched on demand when the tab is shown and never per keystroke.
- **Sections** is a read-only navigator over the same IR: the schema's root
  sections in declaration order, each shown with the same way back to source.
- **Spatial** (`WorldWorkbench.tsx`) is the topology explorer, viewport
  (`UniversalTopologyView` or, for a volumetric topology, the lazy-loaded
  `SpatialTopology3D`), and cell inspector over the newest clean
  composition—geometry always comes from the world engine's own `cells()`
  answer, never a TypeScript ordinal formula. It writes to the running preview,
  never to the source.
- **State** (`StateMatrixView.tsx`) lists the composition's `state.world[]`
  rows; while a preview session is running, a non-keyed row carrying a numeric
  cell value is editable in place through `PREVIEW_WRITE`.
- **Preview** compiles (`PREVIEW_START`), ticks (`PREVIEW_TICK`), steps and
  jumps through its recorded snapshots
  (`PREVIEW_UNDO`/`PREVIEW_REDO`/`JUMP_TO_TICK`—the engine has no
  snapshot-restore of its own, so a history move recompiles fresh and replays
  the write/tick script through that tick, verifies its state hash, and
  replaces abandoned future history when a new write or tick branches), and
  shows the latest snapshot's `JudgeTrace` (`RuleTraceView.tsx`). A rule, and a
  refusal naming it, links to the rule's own line when the open file's
  compile names that rule; the compiler maps every rule at `/rules/<i>`.
- **Console** (`PuckReplConsole.tsx`) evaluates an expression through the
  world engine's `evaluate(handle, expression, kind, tick)` against the live
  preview handle; refused by name when no preview session is running.

Every problem—both tiers, the composed world's findings among them—is listed under the
masthead with a **Reveal** that shows its span in the editor. Local drafts
(`drafts/DraftsPanel.tsx`) list each draft's changed-file count; nothing here
uploads, publishes, or generates a share link. An unreadable local library
reports its error without crashing the editor or overwriting stored data.

## Limitations

- Wasm engine payload: the AOT `Puck.World.Browser` AppBundle is roughly
  50 MB, most of it `dotnet.native.wasm`. A first boot downloads and verifies
  it in full; later boots read the smaller files from the byte store and the
  wasm through the browser's HTTP cache. Whether the wasm code cache answers a
  later page load depends on the browser and on the object server's caching
  headers; the studio's own tests prove the one-compile-per-session count, not
  a code-cache hit.
- The semantic tier and the composition of a large island take seconds on
  the world engine; `PREVIEW_START` stays an explicit, guarded action rather
  than something that runs on every keystroke.
- A composed-world finding caused by an imported file names the root at line
  0, not the file or line that caused it. The language server likewise
  publishes a module's or basis's diagnostic under the root's URI with the
  other file's line numbers, so such a finding can sit at an unrelated line of
  the root.
- A source that declares several worlds is opened as one file; the studio
  checks and previews a source that declares one.
- Cell painting and the value-appearance editor wrote JSON and are retired;
  the workbench writes only to the running preview. Authoring a cell or an
  appearance is a source edit.
- A Cloud Storage query shows at most 1,000 rows; aggregate or add a `LIMIT`
  to see the rest.
- The production build downloads the pinned DuckDB extensions from
  `extensions.duckdb.org` the first time it runs on a machine (then from
  Vite's cache), so a clean build needs that host.

## Layout

Under `src/portal/src`: `App.tsx` is the federated entry, holding only the
providers; `shell/` is the portal frame (the brand bar, section navigation,
the section store that keeps the URL and the studio's unsaved-edits guard in
step, and the lazy section outlet); `theme/` and `ui/` are described under
[Theme and styling](#theme-and-styling). `components/data/` splits the Cloud
Storage page into its storage calls, the bundled DuckDB query engine and its
result formatter, hooks, and panels. `machines/studioMachine.ts` is the one
state machine, and `machines/studio/` holds its types, actors, selectors, the
workspace helpers (`workspace.ts`), the two streams it invokes, and the
preview and geometry calls; `context/StudioContext.tsx` is its React seam.
`native/` is the engine facade (inline and Worker hosting), the language pump,
and the lsp-client transport; `official/` is the official content client;
`document/` owns source paths and URIs, JSON pointers, exact JSON text, the
draft size cap, and local drafts; `authoring/` (`documentTools.ts`,
`presentation.ts`, `sceneProjection.ts`, `sourceMap.ts`) is topology/row
lookup, render-space projection with no ordinal math of its own, and reading
the compiler's source map. `components/world` owns the composed UI:
`WorldStudio.tsx` (the page entry—the one file with `import.meta.env`),
`StudioShell.tsx` (the composed shell everything else mounts under),
`WorldStudioHeader.tsx`/`WorldStudioAlerts.tsx` (build and workspace status,
problems), `authoring/` (the source editor, its language client, semantic
tokens, and the compiled views), the tab components above, and
`drafts/DraftsPanel.tsx`. Tests live in `src/portal/tests` with their own
fixture workspace, run by Node's own test runner;
`src/portal/tests/cssModules.test.cjs` checks that every CSS module class a
component reads exists, which TypeScript cannot. Outside both applications,
`src/build/` holds what their Vite configurations share: the federation share,
the strict warning policy, the local `/official` proxy, and the pinned DuckDB
extensions.

## Documentation

📚 [Engine manual](../../docs/README.md) · 🛠️ [Development](../../docs/development/README.md)
