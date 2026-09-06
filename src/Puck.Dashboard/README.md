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

The designers change the applied document and reset preview history. Apply or
export an outstanding JSON draft before opening a designer. JSON is also the
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

## Explore the preview

The board uses native buttons and displays boxes one layer at a time. Tab enters
the board; arrows, Home, and End move focus; Enter or Space submits a supported
demo move. Hover and focus only inspect the current cell in a fixed area. They
never execute rules or place a speculative piece. Ring and hex cells retain their
native ordinals; the inspector shows coordinates. This view does not load the
3D renderer.

Each preview tick evaluates rules once in document order. Edge rules remember
whether their gate was open on the previous tick. A demo board move runs an input
tick followed by an idle tick so request acknowledgements can rearm Edge gates.
Applying a preview register value runs one tick. Undo and redo include latch
state, and editing after undo starts a new history branch. Trace details use the
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
Geometry is cached by immutable topology identity and limited to 4,096 cells;
the board renders at most 144 cells per page. Expressions have character, token,
nesting, and operation budgets. A preview tick has a 25 ms deadline checked
between rules and effects. A refusal leaves the prior interactive snapshot
intact; it is not a partial commit.

History retains at most 128 snapshots and shares unchanged board rows. Trace
details expand on demand, and inactive tool panels are unmounted. Scenario and
Monte Carlo batches run in a dedicated worker with cancellation and a 30-second
deadline. Rollouts allow at most 200 games and 128 ticks per game. Their random
results are observations of this preview, not replay proofs or deadlock proofs.

The implementation lives under `src/portal/src`: `engine` owns intake and bounded
execution, `machines/worldSimulationMachine.ts` owns applied documents and
preview history, `components/world` owns editing and inspection, and
`clients/worldStorageClient.ts` owns local document persistence. The TypeScript
tests are exercised by Node's test runner in `src/portal/tests`.
