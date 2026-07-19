# World UI/editor implementation review — 2026-07-18

Active implementation review of the World UI/editor arc from
`7aa57021c06f71026afaa5e4a49f1b773ebf9cf8` through committed `HEAD`, including
the current uncommitted P3 continuation. This document describes the current
architecture, the direction already settled by the active plan, and work that
must close before later phases build on it. It is not a commit-by-commit audit.

The worktree was changing during review. Findings below describe the final
static snapshot observed after the routed camera inputs were renamed to
`editor.stick.move` / `editor.stick.look`. Re-check an item against the current
line if later work has already addressed it.

## Outcome

The arc is directionally coherent. It creates two reusable libraries, installs
one backend-neutral overlay seam, proves the initial World UI/editor mode, and
then extends the same document/mutation/render path for selection and
manipulation. The important architectural choices are sound:

- authoring contracts move out of Demo into one `Puck.Authoring` truth;
- overlay surfaces are CPU writers into one neutral GPU node rather than new
  render nodes per feature;
- selection and drag preview remain client-local;
- release submits one whole-row, grant-gated, server-validated mutation;
- applied mutations remain the deterministic journal/undo authority;
- the planned page-group/chord-meaning rework replaces the temporary mode
  layer before binding authoring expands around it.

The uncommitted P3 continuation is not ready to be treated as landed. Its
primary gaps are editor lifecycle cleanup, non-finite input rejection,
acknowledgement-correlated preview retirement, real viewport clipping, and the
missing `editor-edit` proof/measurement envelope. The authoring lift also needs
a strict canonical document boundary before inline creations and hash pins are
built on it in P5/P6.

## Current architecture and trajectory

### Landed through `HEAD`

1. `WorldCamera.AvatarEye` became the mechanism-named `Anchored` camera.
2. `Puck.Authoring` became the single declaration site for
   `CreationDocument`, `CreatorIntent`, `AvatarPrimitive`, `CreationStore`,
   `EditHistory<T>`, and `GridSnap`. Demo consumes the shared document contract
   while retaining private pure-code copies only until its authoring surface is
   retired.
3. The creation editor round trip now carries cameras, behavior, text runs, and
   extension data instead of discarding fields it cannot author.
4. `Puck.Overlays` supplies the glyph pack, design tokens, record builder,
   console/binding/toast writers, and one `UnifiedOverlayNode` for Vulkan and
   Direct3D 12.
5. `SdfWorldRenderSpec` / `SdfWorldRenderBuilder` provide the neutral decorator
   seam used by World to put the unified overlay around the SDF producer.
6. World P1 wires the console mirror, per-seat binding bars, and mutation
   toasts. World P2 adds per-seat editor entry/exit, input diversion, fly/orbit
   rigs, editor layout, and console twins.
7. The active plan supersedes the temporary fifth binding layer: modes become
   active page groups, and a chord's meaning becomes layered data whose value
   is either a page or a direct command.

### Current uncommitted continuation

The worktree contains most of P3:

- polymorphic `WorldSceneRow` data (`boulder` / `slab`) and row-level
  upsert/remove mutations;
- checked-in world migration to `scene.rows`;
- client-local selection, proximity ordering, look-ray picking through a
  document-derived `SdfFieldEvaluator`, orbit retargeting, and selection tint;
- client-local drag and ghost rows composed over the delivered definition,
  with one mutation on release;
- construction-time authoring headroom and server-side render-envelope checks;
- selection/manipulation console verbs and LT/RT binding pages;
- an editor HUD added as another unified-overlay writer/source pair.

This preserves the intended authority boundary: preview state is not protocol
state, while the committed row still crosses the established server mutation,
grant, validation, envelope, journal, and delivery path.

## Triage

| ID | Priority | Area | Current finding | Closure |
|---|---|---|---|---|
| UIE-1 | P1 | Editor lifecycle | A live drag/ghost survives editor exit or seat departure. | Deactivation clears drag/frozen preview and selection; exit/rejoin proof covers it. |
| UIE-2 | P1 | Numeric safety | Editor-local parsers accept `NaN` and infinity, allowing bad camera, snap, picker, or preview state before server validation. | Every local float boundary rejects non-finite values; proof covers all typed surfaces. |
| UIE-3 | P1 | Preview delivery | A frozen released preview retires on any definition revision, not its own result. | Correlate retirement with expected row/result identity; prove unrelated delivery and rejection. |
| UIE-4 | P1 | Split-screen UI | Editor HUD records are positioned from a seat viewport but are not clipped to it. | Add a real record clip/viewport contract and a narrow four-seat pixel proof. |
| UIE-5 | P1 measurement gate | Drag rendering | Continuous drag rebuilds and uploads the full world SDF program at presentation cadence. | Measure 1/4/128-player drag on both backends; keep or replace the path based on evidence. |
| UIE-6 | P1 before P5 | Creation documents | Creation loading silently relabels absent/unknown schemas as v1 and lacks a public canonical validate/normalize/hash boundary. | One strict in-memory/byte pipeline rejects other schemas and returns canonical bytes + hash. |
| UIE-7 | P2 | Overlay startup | Building the ASCII overlay pack decodes the full 4435×4440 RGBA atlas, creating roughly 150 MiB of lower-bound transient image storage to retain about 1.4 MiB. | Bake/load a compact prepacked overlay artifact; measure startup/GC before and after. |
| UIE-8 | P2 | Overlay envelope | Record overflow is silent and writer order can starve later HUD/toast surfaces. | Observable overflow plus declared capacity/priority policy and an all-writers envelope test. |
| UIE-9 | P2 measurement gate | Overlay shader | Every output pixel scans every submitted panel and element; no overlay timing ceiling exists. | Instrument and measure representative 1/2/4-seat frames before choosing spatial bins. |
| UIE-10 | P2 | Targeting | Candidate cycling sorts every selectable row and has no proximity radius. | Make the candidate policy explicit and bounded before worlds scale. |
| UIE-11 | P2 / planned P4 | Concurrent authority | Selection does not acquire or surface an exclusive edit hold, so same-row edits remain last-writer-wins. | Complete the planned P4 grant/HUD semantics before advertising concurrent editing. |
| UIE-12 | P2 | Layering | The general overlay library references `Puck.SdfVm` only for a concrete capture fallback. | Move `ICaptureRequestTarget` to a neutral contract and implement it on `SdfEngineNode`. |
| UIE-13 | P2 | Proof/docs | P3 has no `editor-edit` proof and current orientation/World docs do not describe the landed libraries or worktree schema. | Add the proof and update canonical docs in the landing change. |

## Findings

### UIE-1 — Deactivation leaves a stale drag alive

[`WorldEditorSession.Exit`](../../src/Puck.World/Client/WorldEditorSession.cs)
and departed-seat pruning route through `Deactivate`, which clears the mode
layer, rig state, and staged input but does not cancel the corresponding
`WorldEditorDrag` channel. The command guards then prevent cancel/release while
the seat is outside editor mode. Re-entry or slot reuse can inherit and commit
the old pending row.

Make editor deactivation own the complete client-session teardown: cancel live
and frozen preview state and clear selection before the slot can be reused.
Cover explicit exit, controller departure, and rejoin.

### UIE-2 — Non-finite typed inputs can poison local render state

The numeric parsers in
[`EditorCommandModule`](../../src/Puck.World/EditorCommandModule.cs) and
[`EditorSelectionCommandModule`](../../src/Puck.World/EditorSelectionCommandModule.cs)
use `float.TryParse` without `float.IsFinite`. Consequences occur before the
server's thick validator can help:

- `editor.drag NaN ...` can put a non-finite center into the pending row and
  trigger an SDF rebuild;
- `editor.snap NaN` passes a `pitch <= 0` guard and contaminates `GridSnap`;
- non-finite camera pose/speed can contaminate the rig and picker.

Reject non-finite values in shared parse helpers and again at public state
setters where a non-console caller can reach them.

### UIE-3 — Frozen preview retirement watches the wrong event

[`WorldEditorDrag.Reconcile`](../../src/Puck.World/Client/WorldEditorDrag.cs)
clears a released preview when `WorldClient.DefinitionRevision` changes. That
revision is global: another seat or console mutation can advance it before the
released mutation is accepted or rejected. The overlay can therefore snap back
early or obscure the intended rejection window.

Retirement needs correlation with the submitted act: an operation/result id,
or equality between the delivered keyed row and the frozen expected row. The
deadline remains the honest fallback for a missing response.

### UIE-4 — Seat viewport placement is not seat viewport clipping

[`EditorHudWriter`](../../src/Puck.Overlays/EditorHudWriter.cs) offsets its
absolute-pixel panel by the normalized seat origin. Neither
[`OverlayFrameBuilder`](../../src/Puck.Overlays/OverlayFrameBuilder.cs) records
nor
[`overlay-unified.frag.hlsl`](../../src/Puck.Overlays/Assets/Shaders/overlay-unified.frag.hlsl)
carry a clip/scissor or viewport index; the shader evaluates records against
global `SV_Position`.

At the current 46-character ceiling, the HUD can be about 484 pixels wide. A
640×480 2×2 layout gives each seat 320 pixels, so the panel crosses into its
neighbor despite having the correct origin. Add a general clip-table/index
contract rather than special-casing this writer; later per-seat writers need
the same invariant.

### UIE-5 — Wire coalescing does not remove render-cadence rebuild cost

`WorldEditorDrag.ApplySnap` advances its revision whenever a non-snapped drag
moves. [`WorldFrameSource.CaptureFrame`](../../src/Puck.World/Client/WorldFrameSource.cs)
folds that revision into its rebuild watch and re-runs the full world program
emission/upload path. This successfully prevents mutation validation, network,
and journal flooding, but a continuous gesture still pays whole-program CPU
construction and GPU upload at presentation cadence.

This is a measurement-required risk, not a claimed regression. Measure the
actual 1/4/128-player cost on both backends without concurrent GPU work. If it
is material, use a cheaper preview transform/override while retaining the same
one-mutation release boundary.

### UIE-6 — Creation data needs a strict canonical boundary before embedding

[`CreationStore.Load`](../../src/Puck.Authoring/CreationDocument.cs) normalizes
deserialized data and unconditionally sets `Schema` to `puck.creation.v1`.
Missing, misspelled, or future schema values are therefore silently
reinterpreted as current v1. Normalization is private and file-oriented, while
P5/P6 need to validate embedded/CAS bytes, canonicalize them, and hash exactly
the bytes placed in the World document.

Before inline creations land, provide one public byte/string/in-memory
pipeline that:

1. rejects any schema other than the current schema;
2. validates stable ids, camera/feed uniqueness, palette, frames, transforms,
   behavior, text, and extension invariants;
3. normalizes a valid v1 value;
4. returns canonical bytes and their hash.

World import and validation should use that exact path. Also correct the
`CreationStore.Save` narration: the settled World design resolves mutable named
refs once and inlines canonical data; runtime does not depend on a name in the
store.

### UIE-7 through UIE-9 — Overlay resource and scaling envelopes

The overlay pack currently decodes the full combined 4435×4440 RGBA atlas via
`FontAtlasImageDataLoader` and then copies only 95 ASCII cells into a 46×82
pack. The decoder simultaneously holds scanlines and a new RGBA buffer, giving
a lower-bound transient near 150 MiB before PNG bytes and stream capacity, to
retain about 1.4 MiB. Bake a compact overlay-specific artifact instead of
paying the general atlas cost at every World startup.

The record builder silently drops panels/elements when fixed capacities are
full. Writer order is console → binding bar → editor HUD → toast, so an earlier
surface can starve a later, more urgent one without a diagnostic. Track
overflow, define reservation/priority semantics, and exercise all writers at
their declared maxima.

The fullscreen shader loops over every submitted panel and element for every
pixel. At the 192-element ceiling and 2560×1440, the theoretical element scan
is roughly 708 million loop iterations before early-outs. Current content may
remain cheap; only timing can decide. Add an overlay pass timing and measure
representative seat/writer loads before introducing spatial bins or ranges.

### UIE-10 and UIE-11 — Selection policy and concurrent authority

[`WorldEditorTargeting.Cycle`](../../src/Puck.World/Client/WorldEditorTargeting.cs)
sorts the complete pick table by distance, so “proximity candidates” currently
means every selectable row in the world. Define a radius/count policy as data
before large creation/placement catalogs make the chord unusable.

Section-level `Mutate` grants correctly protect the server boundary, but
selection does not acquire an exclusive hold. Two editors can preview the same
row and submit whole-row replacements; delivery order wins. This is explicitly
planned P4 work rather than a surprise P3 regression. Do not advertise coherent
multi-editor ownership until the exclusive acquisition, denial, release, and
HUD narration are complete.

### UIE-12 — Capture fallback pulls the overlay upward into SDF

`Puck.Overlays` otherwise wraps a neutral same-device producer, but its project
references `Puck.SdfVm` and `UnifiedOverlayNode` special-cases
`SdfEngineNode` because the capture interface lives in SDF and the node does
not implement it. Move `ICaptureRequestTarget` into `Puck.Hosting` or another
neutral presentation contract, implement it on `SdfEngineNode`, and remove the
concrete fallback/project reference.

### UIE-13 — Proof and canonical documentation lag the implementation

The active plan declares `proof.cs editor-edit` as P3's exit bar, but the proof
dispatcher currently ends at `editor-mode`. P3 needs a scripted proof for:

- select and look-ray reselect;
- drag cancel versus one-entry release;
- journal count and undo;
- rejection rollback and an unrelated definition delivery;
- exit/departure during a drag;
- non-finite typed input rejection;
- authoring-headroom rejection;
- narrow four-seat HUD clipping;
- representative drag and overlay timing.

The existing `ui-floor` proof runs both backends but only asserts console scrim
and danger-toast pixels. It does not prove text/glyph decode, binding chips,
HUD output, or calibrated cross-backend overlay parity. Extend its controlled
regions accordingly.

Documentation that must move with the landing change:

- add `Puck.Authoring` and `Puck.Overlays` ownership/layering to
  `docs/project-map.md` and the capability catalog;
- index the active World UI/editor plan in `docs/README.md`;
- update `src/Puck.World/README.md` from boulder-only scene data to
  polymorphic rows and row verbs;
- make `docs/game-studio-plan.md` point destination work to World/shared
  authoring rather than Demo;
- correct stale `SdfWorldRenderBuilder` documentation that still describes a
  deleted Vulkan-only decorator rule;
- scope `Puck.Authoring.DocumentJsonOptions` documentation to the document
  families that actually use it.

## Determinism boundary

The committed mutation and journal are deterministic once the final row exists.
Stick drag currently integrates presentation `deltaSeconds` and then persists
the resulting float row, so replaying identical command snapshots need not
recreate the authored coordinates. This is compatible with Puck's stated
presentation/artistic exception if authoring gestures are deliberately outside
simulation replay. Record that boundary explicitly. If edit gestures themselves
must replay to the same coordinates, integrate them at fixed ticks instead.

## Verification state

Execution stopped on the user's instruction because it interfered with another
agent. No further builds, tests, demos, proof runs, or GPU work should be run as
part of this review.

Evidence collected before that instruction is limited and should not be
overstated because the worktree was changing:

- targeted Release builds succeeded at earlier snapshots;
- semantic inspection found one shared `CreationDocument` / `CreatorIntent`
  declaration with Demo callers resolved to `Puck.Authoring`;
- a transient duplicate `editor.move` registration crashed the command parser,
  then was repaired in the current tree by renaming routed stick verbs;
- later proof attempts were inconclusive due concurrent processes and were
  stopped; there is no green final-snapshot live proof to claim;
- `git diff --check` was clean apart from line-ending warnings at the observed
  snapshot;
- no `editor-edit` proof, overlay timing, controlled backend overlay comparison,
  or final-snapshot full battery result exists.

## Recommended execution order

1. Close UIE-1 through UIE-4: lifecycle, finite inputs, correlated delivery,
   and viewport clipping.
2. Add `editor-edit` and extend `ui-floor`; include the measurement gates for
   drag rebuild and overlay cost rather than assuming either is acceptable.
3. Land the P3.5 page-group/data-defined-chord architecture before persistent
   binding UX expands around the temporary mode layer.
4. Complete P4 exclusive edit ownership and denial/HUD narration.
5. Add the strict creation canonicalization/hash boundary before P5 embeds
   creations or P6 builds normalize-and-commit workflows.
6. Update World, project-map, capability, studio, SDF builder, and docs-index
   documentation in the same landing changes.
