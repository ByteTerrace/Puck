---
name: sdf-world
description: Working on the SDF VM and world renderer — src/Puck.SignedDistance (SdfProgram/SdfProgramBuilder, the packed instruction ISA, and Puck.SignedDistance.Queries' deterministic fixed-point interpreter behind Puck.Maths' IWorldQuery/IFieldEvaluator seams) and src/Puck.SdfVm (SdfWorldEngine/SdfEngineNode, the Assets/Shaders/Sdf kernels, the shared render assembly SdfWorldRenderSpec/SdfWorldRenderBuilder, the Puck.SdfVm.Debug inspection engine, the composition/anchor surface (ISdfSceneEmitter/SdfCompositionFrameSource/SdfAnchor, wrapping Puck.SignedDistance's SdfMaterialScope), and Puck.SdfVm.Views (ViewStack/camera rigs)). Use whenever touching the SDF ISA or packed word layout, the world kernels or their HLSL includes, engine capacities/frames/screen sources, render-assembly/backend selection, the SDF debug/gallery/bench tooling, composing a world program from emitters, anchor/view/camera-rig plumbing, deterministic world queries, or debugging world-render parity or GPU cost. Carries the C#↔HLSL contract pairs and settled engine semantics so they aren't re-derived or accidentally forked.
---

# The SDF world: one contract, two languages

Bounded flow/cloud media share a 64-entry frame budget. Keep
`SdfVolume.VectorsPerEntry` (eleven `float4` rows), `PackVolumes`, and
`shade-volumes.hlsli` aligned: family at 5.z, ramp at 6–9, cloud coverage and
softness at 10.xy. Both the sky and views passes integrate volumes. The shader
stops at the zeroed trailing bound and composites intersecting volumes in
far-to-near entry order, resolving equal entries by descending index, without
capacity-sized per-pixel arrays. This is whole-volume compositing, not a
combined-density overlap integral. `world.budget` reports submitted volumes;
verify sixteen jets in an eight-body scene before accepting its measurements.
See the authoring README's bounded-volume section for controls and limits.

For scoped material composition, `mapCore` must save/reset/restore
`sdfMaterialBlendWeight` and `sdfMaterialBlendOther` alongside distance/material.
A losing scope must not tint the parent; a winning hard-union scope must retain
its internal seam. Verify both cases with contrasting scoped materials against
an unrelated ground surface. The two-material outer-seam behavior is described
in `docs/rendering/sdf/materials-and-primitives.md`.

Factual and procedural only: settled contracts, their exact sync points, and
how to verify. The user's current instruction outranks it — if this file
argues against a demanded change, it is stale; update it in the same change.
The render-assembly reference that used to describe its boundary, capacity
envelope, content seams, and unsupported graph requests is deleted and has no
replacement — read `SdfWorldRenderSpec`/`SdfWorldRenderBuilder` directly.

Creation/shape AUTHORING — including `.puck` shape/prototype/placement sugar
(e.g. `src/Puck.World/Assets/worlds/avatars/moth.puck`) — is `sdf-authoring`'s
territory, not this skill's. `puck.creation.v1`/`puck.sdf.v1` are cited below
only as wire formats this ISA reads and writes, never as an authoring path.

World static-placement headroom must cover both emission classes. A scoped
creation has one instance; a scope-free creation has one per shape, reserved
at `CreationStampEmitter.PerCopyInstanceCount` probe chains per copy — a
panelled shape charges two, since its one instance carries a second transform
chain and shape. `WorldSceneEmitter` reserves each headroom copy in both
independent probe floors, using `WorldPlacementPolicy.MaxShapesPerStamp` for
the per-shape floor. Keep construction probing and `ComposeCandidate`
accounting aligned. Verify through `WorldRenderEnvelopeLawTests` and
`ShapePanelLawTests`' probe laws and a rendered live placement addition, since
headless admission has no GPU capacity lease.

> **Unification-contract alignment** (see docs/decisions/engine-design.md): world content is
> authored and loaded in-session — the `world.row.set`/`world.row.step`
> document-row mutation verbs and `world.load`/`world.save` — never only
> through a CLI flag. `Puck.World` has no content-authoring flags at all.

> **`Puck.Demo.*` symbols below are recorded history.** `Puck.Demo` is
> deleted; its tree is readable only from git history, never from a live
> checkout. The contracts stated here were accurate when written and are kept
> because they explain why the engine seams have the shape they do. They are
> not live collaborators, they are not available to call, and **nothing plans
> re-homing them into `Puck.World`** — the port plan that would have was
> deleted. Where this file names a `Puck.Demo.*` type as the only
> implementation of something, read that as: the capability is absent from
> the running product.

> **ISA admission rule (owner-ratified 2026-07-12).** An op or shape earns a
> switch case ONLY if it cannot be composed EXACTLY from existing vocabulary —
> otherwise it ships as a builder macro emitting existing ops. Ratified but
> never executed, and the SDF backlog that tracked it was deleted 2026-08-02, so
> nothing schedules this now: `Star`/`RegularPolygon` retire
> into `RepeatPolar`-based
> builder macros; `Ellipse` STAYS as the one exact-curve citizen (`Ellipsoid`
> #6 remains the approximate path); shapes join ops on the compiled
> kernel-variant axis so unused vocabulary costs no register pressure.

A named child render node produces and prepares once per host frame even when
several view slots reference it. The first slot supplies its render extent;
the compositor reconstructs that image into each destination slot. Keep
`BuildCompositePush` word 3 (`childMask`, formerly padding) aligned with
`CompositeParams2.childMask`: child sources use `GetDimensions`, while SDF
sources use the valid region derived from render scale. Equal source and
destination extents retain the exact-copy path. Child images must expose an
RGBA8 storage view in General layout, including pipeline intermediate previews.
An asynchronously compiling child may return an empty surface. Clear that slot's
child bit for the frame so the compositor uses its initialized SDF source;
never bind a zero image view while waiting for the first candidate.

## The C# ↔ HLSL sync pairs

The C# ISA and the shader ISA are ONE contract, kept in `references/sync-pairs.md`
— read it before changing an op, shape, lane, packing rule, or any HLSL kernel that
decodes the SDF ISA; change either side only with its partner in the same change.

`SdfProgram.FieldScopeClamps` reads the non-unit scales already baked into
`PopField.Data1.y`, with push/pop indices, instruction owner, and shape count.
It does not change packed words. `StepScaleBinder` only identifies an unscoped
shape-chain bound; global `StepScale == 1` does not mean scopes are unclamped.
`SdfEngineNode.LiveProgramFieldScopeClamps` follows the uploaded program.
`world.budget` reports scoped/shared counts and the worst scope;
`puck creation stats` reports static/pooled unit-scale rest geometry through
the live stampers, or explicitly marks text-atlas-dependent inspection
unavailable. A warp sharing its enclosing scope remains valid; panel/trims/
cells refusals protect field isolation. Clamp factors are field bounds, not
measured march or GPU-time multipliers.

> **Packed field-scope admission.** The public `SdfProgram` constructor enforces the builder's one-deep balanced
> `PushField`/`PopField` structure and refuses a scope that crosses between the world stream and an instance-owned
> slice. It also validates the packed material, screen-surface, and instance-bound tables; instruction-lane
> finiteness alone does not cover those GPU inputs.

> **Wallpaper groups (verified by direct point-group measurement).** `sdfWallpaperFoldCell` realizes all 17 IUC groups.
> `SDF_WPG_CMM`'s half-turn must run AFTER the sign pair (before it, the pattern is `pmg`). `SDF_WPG_P6` is a C6 sector
> fold about the hex centre, NOT P3's 3-colouring turn plus a half-turn (k(-h) = -k(h) kills the central inversion,
> collapsing it to `p3`). `SDF_WPG_P4G` does NOT ride the parity turn-cocycle (that offset mirror sits at a half-cell the
> parity key can't see, and it collapses to `p4`); it folds DIRECTLY to a fundamental wedge — a sign-based C4 reduction
> about the cell centre, then one reflection across the offset diagonal `x + y = cell/2` (through the 2-fold centres, off
> the 4-fold centres). Point group at the centre is C4, zero through-centre mirrors — the signature separating p4g from
> p4m; no live gate today — quarantined `Puck.Post` historically ran `world-wallpaper-p4g` (single-cell translation invariance, period-1 not period-2). For every
> parity-keyed group (P2/PG/CM/PMG/PGG/CMM/P4/P4M) and for P3, the authored `cell` is the HALF-period: the pattern's
> translation lattice is the centered/doubled cell (or the √3×√3 hex supercell for P3). `SDF_WPG_P4G` is the square-group
> exception — its period is exactly `cell` (a pair of opposed 4-fold centres composes to the unit translation).


> **The accumulator rule (blend composition).** `mapCore` carries ONE running nearest-surface distance for the whole
> program; `SDF_OP_RESET` resets the evaluation POINT, never `result.distance`. So the union family (a `min`) and the
> subtraction family (a `max` against the NEGATED candidate, which only bites inside the subtrahend) are LOCAL and may
> be emitted anywhere, while the INTERSECTION family is not: `max(accumulator, candidate)` returns the candidate
> everywhere outside its own shape, annihilating every earlier shape it does not overlap. Author an intersection pair
> FIRST, against the empty accumulator. (`WorldChamferStage` was emitting its `ChamferIntersection` last and rendering
> a lone wedge on empty sky while claiming three clusters — its 2-pixel cross-backend diff was the tell.)
> That unbounded influence region is also why `SdfProgram` packs an instance carrying an intersection-family blend with
> `UnmaskableBoundRadius`: no cull bound can contain it, and a parked one throws. No live gate today — quarantined
> `Puck.Post` historically ran `world-instanced`'s intersection guard (its scene authors a deliberately under-covering bound the packer must override; note a merely
> tight-but-covering bound hides the bug, because the beam cone-marches the UNMASKED field and empties exactly the
> tiles where the mask would matter).
>
> **Severity is a property of the op (why this bug class hides).** Intersection is LOUD — it annihilates every earlier
> shape it doesn't overlap, ground plane included; a 2-pixel diff catches it. The field ops (`Onion`/`Dilate`/
> `Displace`) are SILENT: `abs(d)−t = 0 ⟹ d = ±t` moves the OUTER surface outward by `t`, so every earlier solid
> quietly grows and goes hollow — it reads as "a slightly larger object" and no gate ever tripped on it. Weight review
> attention accordingly. Corollary: **the forge/bake path is safe by construction, not by care** — a single-object
> program's accumulator IS the object; the hazard begins the moment a program gains a floor or a second object.
> (Evidence history: docs/sdf-accumulator-plan.md, retired 2026-07-09 — see git history.)
>
> **A subtraction is a bound in its own void.** `max(a, −b)` is the exact distance only where the subject `a` is the
> nearest solid; inside the carved void, wherever `−b < a`, it returns `a` — the subject's carved-away face — and
> wherever `−b` wins it returns the distance to the CARVE'S OWN boundary, with the carve's inward gradient. Rendering
> never sees either (no zero crossing), but the CONTACT field (`SdfFieldEvaluator` mirrors the same blend tail) reads
> them as surfaces: a body grounds on a carve box's bottom face and on a phantom lip about one radius wide inside the
> rim, and `world.collision.probe` shows the tell (a positive distance with an up gradient in open air). Author a
> carve to extend past every point a body can reach in the void (`puck.world.json`'s `pit` runs from below the safety
> net to above head height), or build the void from union geometry when it must be exact.
>
> **`Xor` is EXEMPT — maskable-exact with a covering, union-margin bound (settled 2026-07-08, real-GPU slice
> comparison).** `max(min(acc,b), -max(acc,b))` reduces to `min(acc,b)` ≡ plain union everywhere OUTSIDE the candidate
> (`b > 0`) — the `-max(acc,b)` arm only wins when `acc + b < 0`, deeper inside than a first-hit march ever samples —
> and the extra surface Xor carves (the overlap hole) lives strictly INSIDE the union hull, so inside any covering
> bound. Masking an Xor instance out of a tile is therefore exactly as safe as masking a union member.
> `HasUnmaskableCompose` deliberately omits Xor and `MaxSmoothBlendRadius` gives it zero halo — correct by design; do
> NOT "fix" Xor into the unmaskable gate. SIZING rule: an Xor member competes on the running `min` wherever it is
> nearest, so its cull bound needs the UNION-style generous influence margin (the `world-instanced` 4/5-unit pattern),
> never the subtraction-style tight bound.

## Engine semantics (settled)

- **Capacities freeze at construction**: program word count, instance-mask
  width, dynamic-transform slots. `UploadProgram` REJECTS a program exceeding
  any of them (loud `ArgumentException`). A hot-swapping frame source declares
  its envelope up front: `SdfWorldEngineOptions.ProgramWordCapacity` /
  `InstanceCapacity` / `DynamicTransformCapacity` (floors, maxed with the
  initial program) — mirrored as `SdfEngineNode` ctor params and as
  `SdfWorldRenderSpec.ProgramWordCapacity`/`InstanceCapacity` in the render
  assembly (the overworld feeds them from its probe — see below).
- **`UploadProgram` is the single owner of per-program state** (buffers, live
  mask width, required dynamic capacity); the constructor calls it. Never
  duplicate its assignments elsewhere.
- **Strict frame contract**: `frame.DynamicTransforms` must supply at least
  the program's `RequiredDynamicTransformCapacity` entries or the frame
  THROWS — empty is valid only for a program with no dynamic slots. A dynamic
  slot silently rendering at identity is a bug, not a default.
- **`RenderFrame` vs `SubmitFrame`**: submit-and-wait (harnesses/readback) vs
  fire-and-forget (the live node; host pacing orders frames). Never blur them.
- **Two content seams, don't conflate:**
  - A **child** occupies a viewport slot (childMask; beam/Stage 1 skip it; the
    compositor copies its surface). `SdfWorldRenderSpec.Children` is keyed by
    NAME (`IReadOnlyDictionary<string, IRenderNode>`), never a fixed slot
    index: each frame's `SdfViewSnapshot.Child` (null = an SDF camera view)
    names which registered child fills that slot, so the same name can sit at
    a different viewport slot on a later frame as `SdfFrame.Views`' own order
    moves. `SdfEngineNode` derives which slots are child slots EVERY produced
    frame from that frame's bindings resolved against the registered names
    and hands the engine the mask (`SdfWorldEngine.SetChildMask`, before its
    `SetChildSource` calls) — a layout switch can turn any slot into a child
    or back, so the engine allocates an SDF source texture for every slot. A
    name the map lacks takes the ordinary SDF camera path for that slot
    instead of throwing (`SdfEngineNode.HasChild` is the read-back a caller
    uses to tell the two apart, e.g. `Puck.World`'s `world.view.state` echo);
    `SdfEngineNode.RegisterChild` adds a child after construction (pump
    thread only). The document-side authoring seam (a `views.layouts` slot's
    `pipeline` field naming a `views.pipelines` row) is `puck-world`'s to describe.
  - A **screen source** is program-declared `ScreenSlab` shading: its lit face
    samples the bound image through a CRT glass treatment (barrel curve, rounded
    bezel, scanlines, vignette, fresnel glint, bloom — `sampleScreenSurface`),
    and each bound screen also emits colored light into the room — its per-frame
    framebuffer average (`SetScreenLight` → the binding-11 `sdfScreenLights`
    buffer) summed with the sun in the `renderView` shade loop (≤4 screen
    lights), with `AmbientScale`/`SunScale` dimming the room for the overworld
    mood.
  - Polling order: screen providers AFTER children produce; light providers
    (`SdfEngineNode.screenLights`) right after.
  - `SetScreenSource(i, 0)` (a provider returning 0) UNBINDS the slot: the
    face falls back to the flat/procedural screen material — the animated
    test-card, a striped no-signal look, NOT black. A screen going black is a
    different bug (dead image, zeroed screen light), not a cleared source.
- **Per-frame screen-feed seams on `ISdfFrameSource` (both default no-op)** —
  an engine capability handed to the source, not a host-shaped hook (they mirror
  `AdvanceBricks`):
  - `PrepareScreenSources(deviceContext, gpu)` fires right AFTER `CaptureFrame`
    and BEFORE the host polls the screen-source providers — the seam a source
    that feeds a screen from CPU pixels uses to upload THIS frame's image to a
    stable handle its provider then returns (test pattern, webcam, window grab).
  - `RenderViews(in FrameContext)` fires right AFTER `PrepareScreenSources` and
    BEFORE the provider poll — the seam a source hosting its own offscreen
    `ViewStack` (diegetic camera / nested-world renders — the jumbotron) uses to
    render those views against the live device, so a provider returning a view's
    handle reads a freshly-rendered image. Distinct from `PrepareScreenSources`
    because a view render resolves its OWN device from the frame context's host
    and renders the same world program the host is composing.
  - Per-frame order once the engine exists: **`EnsureEngine` →
    `PrepareScreenSources` → `RenderViews` → screen-source provider poll** — so
    both feeds have published this frame's images before any provider is read.
- Dynamic-slot bound: `SdfProgram.MaxDynamicTransformSlot` = int.MaxValue−1
  (`slot+1` must fit); the float-lane decode compares in DOUBLE because
  `(float)int.MaxValue` rounds up to 2³¹.

## Composition, anchors, views, and queries (SDF VM Worlds arc, 2026-07-10)

Pure C# — no HLSL counterpart (this layer assembles/consumes programs; it
does not extend the ISA). Landed across Waves 1-6 of the SDF VM Worlds arc.

**Composition (`Puck.SdfVm` root).** `ISdfSceneEmitter`/`SdfEmitContext` is
the composable content contract — a room's fixed geometry, a sculpted scene,
an authoring pool, or a debug takeover all become ONE list item instead of
one hand-written `BuildProgram` method.
`SdfCompositionFrameSource`/`ISdfFrameDresser` composes a fixed emitter list
into one `ISdfFrameSource`: contiguous dynamic-transform slot assignment
(`SdfEmitContext.SlotBase`), a single construction-time worst-case capacity
probe combining every emitter's `Probe` branch (the SAME probe-contract
doctrine the overworld's own probe already followed — every optional
emission needs a probe branch or a live rebuild can outgrow the frozen
envelope), and rebuild-on-revision-change
(`ISdfSceneEmitter.RevisionComponentCount`/`WriteRevision`, compared
COMPONENTWISE — never summed, never hashed: some counters are assigned rather
than incremented and can move DOWN, so any addition on that path can cancel
and hold a stale program). `ISdfSceneEmitter.OwnsMaterialScope` (true for a positional-stride
author — `WallpaperFold`/`RepeatPolar` with `materialStride`) tells the
composition host to wrap that emitter's `Emit` in a
`SdfProgramBuilder.BeginMaterialScope()` scope (`SdfMaterialScope`), clamping
any positional reach to the emitter's OWN added materials instead of leaving
it to author discipline (the `SdfDriftMonolith` hazard the scope mechanism
was built to close). `Puck.World.Client.WorldFramePresenter` (the split-out
successor to the retired `OverworldFrameSource`) is wired onto
`SdfCompositionFrameSource`, composing `WorldSceneEmitter`/
`WorldSdfDocumentEmitter`/`WorldAdjacencySceneEmitter`; `SdfDebugEmitter` (in
`Puck.SdfVm.Debug`) is the debug-takeover emission core.

**Anchors (`Puck.SdfVm` root).** `SdfAnchor` (position + orientation
snapshot, `System.Numerics` float) / `ISdfAnchorSource` (the read seam) /
`SdfAnchorTable` (the sim-side per-tick registry: `BeginTick`/`Publish` key
on NAME not insertion order, so a name that stops publishing stops resolving
without ever being reassigned) / `SdfAnchorKind` (World/Body/Instance — the
engine-side classification a host's own anchor kinds map onto). **Float
verdict: PRESENTATION, not simulation state.** An anchor is
published FROM an already-computed sim pose (a `FixedVector3`/`FixedPosition`
position converted to `Vector3` once at publish time) and its only consumer
is `Views.SdfCameraView.Resolve` (a camera rig pose) — nothing reads
`TryResolveAnchor` back into sim state. Safe by the same rule that makes
`ViewStack`/`ViewTransition` presentation-only.

**Views (`Puck.SdfVm.Views`).** `ISdfCameraRig` + the fixed shapes — `OrbitRig`
(shared `Offset(yaw, pitch, distance)` static, the trig every object-intent
camera in this codebase used to hand-roll), `FollowRig`/`OrientedFollowRig`,
`FixedRig`, `FirstPersonRig` — plus the PROGRAM path, which is the one a host
with authored cameras uses: `SdfCameraProgram.cs` carries the IR
(`SdfCameraOp`: anchor/offset/lookAt/orbit/dynamics/clampPitch/fov/blend,
`SdfCameraProgramSet` as the blend namespace by INDEX), the allocation-free
`SdfCameraProgramEvaluator`, the `SdfCameraProgramRig` adapter whose
`Subjects`/`Scalars`/`Look` buffers a host refills per frame, and
`SdfCameraBoomFollower` — the pole-matched second-order boom ease over
`Puck.SdfVm.Views.SecondOrderFollower3` (see `Puck.SdfVm/README.md`). Every
number an op reads is an `SdfCameraScalar` —
a literal or a per-frame slot — so a host's authored bindings resolve OUTSIDE
this library and nothing here parses a document. `ViewStack` — the
hypervisor-identity primitive that absorbed `CameraFeedPool`: `IViewContent`
(`SdfCameraView`/`WorldSessionView`) registers by NAME
(`ViewId`/`Register`/`Release`/`Resolve`/`ResolveGlow`/`IsLive`), budgeted
round-robin refresh (`OffscreenRenderBudget.RegisteredViews` = 64 live,
`Puck.Abstractions.Presentation.OffscreenRenderBudget.PerProducedFrame` = 4
rendering per frame — the same leaf the world validator caps unbudgeted window
sessions with — deterministic cursor, never wall-clock), and the
SELF-REFERENCE rule (`SetWiredScreens`: inside view V's own render, any
screen surface wired to V binds 0, so a wall of monitors never compounds
frame over frame; one-frame-lag TV-in-TV chains through a DIFFERENT view
stay legal). `SdfCameraView` export has one persistent image: an asynchronous
foreign reader must wire `TryBeginExportWrite`/`EndExportWrite`, so `Resolve` holds the
last completed image while that reader's lease is live rather than overlapping
a full-image read and write. `ViewTransition`/`ViewLayout`/`ViewBinding` — eases a
`ViewStack` composition between two layouts: the REGION lerps continuously,
the VIEW occupying it is a hard cut at the eased midpoint (0.5). Float
verdict: presentation (an `elapsedSeconds` render-clock parameter the caller
advances deterministically, same shape as `ScreenLayoutDirector`'s existing
pane easing — not simulation state). `ScreenSlotPriority` orders views
informationally; a screen-SURFACE slot claim is the separate
`Puck.World.WorldScreenBinder` arbitration.

**Queries (seams in `Puck.Maths`, providers in `Puck.SignedDistance.Queries`).**
`IWorldQuery` and `IFieldEvaluator` are declared in the numerics layer — they
name no representation, so a field's producer and its gravity/contact/wind
consumers sit in sibling libraries that never reference each other
(`Puck.Physics.FixedFieldContactSolver` is the contact consumer). `IWorldQuery`
(`Raycast`/`SphereCast`/`Overlap`/`TryGroundHeight`/`LineOfSight`) — fully
`FixedQ4816`/`FixedVector3`/`FixedPosition`, synchronous, every result tagged
with a `WorldQueryConfidence` (`Bounded` = baked/quantized, `Exact` = a
live-program CPU evaluator). TWO providers now ship. `WorldQueryArtifact`
(a `puck.worldquery.v1` CAS-blob-shaped heightfield + blocked bitmap,
in-memory only — no document/CAS reference yet) baked by `WorldQueryBaker`
(float-authored rectangles in, deterministic artifact out — the
quantize-once-per-edge discipline: every rectangle edge snaps to raw Q48.16 exactly
once via `FixedQ4816.FromDouble`, every per-cell loop after that is pure
integer arithmetic) and read by `BakedWorldQuery` (pure fixed-point,
generalizing `FixedWalkGrid`) via `WorldQueryProviders.ForWorld` —
`Bounded` answers. **`BakedWorldQuery` never point-samples a segment.**
Every verb enumerates the cells the swept volume can reach — column by
column in sweep order, the row span per column derived from the segment's
own linearity — and intersects the segment with each cell box
analytically (slab test, entry parameters floored and exit parameters
ceiled so truncation can only widen an interval). `Distance` is the
CENTER's travel; `Point` is the contact on the geometry. A swept sphere
tests each cell box dilated by the radius per axis, an axis-aligned
dilation containing the true rounded-rect sweep, so a cast can report
contact up to `radius·(√2−1)` early at a corner — `Overlap` uses the exact
Euclidean clamp-to-solid test and is the tighter of the two. Every position
argument is REBASED against the world origin via `FixedPosition.TryDelta`,
exactly as `SdfFieldEvaluator` rebases its query point — the grid's origin is
a world coordinate, so reading `.Local` would alias every 2^20-unit cell onto
the same grid; a position outside signed Q48.16 of the origin is refused by
parameter name. A radius spanning more than `BakedWorldQuery.MaxRadiusCells`
of the artifact's own cells (`MaxRadius` world units) is refused by name from
`Overlap`/`SphereCast`: the cell walk is quadratic in the radius and there is
no occupancy hierarchy, so a consumer needing wider IS the request for one.
`SdfFieldEvaluator.Overlap` resolves a failed world-origin rebase toward occupied
when the program has geometry; unlike the field-only `TryDistance` seam, this
authoritative obstruction verb may not turn an unrepresentable point into clear.
The artifact's
`HasBlocked`/`HasHeightfield` describe CONTENT (scanned once at
construction), not allocation, and its ctor refuses a layer whose length
contradicts the grid, trailing padding bits addressing cells outside it, and an
origin/dimension/cell-size combination whose far edge leaves signed Q48.16 —
reachable from the public ctor's raw longs, unreachable from the baker's float
bounds, so the baker carries no copy of that check.
A blocked cell blocks at every Y (no height in
`WorldQueryBlockerInput`); the heightfield is the half-space at or below its
authored ground; BOTH layers answer EVERY verb, `Overlap` included.
`WorldQueryBaker` refuses by name a grid bound or terrain height the Q48.16
carrier can only saturate (a `float.MinValue` height quantizes to exactly
`NoHeightSentinel` and would erase the cells it authored), a grid spanning more
cells than a 32-bit cell index addresses, and a bake above
`DefaultMaxCellCount` unless the caller supplies a different explicit budget. The baker transfers its newly allocated layers into
the artifact rather than cloning them; public artifact construction still
copies. `SdfFieldEvaluator` (GRAVITY ARC Wave 1) wraps a LIVE
`SdfProgram` directly — `Exact` answers; see its sync-pair table row above
for the interpreted subset, the excluded-ops reconciliation, and the
measured tolerances. `Puck.Maths.IFieldEvaluator`
(`TryDistance`/`TryFieldGradient`) is a SEPARATE, narrower interface
`SdfFieldEvaluator` also implements — the field-only seam a gravity/
magnetism/wind/contact consumer binds instead of the five-verb `IWorldQuery`.
It sits in the numerics layer so a field's producer and its consumers can be
sibling libraries that never reference each other;
`BakedWorldQuery` does NOT implement it (capability checked via
`FieldEvaluatorCapabilities`, never stubbed). `SdfDistanceGrid` holds the exact
evaluator's values at the corners of authored-size cells (8×8×8 blocks filled
on first touch) and proves `nearest corner − Slack` as a lower bound anywhere,
Slack = L·cell·√3/2 plus rounding; `SdfBandedFieldEvaluator` reads the exact
evaluator through it — exact below its `Band` (contact reach + slack), the
bound above; gradients always exact; `Overlap` identical everywhere; the
verbs march `SdfFieldMarch`, the one sphere-trace loop both evaluators run,
over samples that are exact wherever the exact march could accept or stop.
A march that stays in the band is bit-identical; one that crosses the bound
region differs only in its steps, so its converged hit lands elsewhere
within the accept threshold. `WorldSolidField` bakes it when
`collision.gridCellSize` is authored; the band is the largest kit collider
extent at the scale row's ceiling plus the skin. **`Puck.SignedDistance.Queries.Debug`:**
`WorldQueryDriftInstrument` measures the evaluator's answers against two
INDEPENDENT channels outside an epsilon-shell exclusion around its own zero
set (a near-surface point is not a fair sign test for any coarser
representation) — a GPU render (a sphere-trace invariant: a march can never
accept a hit closer than the field's true value at its origin) and a
`BakedWorldQuery` cross-check sourced from the evaluator's own samples (a
query-PLUMBING consistency check, not a field-math one).
`BakeGroundHeightArtifact` measures the grid first, walks it by CELL INDEX — a
float bound incremented by `CellSize` stops making progress once its ULP reaches
0.5 — and hands one terrain rectangle per sampled cell to
`WorldQueryBaker.Bake`. The write-side cell index is therefore the BAKER's, so a
fencepost there disagrees with `BakedWorldQuery`'s read instead of moving with
it; an instrument that wrote the artifact itself agrees with itself whatever the
baker does. Each rectangle is degenerate at its cell's CENTER (half a cell from
the boundary the baker's floor/ceil split, where one float rounding would claim
the neighbour), and a region whose coordinates are coarser in float than
`CellSize` is refused by name rather than baked into silently mis-addressed
cells. Historically backed two `Puck.Post` stages, both measured-first and frozen at that measured
reality, never tightened unasked — quarantined now, with no live equivalent:
`world-field-evaluator-determinism` (Tier A — three
independently constructed evaluators over a fixed program/point set hash
BIT-IDENTICAL) and `world-field-drift` (Tier B — measured 403/403, 100% GPU
sign agreement outside a 0.75-world-unit shell, held at exactly 1.0 since the
sphere-trace invariant PROVES it structurally, not just observes it; measured
496/500, 99.2% baked ground-height agreement, frozen at 0.98 with headroom).

## Render assembly (Puck.SdfVm)

`SdfWorldRenderSpec` + `SdfWorldRenderBuilder.Build` — in `Puck.SdfVm`
root namespace — own EVERY
backend-specific choice from one `HostsOnDirectX` field: kernel bytecode
extension (`.spv`/`.dxil`, resolved via `SdfWorldKernels.Load`'s one-arg
default now that the Builder no longer threads a caller-supplied directory),
child `directX` flags, and the `DecorateFrameSource` seam
(`Func<ISdfFrameSource, ISdfFrameSource>?`) — an optional in-place decorator
the Builder applies to `spec.FrameSource` before building the engine node,
identity when absent. The Builder itself never names a host type, and no
live caller currently sets `spec.DecorateFrameSource`. A caller never names a
bytecode extension.
`GraphBuilder.UnsupportedReason` WAS the one owner of the world graph's
deferred rejections (cross-backend `produce`, `live-camera` pending its
child node) — pre-flighted in `Program` BEFORE the window host built, so
rejection was an attributed stderr line and exit 2, never a mid-host crash.
It RETIRED with `Puck.Demo`'s composition root, so the pre-flight side has
no live owner today; the doctrine waits for the next graph-building host.

**The capacity probe (the envelope pattern, live in `Puck.World.Client.WorldFramePresenter`).**
Composing `SdfCompositionFrameSource` runs ONE worst-case probe across its
emitters — every diegetic screen lit, the largest catalog rig in each detailed
body slot, one coarse capsule in each remaining supported body slot, the
reserved placement instances, and the worst-case animated pool — measures it
(the probe is never rendered), and feeds the result through
`SdfWorldRenderSpec.ProgramWordCapacity` /
`InstanceCapacity`, so live rebuilds vary freely BELOW the frozen envelope.
The hybrid body probe is a storage and SDF-input bound, not evidence of a
dense-crowd frame-rate target; that target requires rendered GPU evidence and,
where per-creature SDF instances miss it, a different presentation lane.
Any NEW optional emission MUST also declare a `Probe` branch on its
`ISdfSceneEmitter`, or a live rebuild can outgrow the buffers and
`UploadProgram` throws loudly.

Catalog bodies use one dynamic cull instance per leaf, tracking its bone slot.
The bound encloses that primitive at the selected scale through any orientation
and gait phase. Authored leaf offsets are unscaled, so their radius is added
after scaling the primitive reach. Keep the catalog's `PrimitiveReach` paired
with its emitted dimensions. Body-wide or multi-leaf grouping requires fresh
GPU evidence: fewer mask entries can admit far more segments to each tile.
Instance count and reserved dynamic-transform capacity are distinct resources;
inactive bodies and smaller looks do not shrink their reserved bone ranges.

## Shader build mechanics

Kept in `references/shader-build.md` — read it before touching DXC build steps,
kernel variants, pass labels/timing, or descriptor/register wiring.

## Gotchas (verified, expensive to re-learn)

- **Silhouette coverage requires observed background visibility.** The deferred
  views pass checks cardinal neighbors in the completed primary cache before
  blending toward sky. Check the beam's current-frame `TileEmpty` first: records
  outside the indirect dispatch bbox can be stale. Bound neighbors to the
  current viewport's reduced render extent, and exclude exhausted rays from
  sky evidence. A local field rise does not prove sky behind grass or other
  foreground geometry. The monolithic reference omits this filter. See
  `docs/rendering/sdf/shading-ao-shadows.md` for the weight and verification cases.

- **Sweep cull spheres must include the field's subtractive margin.**
  `SdfProgram.Sweep.cs` encloses the control-point hull, maximum positive profile
  radius, strand orbit and margin. The result bounds the uncapped candidate for
  any closest-t choice. Admission requires margin <= 16 so the shader's 1e9
  strand seed remains 1e9 after subtraction; larger margins stay unbounded.
  All three sphere tests in both scalar and dual walks also require accumulator
  <= `SDF_FAR_DISTANCE`. Preserve both guards: geometric containment alone or
  ignoring the strand seed changes the field. Geometric chain reach stays separate.

- **Whole-part programs are a scalar execution path, not another cull.**
  `SdfProgram.PartPrograms.cs` compiles complete hard-union instance scopes with
  Reset, optional dynamic pose, optional single Scale/AxialProfile/Shear, and
  Shape chains. Internal shape blends remain ordered. Geometry keys include
  Detail/NoSecondary flags, domain parameters and polygon vertices, while pose
  slots and material IDs live in a separate binding run. Sweep and unsupported
  chains retain the original program, as does the analytic dual evaluator.
  `sdf-parts.hlsli` consumes the scalar plan at the instance merge boundary and
  advances past the entire owned segment range. Preserve hard-union ties,
  scope distance correction and the winning scope's internal material seam.
  The instance-directory header `.y` points to the appended part table (zero
  when absent); the table header carries compiled-instance, shared-program,
  shared-leaf and binding counts. Header `.x` bits 0..30 hold the instance count;
  bit 31 admits independent primary tracing. One uint4 per instance names its leaf run,
  binding run, leaf count (high bit = dynamic), and float scope correction.
  A leaf uint4 names canonical shape/domain instructions; a binding names pose
  slot+1 and material. Non-dynamic kernels retain dynamic parts' reference walk.
  Capacity probes use `PartCompilationWordCapacity`, not `Words.Length`: sharing
  and admission can change within the instruction/instance ceilings.
  `PartProgramLawTests` pins these packing and admission contracts; verify actual
  rendering against the reference kernels on both backends before claiming parity.

- **Independent primary tracing requires hard-union root composition.**
  `CanTracePartsIndependently` allows only root Union shapes/PopField and
  Reset/Translate/Rotate/Scale/TransformDynamic. Root field modifiers or other
  composes keep the full-scene march; eligible parts still accelerate scalar queries.
  `sdfCanTracePartsIndependently` decodes the admission for both beam and primary.
  Admitted programs use `IndependentConeMarchSteps` entry samples without gap/tail
  searches; other programs retain the full beam path. An exhausted entry search
  publishes its conservative depth and far-distance sentinels, never TileEmpty.
  Compare combined beam/primary work and inspect silhouettes and changed poses;
  an earlier start can increase primary queries without changing visible geometry.
  `sdf-primary.hlsli` shares the marcher between the remaining scene and each
  visible complete part, then resolves attributes with the full field at the
  nearest accepted sample. Independent selection uses `SdfPrimarySurface`, not
  the attribute-bearing result: retaining those unused fields made the measured
  primary pass slower despite unchanged primitive work. It never substitutes a
  leaf for its CSG parent.
  The beam refits per-view complete-part sublevel boxes in `sdf-part-bounds.hlsli`.
  `SdfWorldEngine.PartBoundFloatCount` / `SdfPartBoundFloatCount` are twelve floats
  per instance per viewport: two bands of six-float corners, appended after the
  four tile planes in `m_tileBuffer`. The first band covers primary acceptance;
  the second covers the raw-field 0.15 sublevel set for AO. Its parser also admits
  supported flat chains and complete uncompiled scopes. Unknown expressions stay
  unbounded. AO exclusions require an independent hard-union root and a per-rung
  scale-corrected ceiling within the cached band; otherwise query the full field.
  Root distance clipping must not initialize a child CSG scope. Each AO rung's
  contribution is nonnegative, so distant clearance cannot cancel closer contact.
  Allocate against construction capacities; shader offsets use live counts.
  Beam threads stride over instances when a viewport has fewer tiles than parts.
  Primary reads after the existing beam barrier, clips its local ray interval,
  and keeps the full interval for unsupported formulae. Include the footprint,
  scope/domain corrections and smooth-union slack; bare zero-set bounds are unsafe.
  Verify changed poses, multiple/small viewports and both backends when changing this cache.
  `sdfPrimaryOmitParts` is primary-local state and must be cleared before full-field
  attribute resolution or any other query path. Sample positions can differ
  within the existing footprint acceptance rule; this is not pixel-exact parity.
  Word 11 of the 80-byte hit record packs selected-march steps in bits 0..7,
  total primary queries (saturated) in bits 8..30, and hit in bit 31. Change producer
  and consumer together; query count is not primitive work because query sizes differ.

- **Use compiled-shader reload during iteration.** After `CompileShaders` finishes,
  `world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf` queues the primary
  node's next-frame reload; `world.shaders.status` distinguishes pending from
  applied/unchanged/failed. Do not reboot for HLSL-only edits. The engine stages
  changed pipelines, drains the frame ring, validates beam/primary/surface/ambient/full/core/fold ISA,
  then retires old pipelines; failure restores them. Scene buffers, textures,
  baked bricks, and world state survive; descriptor caches borrowed by the ISA
  probe, cadence, and shadow history are invalidated. The last successful set
  also survives device-loss reconstruction. Host ABI changes still need a build;
  child engines and overlay/decorator pipelines are outside this command.

- **The soft-shadow march is GRID-CULLED (default ON; `sdf.shadowcull on|off`,
  `SdfFrame.DisableShadowCull`).** `renderView`'s `softShadow` no longer marches
  the CAMERA-tile mask (the wrong occluder set for a ray that leaves the camera
  cone). Instead `sdfShadowGatherGroup` (sdf-world.hlsli, under
  `SDF_GROUP_SHADOW_GATHER` — the Stage 1 kernels) walks the SAME view-
  independent `SdfInstanceGrid` the beam cull walks, along the SUN ray, into ONE
  GROUPSHARED mask per 8x8 workgroup (`sdfShadowMaskWords`,
  `SDF_SHADOW_MASK_WORDS = ceil(SDF_MAX_INSTANCES/32)` = 2048 words, covering
  all 65536 instance slots, including reserved pools) that `mapMasked`
  reads via the `sdfShadowMaskActive` static. PER-TILE since 2026-09-03 (it was a
  per-lit-pixel gather into 32 per-thread registers): every lane publishes its
  hit point at the ONE uniform seam in `renderView` between the march and the
  epilogue, lane 0 reduces the lit points to a centroid + enclosing radius R, and
  the 64 lanes walk the grid cooperatively (always-list and cell boxes strided by
  lane, `InterlockedOr` into the mask) along the penumbra cone apexed at the
  centroid with every bound inflated by R + `ShadowBias`. SUPERSET of each
  pixel's own cone by a translation argument, so the masked march stays
  bit-identical to flat; the price is only the extra candidates a wide group
  (a silhouette group spanning near and far) admits. UNIFORM CONTROL FLOW is
  load-bearing: `sdf-world-views.comp` turns its render-extent return into an
  `active` flag so inactive lanes still reach the barriers, and the decisions
  feeding the gather (view mode, levers, reach) are uniform. Measured on the
  RTX 2060 shipped world at shadows-high + AO: DX12 views 240 → 177 ms
  (frame 265 → 200), Vulkan 210 → 188. So the culled shadow is BIT-IDENTICAL to the flat all-instances march
  (historically checked by the now-quarantined `world-shadow-cull` Post stage; no live gate today) yet restricted to the shadow ray's neighbourhood,
  AND newly CORRECT for occluders outside the camera frustum (the corridor case).
  THREE settled pins: (1) the gather cone is the **penumbra cone**
  `ShadowPenumbraChord = 3/ShadowSharpness`, NOT a bare ray — the Aaltonen
  closest-approach refinement couples each sample to the PREVIOUS sample's nearest-
  surface clearance, so a bare ray (chord 0) or the direct 1/k penumbra drops
  penumbra-edge px (measured: 1/k→840, 2/k→125, 3/k→0); a wider cone is always a
  safe superset, only less selective. (2) The fallback is 3-way: gather BUILT (2) →
  the cull; explicit camera-tile quality policy (1) → the CAMERA-tile mask;
  capacity never downgrades an exact request. NO grid (0)
  → flat all-instances (cheap for few instances, and MATCHES a would-be gather so
  the `sdf.grid` toggle stays render-invariant — the `world-grid-cull` contract).
  (3) PERF is scene-dependent and MEASURED: the per-pixel gather WINS on spread
  scenes (the town reveal 254→116 ms views vs flat; 134→116 vs the old camera-tile)
  but LOSES on dense clustering (1024 carves stacked in one spot 46→101 ms — the
  amortized per-tile camera-tile mask beats the per-pixel gather when the cone can't
  narrow). A density-adaptive gate (skip to camera-tile when the grid is dense) is
  an approximation policy, never an exact-cull fallback; the lever ships ON
  for the overworld's benefit.

- **Exact AO uses a complete live-instance mask.** At the same uniform seam,
  the lanes build `sdfAmbientMaskWords`, excluding only negative-radius parked
  slots. No camera-cone or finite-radius rejection: the ladder consumes field
  clearances, including negative deficits, so a visibility proof cannot preserve
  its result. `sdfAmbientMaskActive` selects this mask only during exact AO;
  fast AO retains its camera-tile approximation. The two 2048-word masks consume
  16 KiB shared memory per workgroup. This favors avatar fidelity; dense scenes
  must measure the cost before selecting exact AO globally.

- **SmoothUnion against WORLD geometry — now cullable (was the headline cull
  gotcha, closed by D1 increment E).** `blendSmoothUnion` is written far-exact
  (`lerp(a, b, 1-h)`): once the seam saturates past the blend radius k, `h`
  clamps to exactly 1 and it returns the accumulator TO THE BIT, so a
  masked-out smooth member is bit-identical to skipping it — provided the cull
  bound covers the k halo, which `SdfProgram.PackInstances` now auto-inflates
  (bound += the instance's max blend radius). So a smooth-blended instance
  masks bit-exactly with a FINITE bound instead of needing an unmaskable one.
  (Before E the saturated lerp computed `candidate + (current - candidate)`,
  ~1 LSB off skipping, so smooth-blending across a maskable instance boundary
  clipped — hence the old unmaskable-bound workaround.)
- **Every interpreter growth re-rolls DXC codegen per backend**: benign ±1
  LSB noise REDISTRIBUTES (spread moves, still ±1) and boundary
  material-winner flips appear as isolated multi-LSB deltas. The relaxed-vs-strict
  threshold-family posture (`WorldLsbExact`/`WorldHighContrast`/`WorldFuzz`,
  `PUCK_PARITY_STRICT`) was Demo+Post-era; it has no live equivalent — that
  machinery lives only in quarantined `experimental/Puck.Post`. The one live
  check is `puck parity` (`src/Puck.Cli/Parity/ParityCommand.cs`): it boots the
  authored parity world offscreen once per backend and renders three verdicts
  per tick-scheduled capture — a content gate, an exact `stateHash` compare,
  and per-tile pixel deltas against per-station `tileMeanDelta`/`tileMaxDelta`/
  `censusFloor` thresholds in `tests/Puck.Parity/parity.contract.json`.
  Recalibrate a station's thresholds by hand in the same change that moves
  them; never re-tighten unasked.
- **An instance bound is an INFLUENCE sphere, and it is read per TILE CONE only.** The
  tile mask may drop an instance because the whole cone misses its sphere; a
  per-SAMPLE test on the same sphere is UNSOUND — a sample just outside the
  sphere still needs the instance's distance to bound its step, and dropping it
  marches straight through the shape (measured 2026-09-03: the avatar's head and
  the dragonfly vanished; reverted). The sound per-sample rule is "cannot lower
  the running minimum", and `mapCore` already applies it per SEGMENT from the
  segment directory (`clearance = acc + r`). Two consequences: (1) a scoped
  segment (`PushField`/`PopField`) is NEVER eligible for that early-out and never
  rigid-planned, so a scope is a real per-sample cost — bake a correction into
  the shape (an ellipsoid's `Data0.w` is free for its `min/max` radius factor)
  before reaching for a scope around a single primitive; (2) a bound short of
  the true reach clips geometry at tile edges TODAY — the stamp pool's per-shape
  dynamic bound was `0.9 × max(scale)` until 2026-09-03 and is now
  `SdfSolidGeometry.Reach` + the shape's field ops (`WorldStampPoolBoundLawTests`).
- **Every `map*` call site is a full copy of the tape interpreter** (DXC has no
  real calls; SM 6.x inlines everything), so the views kernel's cost has a
  FOOTPRINT term beside its evaluation term. Keep call sites ROLLED: `calcAO`'s
  three rungs went from `[unroll]` to `[loop]` on 2026-09-03 for −11 ms of a
  38 ms AO term on the RTX 2060 shipped world with identical arithmetic, and a
  new epilogue walk should reuse an existing call site through a loop rather
  than add one. Measure with `world.debug-view depth` (march only) against the
  shaded frame: the gap is the epilogue, footprint included.
- **The soft shadow is one deterministic penumbra march per lit pixel**
  (`softShadowVisibility`): the running minimum of `k · c / t` — the
  clearance at each sample over the distance travelled — with
  `k = 1 / worldShadowPenumbraSlope()` (the shadow light's authored
  `angularRadius`, tangent taken host-side), marched by the fold-safe clearance
  under `max(ShadowStepNear, ShadowStepFarSlope · t)` with `ShadowStepMin` as
  the floor, then a smoothstep. No per-frame sample, no history lane, no push
  word: a frame is a pure function of its inputs, and a moving occluder leaves
  no trail. The estimate is self-sampling (the step never exceeds the
  clearance, so samples crowd toward a close approach). The closest-approach
  CHORD fold between consecutive clearance spheres is deliberately absent:
  it reads the previous sample too, so whether a pair straddles the close
  approach flips across neighbouring rays whenever the occluder is thinner
  than the step, banding a thin rim's penumbra into alternating stripes, and it
  collapses to zero on a ray leaving its own surface along the normal (the
  clearance doubles every step). Samples within `ShadowEstimateStart` of the
  origin read the origin surface and are skipped, never clamped. The estimate
  divides a de-scaled clearance (`sdfDeScaleField`) by world-unit travel; the
  step and the surface test use the raw clamped sample. The gather cone chord
  is three penumbra half-slopes (`worldShadowPenumbraChord`), which is why the
  validator caps the angular radius at `atan(SdfEnvironment.MaxPenumbraSlope)`.
  The fast path shortens the reach and budget and widens the stride.
  `sdf-world-rt-debug` keeps its own `lightShadow`. To attribute a shadow
  artifact, A/B the levers live (`world.ao off`, `world.shadows off`,
  `world.shadow-mask`, `world.shadow-march`) and place a `camera` at the
  shaded point looking along `worldSunDirection()` under
  `world.debug-view material-id`: it shows what the shadow ray sees.
- **Curvature samples share one interpreter call site.** Keep the `[loop]` in
  `calculateNormalCurvature`: four spelled-out calls duplicated the VM body and
  measured slower on the RTX 4070. The four sample positions, Detail gate,
  primary-center reuse and gradient/curvature formulas retain their semantics.
- **The shadow/AO de-scale is GRADIENT-SCALED, on top of the program stepScale
  clamp, not instead of it.** `sdfDeScaleField`'s divide-back corrects for the
  program's own worst-case Lipschitz clamp (`stepScale`); it says nothing about
  how far a given SHAPE's own formula departs from a unit SDF at the hit (an
  approximate `Ellipsoid`'s directional gradient, `FlareY`'s y-varying shear).
  `sdfResolveSurface` records the hit's LOCAL field gradient magnitude for AO and views —
  `calculateNormal`/`calculateNormalCurvature` (the 4-tap tetrahedron sum's own
  magnitude, divided back by `sdfStepScale()` to strip the taps' own
  `mapDistanceMasked` bake) or `calculateNormalAnalytic` (`mapGradMasked`'s
  `gradient` out-parameter is ALREADY stepScale-excluded — sdf-vm.hlsli's
  `mapGradCore` multiplies only `result.distance` by `stepScale`, never
  `gradient`) — and composes `shadingStepScale = stepScale *
  max(gradientMagnitude, GradientMagnitudeFloor)` as the ONE parameter passed
  to `softShadowVisibility`/`calcAO`/`calcFastAO` in place of the bare
  `stepScale`, so a scaled shape's penumbra/AO term reflects its own local
  steepness instead of only the program-wide bound. Never folds into the
  marching `radius`/step-length logic (unaffected — soundness stays keyed on
  `stepScale` alone); `src/Puck.World/Assets/pipelines/moth.glsl`'s `surfaceGradient`/`shadow`/
  `ambientOcclusion` is the reference posture (the study's `distanceScale`
  correction, computed once at the primary hit and reused for the whole
  secondary march — a deliberate approximation this engine also makes).
- `renderView` computes normals LAZILY (`needsNormal` = normals debug view or
  lit path). Do not add an eager `calculateNormal` — the 4-tap TETRAHEDRON probe
  is ~4 full VM interpretations per pixel in the hottest kernel (isotropic taps,
  `Σ dᵢdᵢᵀ = 4·I`, so it reconstructs the same gradient as the old 6-tap central
  difference at 2/3 the cost; the D1 `stepScale` cancels under `normalize`).
- **The lit normal is ANALYTIC by default — the forward-mode gradient DUAL**
  (`mapGradMasked`/`mapGradCore` in `sdf-vm.hlsli`, consumed by
  `calculateNormalAnalytic` in `sdf-world.hlsli`). ONE dual field eval at the
  hit replaces the four taps: `mapGradCore` is a HIT-ONLY parallel twin of
  `mapCore` (KEEP the walk skeleton IN SYNC) that carries, beside the scalar
  accumulator, the transform-chain Jacobian columns `jx/jy/jz`
  (`= d(localPosition)/d(worldPosition.{x,y,z})`, identity at each RESET, each
  point op applies its analytic point-Jacobian) and the world-space accumulator
  gradient. At a SHAPE the primitive's LOCAL gradient (`evaluateShapeGradient`:
  analytic for sphere/plane/box/torus/capsule/cylinder, shape-local 4-tap FD for
  the exotic rest) maps to world through those columns ×`distanceScale`; field
  ops and blends carry the gradient in `blendShapeDual` (subtraction NEGATES the
  candidate gradient — the classic carve-inversion bug lives there; smooth blends
  LERP by the same `h`; the scope save slot is the `{distance,material,gradient}`
  `SdfFieldSave` struct, one migration for a future depth raise). `stepScale` is
  NOT applied to the gradient — a uniform positive factor `normalize` cancels.
  A rigid segment (host-collapsed `SDF_SEGMENT_RIGID_PLAN`) takes a rigid-leaf
  fast path in the dual too — the KEEP-IN-SYNC twin of `mapCore`'s rigid walk:
  both use `sdfShapeEnabled` on the original shape header and mask off the
  Detail/NoSecondary bits before primitive dispatch. Flagged shapes remain
  eligible for rigid compilation; `RigidLeafModeLawTests` pins host packing,
  while GPU comparisons must check march, detail shading, and secondary modes.
  An identity `Scale` (all four payload lanes exactly one) is also eligible;
  other scales still fall back. `renderView` enables `sdfSecondaryMarchActive`
  only around shadow/AO calls and resets it before hit material/normal work.
  shape-local `evaluateShapeGradient` forward-rotated to world by the leaf
  quaternion (static) or `dynamicOrientation ∘ leafQuat` (`TransformDynamic`),
  `distanceScale` = 1, fed through the shared `sdfComposeDualCandidate` tail — so
  analytic normals are cheap exactly where the primary march is (the avatar
  fleet). This is MORE cross-backend-stable than the taps (survey R7): the hero `world`
  parity IMPROVED (51→11 diff px) and the hero gpu-budget dropped (~1.87→1.58 ms,
  4 evals → 1 dual). The runtime A/B lever is `SdfFrame.UseFiniteDifferenceNormals`
  → `worldUseTapNormals()` (rides `sdfScreenLights[SdfGridObjParams].z`; the demo
  verb is `sdf.normals taps|analytic`, default analytic); the 4-tap path stays
  compiled, selected at runtime. The `sdf-world-rt-debug` 6-tap is a DELIBERATE
  parity probe — do NOT migrate it. No live gate today (the now-quarantined `world-analytic-normal`
  Post stage — the op-chain scene: twist+repeat+scoped-onion+smooth — plus every existing world
  stage historically checked this; they rendered analytic by default).
  Superellipsoids use `sdfSuperellipsoidGradient`: normalize
  `sign(p) * pow(abs(p/r)/max(abs(p/r)), e-1) / r`, returning zero at the center.
  This replaces four leaf SDF taps; it retains the existing unit-gradient
  transport convention and leaves geometric distances unchanged. The helper and
  its dispatch case share `SDF_STRIP_HEAVY` with the primitive's scalar case.
  The builder forwards `detail` at every exponent, including the e=2 Ellipsoid
  delegation; `SdfDetailShapeLawTests` pins packing and contact exclusion.
  Curvature shading bypasses this analytic path: `calculateNormalCurvature`
  samples four neighbors, plus the center when Detail shapes prevent primary
  reuse. Moth enables curvature shading, so analytic leaf
  improvements alone do not reduce the authored Moth normal cost.
- **A HOST-OWNED image-view handle is NOT a durable identity — never
  change-detect a descriptor write against one.** A handle value is unique only
  among LIVE objects; retire the object and the value comes back for a different
  one. Direct3D 12 mints the token as a `GCHandle` whose freed table slot the
  next `Alloc` reuses — MEASURED: three successive QR authorings on one
  screen produced three different `ID3D12Resource`s behind ONE token value —
  and Vulkan hands back a `VkImageView` a driver may re-issue after
  `vkDestroyImageView` (latent there, not observed on the NVIDIA driver).
  `SdfWorldEngine.BindScreenSources`/`BindSources` therefore rewrite host-owned
  bindings (screen sources, child storage images) EVERY frame and value-skip
  only engine-owned views (`m_screenSourceFiller`, `m_sourceTextures`, the glyph
  atlas — and `SetGlyphAtlas` clears its own cache on re-upload, because the
  same recycling `IGpuSurfaceUpload` mints its view). Skipping on a matching
  value left the ring slot's descriptor pointing at a RELEASED resource for the
  rest of the run; the next extent change stopped the freed allocation from
  being reused by an identically sized one and the sample removed the device
  (`DXGI_ERROR_DEVICE_REMOVED`, then an unrecoverable `DEVICE_HUNG`). It needs a
  RENDERED FRAME between the swaps — back-to-back authorings inside one frame
  never publish the retired feed, so a no-`world.wait` stress script does NOT
  discriminate.
- **Builder exception safety**: `Instance`/`DynamicInstance` leave the builder
  with an OPEN instance if the `emit` callback throws — discard the builder,
  never reuse it.
- **`ScreenSlab` has 3 overloads** with materially different `Material`-id
  encoding; the wrong one silently loses screen sampling.
- **Every new soft-blend family needs its own halo derivation, and there are
  now TWO margin channels**: `MaxSmoothBlendRadius` (compose halo — note the
  ChamferUnion `1.70711×k` vs smooth `1×k` asymmetry a copy-paste would
  re-break) and `MaxScopedFieldReach` (a scoped field op's outward growth).
  A new blend/field op must answer which channel covers it before it ships.
- Per-pass GPU-ms: arm live via the gpu.timing switch (demo) / world.timing verb (world) or the run-doc host.timing field.

## Verifying

**Almost nothing gates the world path today.** The POST battery whose
world-path stages exercised every kernel is quarantined with `Puck.Post` and
never run, the `--run`/`--capture` entry points that drove the example
documents left with the `Puck.Demo` composition root, and nothing replaced
either. The one on-demand check is `puck parity` (tests/Puck.Parity/): it boots
the authored parity world (`parity.world.json` + its `parity.sdf.json`
companion) offscreen once per backend — stations: sky, materials, a
`state.lattices` height-field, `noiseDisplace`+`cellJitter`, and a
`prototypes`/`placements`-authored creation exercising chamfer, a panel, a
`symmetry` fold riding a `parent`, and an origin-bearing `repeat` at two
placement scales — and renders
three verdicts per tick-scheduled capture: content gate (camera-inside/
census-floor refusals — `parity-inside.world.json` proves the refusal),
exact `stateHash`, per-tile pixels under `parity.contract.json` (thresholds
are content facts, re-calibrated in the change that changes a station).
There is NO text/glyph station currently. Everything else about a kernel,
ISA, or render-assembly change is unverified by machine.

What remains is looking at it: `dotnet run --project src/Puck.World -c Release --
--exit-after-seconds 2` (0 or less runs until the window is closed), plus the
`Puck.SdfVm.Debug` inspection engine for field-level questions. Say in the
commit what was not checked.

Capture integrations must retain the `FrameCaptureRequest` returned by
`SdfWorldRender.RequestCapture` and await its `Completion`. All capture-capable
decorators forward that same request; `PendingCapturePath` is only a busy
diagnostic. Arm on the host pump, and never block that pump awaiting a frame.
Capture readback must preserve `DeviceLostException` as a host recovery signal:
complete the request with failure, then rethrow it. Do not swallow device loss
as an ordinary PNG error.
See [capture completion](../../../src/Puck.SdfVm/README.md#capture-completion).
