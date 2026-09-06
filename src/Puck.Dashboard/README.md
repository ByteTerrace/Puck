# Puck authoring studio

The studio edits world documents and previews a limited set of integer rules in
the browser. It works without signing in. The authenticated storage and audit
pages are separate from the studio's local document library.

## Run and check

From `src/Puck.Dashboard/src`, run `npm ci` to install the workspace dependencies,
then `npm --workspace portal run dev`. Open `http://localhost:61101` for the
standalone studio. The host runs separately with `npm --workspace host run dev`
on port 61100.

Run `npm --workspace portal test` for the offline engine, document intake, state
machine, and local storage regression tests. Run `npm run build` to type-check and
build both the host and portal. No native engine service is needed for these
checks. The build also generates the module federation declarations.

## Author a document

Start with an example or import JSON into the editor. Typing changes a draft;
**Apply document** checks its structure and starts a fresh preview. A rejected
document leaves the applied world and preview history intact. Drafts survive
switching tool tabs. Leaving with unsaved changes asks before discarding them.

The designers, cell painting, appearance bindings, and applied JSON edits all use
the same document transaction path. Each edit resets preview history. **Undo
edit** and **Redo edit** travel through document revisions; Ctrl/Cmd+Z and
Ctrl/Cmd+Shift+Z do the same outside text fields. A bulk paint is one revision.
Invalid edits leave both histories intact. Saving establishes the clean revision,
so undoing away from it marks the document unsaved. Apply or export an outstanding
JSON draft before opening a designer or painting. JSON is also the
editing surface for constructs the designers do not support. HUD settings are
stored as a string in `metadata.custom.puckStudioHud`; they are authoring data,
not a native HUD binding.

**Save locally** saves the applied document in this browser's local storage. It
keeps up to ten document revisions in the same atomic write. Storage failures
remain visible and do not mark a document saved. Local IDs take precedence over
example IDs when loading. The library can open a saved revision for review before
saving again. These saves do not upload, publish, or generate share links.

**Export JSON** downloads the current editor text, including unapplied edits. Use
it for a portable copy or before clearing browser data. Preview register changes
and played moves are temporary: they do not alter the authored document or its
saved revisions.

## Select and paint

The explorer chooses a topology and one of its cell state domains. The viewport
and inspector share selection by topology name and native cell ordinal. Click a
cell to select it; Shift-click adds or removes it. The address field accepts
inclusive ranges such as **0-19, 32, 63**, and **Select visible** selects the
current layer and visibility filter. Selection outside the view remains selected
and is counted explicitly. **Apply to cells** paints the entire selection in the
chosen domain, after checking integer precision and declared bounds.

The **Author** view displays authored values. **Preview** displays temporary
execution state; painting requires switching back to Author. Selecting a cell
never runs rules. The inspector's **Submit selected cell** is an explicit preview
action through the supported input adapter. Preview register controls and time
travel live in the collapsible **Document & diagnostics** panel.

Value appearance maps arbitrary integer values to a label, color, shape (cube,
sphere, or diamond), and optional visibility. Both views use the same binding;
3D shows a label for the primary visible selection, and 2D labels every button.
Bindings are JSON stored in the native custom string extension
`metadata.custom.puckStudioPresentation`. Qubic's X/O labels and shapes are
example metadata, not renderer behavior. **Show hidden values** keeps hidden
values available for editing. **Reveal in JSON** focuses the selected authored
cell value, or its state row when that cell has no explicit authored value.

## Navigate spatially

Volumetric topology opens in 3D. Drag to orbit, right-drag to pan, and scroll to
zoom. Fit world, fit selection, axis views, and orthographic projection offer
explicit framing. Layer isolation and adjustable separation expose dense volumes;
these controls never change logical coordinates or saved topology. The optional
authored-direction overlay is shown with all layers and values.

The 2D view uses native buttons and retains the same selection. Tab enters the
cell list; arrows, Home, and End move focus; Enter or Space selects. Shift adds
or removes a cell. Volumes show one layer at a time, with at most 144 buttons per
page. Non-hex pages use native ordinal order; hex pages show the native coordinate
layout. The address inspector is also available without pointer interaction.
3D load or rendering failures offer a 2D fallback.

Spatial projection supports grid, box, hex, ring, and explicit lattice geometry.
Hex positions use the Eisenstein basis; rings have a circular presentation.
Box and lattice logical z is vertical in the 3D view. Presentation is centered
near the origin, while the inspector retains native coordinates and addresses.
This topology visualization is not the native SDF world renderer.

## Explore the preview

Each preview tick evaluates rules once in document order. Edge rules remember
whether their gate was open on the previous tick. A demo board move runs an input
tick followed by an idle tick so request acknowledgements can rearm Edge gates.
Applying a preview register value runs one tick. Previous/next preview controls
include latch state, and changing preview after stepping back starts a new
preview branch. These controls never edit document revisions. Trace details use the
state at each gate evaluation, rather than the final state of the tick.

The expression interpreter parses a small grammar instead of executing
JavaScript. It supports integer arithmetic, comparisons, bit operations,
conditionals, inclusive board masks, and named board shifts. Integers remain
exact through 64 bits; values beyond JavaScript's exact number range stay BigInt
inside preview. This is not a claim of native arithmetic or compiler parity.
Native validation remains necessary before using a document in the engine.

JSON integer literals outside JavaScript's exact number range cannot be applied
or saved locally: parsing and reserializing them would round their values. Such
a draft remains editable and exportable without changing its original text.

Bound rules, unsupported effects or predicates, non-integer state kinds, and
unsupported domains disable preview while leaving the document editable. Board
input adapters currently cover the Qubic and hex demo request registers. Other
documents can expose geometry and register inspection without playable input.
Mask expressions currently require one topology and at most 64 cells. Nonzero
empty-cell values and wrapped grids require native preview.

## Performance limits and ownership

Document intake is limited to 2 MB, 100,000 values, 32 topologies, 512 state rows,
and 256 rules.
Geometry is cached by immutable topology identity and limited to 4,096 cells.
Three.js is lazy-loaded behind the viewport boundary. It renders on demand,
batches cells by three primitive shapes using instancing, updates changed
instance buffer ranges, caps pixel ratio at 1.5, and uses no shadows or continuous
hover animation. React Three Fiber disposes declaratively owned GPU resources
on unmount. Direction overlays cap at 16,384 unique edges. Camera and hover
interaction never dispatch document or simulation actions. View options include
a one-second frame-count measurement and renderer draw/resource counts; these
are local diagnostics, not a frame-rate guarantee.

Expressions have character, token,
nesting, and operation budgets. A preview tick has a 25 ms deadline checked
between rules and effects. A refusal leaves the prior interactive snapshot
intact; it is not a partial commit.

Document history retains at most 64 revisions and 8,388,608 UTF-16 code units
(roughly 16 MiB of text), dropping oldest revisions when either budget is exceeded.
Preview history retains at most 128 snapshots and shares unchanged board rows. Trace
details expand on demand, and inactive tool panels are unmounted. Scenario and
Monte Carlo batches run in a dedicated worker with cancellation and a 30-second
deadline. Rollouts allow at most 200 games and 128 ticks per game. Their random
results are observations of this preview, not replay proofs or deadlock proofs.

The implementation lives under `src/portal/src`: `engine` owns intake and bounded
execution and demo input adapters; `authoring` owns cell references, immutable
paint candidates, presentation bindings, scene projection, and JSON addressing.
`machines/worldSimulationMachine.ts` owns document transactions and the separate
preview history. `components/world` owns editing and inspection, with Three.js
objects confined to `SpatialTopology3D.tsx`, and
`clients/worldStorageClient.ts` owns local document persistence. The TypeScript
tests are exercised by Node's test runner in `src/portal/tests`.
