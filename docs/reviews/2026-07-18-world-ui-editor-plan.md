# Puck.World UI/editor arc — the plan (2026-07-18)

Puck.World gets its first rendered UI and its editor in one arc. The moldable-
state substrate (mutation vocabulary, journal-as-undo, principals+grants,
layered bindings — `docs/reviews/2026-07-18-world-moldable-state-handoff.md`)
is the foundation; Puck.Demo is the reference implementation and the donor,
never a dependency. This plan is grounded in a nine-report recon/analysis pass
over both codebases; every ruling below carries its evidence.

Doctrine that governs the whole arc:

- **The §2.6 audit is the review gate.** Every new surface must be different
  *data*, not a different *message*. The editor adds document sections and
  client-side command modules; the wire grows only new whole-row mutation
  records in the established pattern.
- **Verify by running.** World has no Post gate and gets none. Each phase
  lands with a `scripts/proof.cs` scenario driving the new surface over
  stdin — the established reference-behavior pattern (`expo-author` et al.).
- **Scope discipline.** Work lands in `src/Puck.World` and new/existing
  library projects. Demo is touched only to consume lifted libraries or to
  fix the creation round-trip bug (which protects existing content). Engine
  seams are named explicitly below.
- **Chord-first.** Pad chords are the primary authoring interface; console
  verbs are the assist/automation layer; the overlay is the reliable
  accessibility surface, diegetic mirrors come later
  (`docs/game-studio-plan.md:118-130`, `docs/overworld-demo-plan.md:203-205`).
- **Vocabulary names the primitive, and calcified names are renamed on
  contact.** A concept's name must describe the mechanism, never one use of
  it — supergreen makes renames free, so every phase renames what it
  touches. Precedent (owner-ordered, landed with this plan):
  `WorldCamera.AvatarEye` → `WorldCamera.Anchored` (`AnchorIndex` +
  `Offset`) — a camera anchored to an entity is not an "eye"; the anchor
  point is arbitrary data. The altitude split to preserve: document/protocol
  vocabulary stays mechanism-named, while genuinely anatomical facts
  (`WorldAvatarCatalog.EyeOffset` — where the avatar rig's head leaf is) and
  standard graphics terms (`SdfCameraRig.Eye`, the camera's own eye point)
  keep their honest names.

## 1. The decisions

### D1 — One editor, journal-authoritative, with a client preview channel

World gets ONE editor: a per-seat client mode over the live world, not a
local-copy-and-commit tool. Discrete acts (place, delete, retarget, wire,
kit edit) are immediate whole-row mutations — tick-buffered, revalidated,
journaled, grant-gated, exactly the granularity the 18 mutation records were
designed at. Continuous acts (move/rotate/scale drags) run in a client-local
preview channel and commit **one** whole-row mutation on the release edge.

Why: per-frame mutation is the worst path in the system — every
scene/screen apply runs a full worst-case 128-avatar render-envelope probe
(`Server/WorldServer.cs` `TryApplyMutation` → `WorldRenderEnvelope.TryFit` →
`WorldFrameSource.BuildWorld(probeWorstCase: true)`,
`Client/WorldFrameSource.cs:96-104`), full-document revalidation (which
recompiles the complete binding composition every time,
`WorldDefinitionValidator.cs:378-416`), a journal append, and a full program
re-upload on delivery. A 30 Hz drag would flood the journal (300 entries per
10 s drag), destroy gesture-grained undo, and make `world.undo` replay O(huge).
Both Demo editors already coalesce drags to one undo step
(`CreatorScene.cs:2204-2245`, `WorldSculptController.cs:637-645`) — this plan
moves that coalescing to the wire boundary.

Undo has two honest domains: mid-gesture (client-local, instant abort) and
committed history (`world.undo` — one journal entry per completed act). The
creation *sub-editor* (sculpting, D6/P6) additionally keeps a bounded local
snapshot ring (`EditHistory<T>`) because sculpting is frame-rate editing of a
separate document; its commit is one creation-row mutation.

Rejected: local-edit-scene-plus-commit as the *world* editor (the Demo
`WorldSculptController` model). It duplicates the document model as a second
in-memory god-object, goes stale under concurrent principals, and its commit
is either a journal-resetting whole-swap or an unbuilt 3-way merge. The
pattern survives only inside the creation sub-editor, where the document
really is separate.

### D2 — The UI stack: `Puck.Overlays`, one unified node, backend-neutral

A new library project `Puck.Overlays` (references `Puck.Compositing`,
`Puck.Text`, `Puck.Abstractions`; owns its HLSL + the dual-compile
`CompileShaders` target) hosting ONE `UnifiedOverlayNode` built on the proven
Demo skeleton: `IRenderNode` decorator, one fullscreen-triangle fragment
pass, one storage buffer with the glyph-pack prefix, push constants for
scalars, per-node submission fence, pass-through fast path, capture
forwarding (`src/Puck.Demo/Ui/OverlayPanelsNode.cs` is the template — its own
header already states the generalization: "panels are data … a future surface
is a new writer, not a new node or shader").

What the unified node adds over the Demo trio:

- **One node, N writers.** Console panel, binding bar, and editor HUD are
  writers into one record vocabulary (panel chrome / rect / text-run /
  procedural-icon element kinds — the binding bar's icon repertoire folds in
  as element kinds). The three-node split and the triplicated ~130-line
  boilerplate die.
- **Per-viewport scoping.** Records carry a viewport index resolved against
  the seat split (`LayoutRegion`) — Demo never had to solve 4-player
  split-screen UI; World must, day one.
- **Buffer-fed design tokens.** The `DesignTokens` palette/geometry uploads
  once as a prefix block (≈80 words) beside the atlas, killing the four-way
  hand-synced HLSL literal tables. Tokens stay data; a run-document tunable
  later costs nothing. The duplicated SDF-decode functions collapse into one
  `.hlsli` in the same change.
- **Backend-neutral from the first commit.** World defaults to D3D12 on
  Windows (`Program.cs:24-27`) and registers only one backend — so the
  Vulkan-only posture of Demo's overlays is dead on arrival here. The recon
  proved neutrality is cheap: the nodes consume only neutral `IGpu*`
  services, D3D12 implements the full surface
  (`DirectXPresenterServiceRegistration.cs:41-52`), and the overlay shaders
  already dual-compile to `.dxil`. The only Vulkan lock-in is Demo's ~30-line
  `SdfParityProducers.BuildVulkanServices`; `Puck.Overlays` gets a neutral
  `BuildServices` resolving `IGpuDeviceContext` directly and selecting
  bytecode via `SdfWorldRenderBuilder.BytecodeExtension`. The D3D12 overlay
  path has never run — budget its shakeout once, first, then it covers every
  later surface. Delete the D3D12 `Decorate` skip in
  `SdfWorldRenderBuilder.cs:84-91` when it passes.
- **Glyph pack: lift, don't redesign.** `SharedGlyphAtlas` +
  `SharedGlyphSdfPack` move into `Puck.Overlays`. The pack is already
  index-addressed; keep ASCII-95 for v1 (optionally rebake Latin-1-ish at
  negligible cost). The honest documented ceiling is fixed-cell monospace —
  proportional text needs a per-glyph-quad element kind (deferred ledger),
  which is where `Puck.Text.TextLayout` enters the GPU path.

Demo keeps its three nodes untouched until Demo dies — per the retirement
trajectory, we never polish Demo internals; the library is World-first.

Diegetic UI composes later, not first: the unified node's offscreen variant
renders the same records to a texture published through
`WorldScreenBinder`'s existing provider seam (`WorldScreenBinder.cs:211-214`)
— UI-on-a-world-screen with zero engine change. Diegetic is a *presence*
answer, not a *usability* answer; precision editing chrome stays screen-space.

### D3 — Chords are binding data; the hand-rolled trackers die

The editor's chord surface is a code-authored `BindingProfileDocument` —
ordered-modifier pages (`BindingPageDefinition.Chord`), per-page analog
entries (the `player.move` Axis2D precedent, `WorldDefaultBindings.cs:55-58`),
hold/release pairs, labels/icons for the bar — entering each editing seat's
compose stack as a **mode layer** (a fifth layer beside
`WorldSeatBindings.BaseLayers`, or the session-rebind layer; never a world
`bindingOverlays` mutation, which re-binds every seat,
`WorldSeatBindings.cs:106-120`). World's engine default binds no triggers
(`WorldDefaultBindings.cs:27-69`), so LT/RT modifier pages collide with
nothing.

This is strictly better than Demo's hand-rolled `HeldOrderTracker`
controllers (whose own header confesses the duplication,
`Puck.Commands/HeldOrderTracker.cs:5-12`): `PagedInputBindings` latches a
press's resolution across page flips (`PagedInputBindings.cs:17-21`), and
page-resolved commands are `CommandSnapshot`-visible — the editor becomes
scriptable and replay-safe for free. Editor-local state (pending transforms,
drag brackets, camera mode) and typed-parameter verbs stay in the
`editor.*` command module — the division both Demo editors already concede
("a chord can't express typed parameters",
`WorldSculptController.cs:231-232`).

The binding bar renders the **active page view** (`BindingPageView`), never a
hardcoded legend — the product doctrine already demands this
(`docs/overworld-demo-plan.md:203-205`); Demo's `PublishCreator`/
`PublishWorld` hardcoded legends are the anti-pattern and die with Demo.

**Owner refinement (post-P2): modes are page GROUPS, and a chord's meaning
is DATA.** P2 first shipped editor mode as a fifth compose layer, which
forced the editor's resting page to impersonate `base` (one empty-chord page
per profile) and let unmentioned lower-layer entries leak through (masked by
the idle diversion). The settled design separates the questions: layers
answer *authoring* (who overrides what a binding means); a per-seat **active
page group** answers *runtime mode* (which family is live); and a **chord
row** answers what a chord does — `(group, ordered chord) → meaning`, where
meaning is a discriminated union: **page** (an entry table — the selector
case) or **command** (a direct binding with full entry semantics:
HoldRelease, CommandValue, Label/Icon). Page switching is not privileged —
it is one meaning a chord can carry, and players/authors declare meanings
through the same four layers (the merge key gains the group; chord rows key
on `(group, chord)`). One resting page per group; one meaning per
`(group, chord)`, rejected loudly otherwise; deepest-held prefix resolution
and the press latch are reused unchanged. Mode flips become pointer-level
switches on the compiled profile — no recompose. This is the P3.5 rework (a
flagged `Puck.Commands` seam); the diversion remains as intent routing, no
longer a leak backstop.

### D4 — Selection is client state: proximity + cycle now, field-ray later

A selection is `(WorldSection, id-or-index)` — pure data over the stable-id
convention every row already has. It lives client-side in the editor session;
it is never protocol. The server-visible reflection of "I'm editing this" is
an **exclusive grant**, which already exists as data (`WorldGrant.Exclusive`).

- **v1 targeting:** proximity candidate set (the `player.engage` planar
  distance pattern, `PlayerCommandModule.cs:615-625`) + chord cycle through
  candidates (the Creator model, `CreatorScene.CycleSelection`), highlight
  via material swap + program rebuild at human cadence. Zero engine change.
- **Precision upgrade (in-arc, after v1 targeting works):** a picking
  program derived from the *document* (not the render program — the live
  program contains `TransformDynamic` avatar leaves, which
  `SdfFieldEvaluator` rejects by contract, `SdfFieldEvaluator.cs:81-85`),
  with one material id per row so `RayHit.Material` IS the row index.
  Fixed-point, `WorldQueryConfidence.Exact`, rebuilt only on
  `DefinitionRevision`, ray = eye + facing — mouseless look-picking, and
  sim-grade deterministic if a server ever needs it. Slots into
  `IWorldQuery`.
- **Rejected:** GPU id-buffer readback — highest cost, float, cross-backend
  parity surface, answers a question the other two already answer.

### D5 — The editor camera is a client-side rig swap

Entering editor mode swaps the seat's `OrientedFollowRig` for a free-fly
`FixedRig` (editor mutates Eye/Target per frame from pad input) or an
`OrbitRig` around the selection — the rig array becomes `ISdfCameraRig[]`
(`WorldFrameSource.cs:29,74-78`; rigs are mutable-property classes,
`SdfCameraRig.cs:220-236`). Pure presentation; nothing crosses the wire.
While flying, seat input diverts from the avatar at the client
`SeatController` level (the engaged-idle precedent, `WorldClient.cs:120-134`)
so the body idles honestly. `LayoutRegion` grows an editor layout policy
(seat 1 fullscreen-edits while seats 2–4 keep playing) — client-only,
same `(count, index)` function.

Rejected: flying a `WorldCamera` row via mutations (per-frame mutation, and
camera rows are what the editor *edits*); an editor 'view' screen source for
the main view (jumbotron views are budgeted low-res AO-less offscreens —
wrong altitude; fine later as camera-row preview thumbnails).

### D6 — Creations become World data: asset rows + placement rows, no bake in v1

Two new `WorldSection`s in the established whole-row pattern:

- `creations: [{id, doc, hash}]` — the asset. **Inline-canonical with a hash
  pin**: the full `puck.creation.v1` JSON embeds in the world file; the hash
  (recomputed at `world.save`) is identity/provenance. Measured cost is
  negligible (creations run 4–145 KB; a furnished plaza ≈ +250–400 KB on a
  ~15 KB world file) and the journal replays mutations, not documents, so
  undo never touches doc size. World files stay self-contained — the settled
  storage doctrine. CAS (`Puck.Assets.ContentAddressedStore`) becomes an
  authoring-time import/export cache, not a load-time dependency; ref-by-name
  stays rejected (mutable identity violates the bit-for-bit doctrine Demo
  already settled, `WorldDocument.cs:48-51`).
- `placements: [{id, creationId, position, yaw, scale, …}]` — the instance.
  Field vocabulary donated by Demo's `PlacementDocument`
  (`WorldDocument.cs:74-87`: repeat, mirror, pattern). Asset/instance
  separation is exactly the "named assemblies stamped many times" demand
  (`docs/game-studio-plan.md:89`).

Four new mutation records (`UpsertCreation`/`RemoveCreation`/
`UpsertPlacement`/`RemovePlacement`) — §2.6-clean: every genre places
different data through the same messages. The render envelope reserves boot
headroom (`MaxPlacements × MaxShapesPerStamp` worst case in the probe — the
Demo budget precedent, `WorldScene.cs:135-150`;
`CreationDocument.StampShapeCount()` already measures emission cost).
Over-budget placements reject loudly into the editor HUD.

Animated placements replay their frame timelines **client-side on the render
clock** (the Companion pattern, `CompanionState.cs:10-18` — matches World's
presentation-side gait precedent, `WorldFrameSource.cs:124-126`). The later
"creation as a driven body" rung needs zero new protocol — kit + wander +
`world.grant <principal> drive body:<n>` is capabilities-first-class already;
the placement row carries a nullable role/drive field so that rung lands
without schema surgery. Creation-as-avatar-costume is a separate deferred
seam (the avatar catalog's frozen slot-identity contract,
`WorldAvatarCatalog.cs:11-13`, makes it a rig-source arc of its own).

**No bake in v1.** Sculpt → save → place is the honest boundary. The
sculpt→bake→boot-on-screen unification composes later from existing seams —
`IScreenMachineEngine.Create` already takes bytes
(`Puck.Abstractions/Machines/IScreenMachineEngine.cs:15-22`) — without any
World schema change.

**Prerequisite fix (P0):** `CreatorScene.ToDocument()/LoadDocument()`
silently drop `Cameras`, `Behavior`, `TextRuns`
(`CreatorScene.cs:1984-2049, 2056-2160`), and `CreationDocument` has no
`[JsonExtensionData]` bag — flagship content is one editor save away from
destruction. Fix is small (carry-and-overlay + the `WorldDefinition`
extension-bag pattern; `CreationStore.Normalize` already self-heals stale
anchors) and must land before creations become World content. The
`CreationDocument` family + normalize/store half moves to a library World
may reference (P0), fixing the CWD-relative `"store"`/`"creations"` roots in
the same move.

### D7 — Live-apply: cameras now, defaults stay asymmetric, addons deferred

- **Cameras (P4, cheap):** the machinery is already live-capable
  (`TryView` runtime binds, `ViewStack.Register` updates in place, rigs are
  mutable) — the gap is one stale boot-captured list
  (`WorldScreenBinder.m_cameras`, `WorldScreenBinder.cs:69`) and one missing
  `ReconcileCameras` diff hook beside the existing `ReconcileScreens` call
  in the `DefinitionRevision` branch (`WorldFrameSource.cs:148-151`). Pose
  edits become property writes on live views; only dimension changes
  recreate. Delete the "applies at next boot" narration.
- **Population/render defaults:** the asymmetry is settled doctrine, not a
  gap — session levers own "now", defaults mutations own "the document",
  Phase 5's fold composes them at `world.save`. Live-applying defaults would
  invert that and create a write loop with the fold. **Ruling: keep the
  asymmetry; the editor HUD presents "live" levers and "defaults" edits as
  visibly distinct operations.** (Cheap to revisit if the owner overrules —
  it's a two-line hook either way.)
- **Addons (deferred ledger):** live remount needs a definition watch and a
  row diff in `WorldAddonDriver` plus per-instance unmount semantics that
  `AddonHost` has not yet proven (whole-host disposal only today,
  `WorldAddonDriver.cs:153-161`). Grants already survive remount by design.
  Not editor-blocking; boot-time mounting stays the honest story this arc.

### D8 — Grants: a `Profile` subject and an exclusivity-acquisition fix

- **`GrantSubjectKind.Profile`** — per-profile `Edit` trust becomes data.
  Wrinkle: profile ids are strings, `GrantSubject.Value` is an int — widen
  `GrantSubject` with a nullable string field (record-struct equality holds;
  the seeded `Edit/All` wildcard keeps local play unchanged via the
  `Contains(All)` path, `WorldGrants.cs:107`).
- **Exclusive section editing:** enforcement is already correct (an
  exclusive holder overrides wildcards, `WorldGrants.cs:94-108`), but
  acquisition is blocked on a default table because seats and console hold
  per-section `Mutate` grants concretely (`SeedDomain`,
  `WorldGrants.cs:77-84`) and rule 2 only exempts the `All` wildcard
  (`WorldGrants.cs:196-207`). Extend the exemption to the seeded permissive
  defaults for Section subjects — the smallest honest change, mirroring the
  existing Drive/All rationale ("the backdrop must never block an exclusive
  hold").
- **Row-level subjects** (two seats editing different rows of one section):
  deferred until demonstrated need — it's again pure data
  (a `Row:(section,id)` subject kind) when wanted.

### D9 — Scene grows per-row mutations

`WorldBoulder.Id` documents itself as "its mutation address"
(`WorldDefinition.cs:481`) but only whole-scene `SetScene` exists — an
editor doing per-boulder edits would re-send the whole scene per act. Grow
`UpsertSceneRow`/`RemoveSceneRow` (and generalize the scene row vocabulary
from boulder-spheres toward Demo's terrain-patch shapes as data — same
section, richer rows). Demo's separate screen-wiring table dies (source is
part of the screen row; `UpsertScreen` whole-row replaces it); Demo's
daylight dial dies as document data (its World home is the session render
levers); walkability/bounds/collision is explicitly **out of this arc** — a
sim-engine seam (World bodies have no collision at all today) that deserves
its own plan.

## 2. The phases

Each phase is independently landable, verified by running World, with a
proof scenario. Order is dependency-driven; nothing waits on anything it
doesn't need.

### P0 — Foundations (small, unblocks everything)

1. Fix the `CreationDocument` round-trip loss + add `[JsonExtensionData]`
   (in Demo — protects existing content immediately).
2. New library `Puck.Authoring` — with the copy-vs-move rule (owner-set):
   **data contracts MOVE** (one definition, Demo rewired to consume) because
   creations are durable content that outlives Demo and the schema must not
   fork — the `CreationDocument` family + `CreationStore` (root paths become
   configurable) + the shared serializer options; **pure code COPIES IN
   SPIRIT** (`EditHistory<T>`, `GridSnap` — Demo's copies stay untouched and
   die with Demo; rewiring their Demo consumers is exactly the forbidden
   Demo polish). "Copy" is never a 1:1 clone: Demo is the reference and the
   behavioral ORACLE, and the library version is the permanent artifact —
   destination-quality structure, naming, and API, with hard-won *behavior*
   (settled math, proven semantics, serialization shapes) preserved
   deliberately. The same rule governs every later lift: touch Demo only
   where a single data truth demands it, and improve everything on the way
   through.
3. New library `Puck.Overlays` skeleton: glyph atlas/pack lift, neutral
   `BuildServices`, the shared `.hlsli` decode include, dual-compile target.

Exit: both libraries build; Demo runs unchanged on the lifted code; a
hand-authored creation with cameras/text/behavior survives an editor
load→save round-trip byte-comparably.

### P1 — The UI floor: unified overlay + console panel + binding bar

1. `UnifiedOverlayNode` in `Puck.Overlays`: record vocabulary (panel / rect /
   text-run / icon element kinds), per-viewport scoping, buffer-fed tokens,
   fence/capture/pass-through per the proven skeleton.
2. **D3D12 shakeout first** — World's default backend renders the first
   panel; then verify Vulkan; then delete the backend gate in
   `SdfWorldRenderBuilder.Build`.
3. World wires `spec.Decorate`: console panel writer (mirroring
   stdin/stdout — the unification contract's "on-screen panel AND stdin")
   and binding-bar writer rendering each seat's **active page view**.
   Toast/status writer for mutation echoes and capacity rejections.

Exit: `dotnet run --project src/Puck.World` shows the console panel and
per-seat binding bars on both backends; `proof.cs` gains an overlay-visible
capture assertion; a mutation rejection surfaces as a toast, not just
stderr.

### P2 — Editor mode: chords, camera, entry

1. The editor binding document (code-authored pages: sculpt/select/place/
   camera/meta or as the design settles) entering the seat's mode layer;
   `editor.enter`/`editor.exit` verb + chord; the bar flips to editor pages
   automatically (it renders the active page — no new work).
2. Client-side `EditorCommandModule` (`editor.*` verbs — the console twin of
   every chord act, typed-parameter setters included).
3. Rig swap free-fly/orbit camera + seat-input diversion + `LayoutRegion`
   editor layout policy.

Exit: a seat enters editor mode mid-session, flies the camera while its
avatar idles and other seats keep playing, and every chord act echoes as a
console line; `proof.cs editor-mode` scripts enter/fly/exit over stdin.

### P3 — Selection and manipulation

1. Selection state `(section, id)`; proximity candidate set + cycle chord;
   highlight via material swap; selection HUD readout (id, section, grants).
2. The drag preview channel: pending row transform rendered client-side,
   commit-on-release as one whole-row mutation; cancel = never existed.
3. `UpsertSceneRow`/`RemoveSceneRow` mutations + scene-row vocabulary
   generalization (terrain-patch shapes as data).
4. Numeric entry via the console twin (`editor.move <x> <y> <z>` etc. — the
   game-studio §4 demand, satisfied by the assist layer).
5. In-arc upgrade once v1 targeting works: the document-derived fixed-point
   picking program (one material per row) behind `IWorldQuery` for look-ray
   selection.

Exit: `proof.cs editor-edit` places, drags (coalesced to one journal entry —
asserted via `world.status` dirty count), undoes, and re-selects by look-ray;
capacity rejection paths asserted.

### P4 — Live-apply and grants closure

1. `ReconcileCameras` — camera rows edit live (place/aim a jumbotron from
   the editor camera pose); "next boot" narration deleted.
2. The exclusivity-acquisition exemption + `GrantSubjectKind.Profile`;
   editor UX surfaces grant denials and exclusive holds in the HUD.
3. Defaults-vs-live presented as distinct HUD operations per D7.

Exit: `proof.cs editor-cameras` upserts a camera and asserts the jumbotron
view updates without restart; `proof.cs grants` grows exclusive-edit and
profile-subject rounds.

### P5 — Creations and placements in World

1. The `creations` + `placements` sections, four mutation records, validator
   rules, envelope headroom budgets, loud over-budget narration.
2. Placement stamping UX: place-by-name from the creations list (ghost
   preview → commit), transform via the P3 drag channel, repeat/mirror
   facets as data.
3. Client-side timeline replay for animated placements (Companion pattern);
   the placement row carries the nullable drive/role field for the future
   body rung.
4. Import path: `editor.import <path|cas-ref>` inlines a creation into the
   document (CAS as authoring cache).

Exit: `proof.cs placements` authors a furnished world over stdin — import,
stamp, drag, save, reload, byte-stable; an animated flagship creation walks
its timeline in-world.

### P6 — The creation sub-editor: sculpting in World

1. The sculpt model (a `Puck.Authoring` descendant of `CreatorScene`'s
   feature set: primitives, blends, palette, frames, chains/IK — IK stays
   float host-side math by design) editing a creation in a workbench
   context: local `EditHistory` ring at frame rate, orbit camera, sculpt
   chord pages.
2. Commit = normalize + hash + one `UpsertCreation` mutation (and the
   world journal picks it up from there); live placements of the edited
   creation refresh on commit.
3. The preview easel is a diegetic screen fed by the offscreen overlay/
   render variant — the first composed diegetic surface, proving the D2
   screen-feed seam.

Exit: `proof.cs sculpt` sculpts a creation from nothing over stdin, commits,
stamps it, saves the world, reloads, and the creation round-trips losslessly
(including previously-unauthorable carried fields).

## 3. Deferred ledger (explicitly out of this arc)

- **Walkability/collision/bounds** — own arc; sim-engine seam
  (fixed-point walk grids vs `MotionModel` reconciliation).
- **Addon live remount** — needs `AddonHost` per-instance teardown proof.
- **Per-principal undo** — data on the existing Undo op (principal filter),
  but selective-replay conflict semantics need design; global undo +
  "Mutate over every section" gate is the honest v1.
- **Row-level grant subjects** — until concurrent same-section editing is a
  demonstrated need.
- **Proportional text element kind** (per-glyph quads; `TextLayout` on GPU)
  and non-Latin glyph paging.
- **Creation-as-avatar-costume** — the avatar-catalog rig-source seam.
- **Bake in World / sculpt→cart→screen loop** — composes from existing
  seams when wanted; no schema change required.
- **Diegetic mirrors of the bar/console** (the `DiegeticUiDirector`
  descendant) — Tier-2 immersion polish after the overlay floor exists;
  the SdfVm glyph-op seam is already library-hosted.
- **Autosave tier** — a periodic journal-preserving sidecar save distinct
  from deliberate `world.save` (game-studio §4's "autosave recovery that
  never replaces deliberate published saves"); design wants the storage
  seam's version tokens; not editor-blocking.

## 4. Owner-decision flags

Per game-studio's working rule (ask when a choice changes the durable
document model, CAS boundary, multiplayer rules, or public authoring
workflow), three D-rulings above are owner-overridable cheaply if flagged
early: **inline-canonical creations** (D6 — the CAS boundary), **defaults
stay asymmetric** (D7 — two-line hook either way), and **global-undo-only
v1** (D1/deferred — per-principal filter is additive data later). The plan
proceeds on the rulings as written.

## 5. What dies (never ported)

Demo's hand-rolled chord trackers (`CreatorController`/`WorldSculptController`
page machines), the hardcoded binding-bar legends, the separate screen-wiring
table, placement role strings (`cabinet:<n>`/`eye`/`screen` collapse into
real Screen/Camera rows), the daylight dial as document data, profiles.v1
remnants (`BindingProfileDocuments`/Store), `GamingBrickPadService` (grants
superseded it), the three Demo overlay nodes (die with Demo), and the CA1506
forwarder-store workarounds (Demo analyzer-budget artifacts, explicitly not
patterns to carry).
