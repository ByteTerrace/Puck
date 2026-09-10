---
name: sdf-world
description: Working on the SDF VM and world renderer — src/Puck.SignedDistance (SdfProgram/SdfProgramBuilder, the packed instruction ISA, and Puck.SignedDistance.Queries' deterministic fixed-point interpreter behind Puck.Maths' IWorldQuery/IFieldEvaluator seams) and src/Puck.SdfVm (SdfWorldEngine/SdfEngineNode, the Assets/Shaders/Sdf kernels, the shared render assembly SdfWorldRenderSpec/SdfWorldRenderBuilder, the Puck.SdfVm.Debug inspection engine, the composition/anchor surface (ISdfSceneEmitter/SdfCompositionFrameSource/SdfMaterialScope/SdfAnchor), and Puck.SdfVm.Views (ViewStack/camera rigs)). Use whenever touching the SDF ISA or packed word layout, the world kernels or their HLSL includes, engine capacities/frames/screen sources, render-assembly/backend selection, the SDF debug/gallery/bench tooling, composing a world program from emitters, anchor/view/camera-rig plumbing, deterministic world queries, or debugging world-render parity or GPU cost. Carries the C#↔HLSL contract pairs and settled engine semantics so they aren't re-derived or accidentally forked.
---

# The SDF world: one contract, two languages

For scoped material composition, `mapCore` must save/reset/restore
`sdfMaterialBlendWeight` and `sdfMaterialBlendOther` alongside distance/material.
A losing scope must not tint the parent; a winning hard-union scope must retain
its internal seam. Verify both cases with contrasting scoped materials against
an unrelated ground surface. The two-material outer-seam behavior is described
in `docs/sdf-wiki/materials-and-primitives.md`.

Factual and procedural only: settled contracts, their exact sync points, and
how to verify. The user's current instruction outranks it — if this file
argues against a demanded change, it is stale; update it in the same change.
The render-assembly reference that used to describe its boundary, capacity
envelope, content seams, and unsupported graph requests is deleted and has no
replacement — read `SdfWorldRenderSpec`/`SdfWorldRenderBuilder` directly.

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

> **Unification-contract alignment** (see docs/vision.md): world content is
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

## The C# ↔ HLSL sync pairs (KEEP IN SYNC — the whole list)

The C# ISA and the shader ISA are ONE contract. These are the live pairs;
change either side only with its partner in the same change. Every C# member
in the table below lives in `Puck.SignedDistance` (the field-as-data half,
split from `Puck.SdfVm` — no GPU/shader-compiler dependency of any kind)
unless it is `SdfWorldEngine`/`SdfEngineNode`/a `Views.*`/`Debug.*` member,
which remain in `Puck.SdfVm` (the GPU engine that consumes
`Puck.SignedDistance` for the program model); the HLSL side is unaffected —
it has no assembly to move. Four rows straddle the cut and are called out by
name: the ISA-identity row (`SdfIsa.Version` + `SdfProgram.ValidateIsa` move;
the shader-set verification and report decode stay on `SdfShaderSetVerification`
in `Puck.SdfVm`), the mask-width row (`SdfProgram.InstanceMaskWordCount`
moves; the engine's push-word write stays), the `MaxScreenSurfaces` row (the
constant moves to `SdfProgramBuilder`; `SdfWorldEngine.MaxScreenSurfaces`
reads it rather than hand-syncing a second literal), and the
`MaxScreenDecalCells`/decal row (the constant moves to the new
`SdfScreenDecalLayout` in `Puck.SignedDistance`; `SetScreenDecal`/
`sdfDecalCells` stay on the engine).

| C# | HLSL | Contract |
|---|---|---|
| `SdfIsa.Version` + `SdfProgram.ValidateIsa` + `SdfWorldEngine` initialization (via `SdfShaderSetVerification`) | `sdf-isa.hlsli` + the report branches in `sdf-beam.comp.hlsl`/both views variants + both `mapCore` dispatch switches | v1 ISA identity is reported by the actual production interpreter bytecode through a runtime GPU readback, cached per device+shader-set; mismatch refuses before `UploadProgram`, an undeclared host opcode refuses by numeric id/instruction index, and an unknown GPU opcode returns the diagnostic material instead of falling through — the version pins a host to its own bytecode, never records that the ISA changed: an op, shape, lane or packing change regenerates the bytecode and leaves the constant alone |
| `SdfProgram` packed `Words` layout, op/shape/blend enums | `sdf-vm.hlsli` decode (`evaluateShape`, op switch) | instruction stream |
| `SdfBlendOp.GrooveUnion` = 10, `PipeUnion` = 11; `SdfProgram.ComposeLipschitz`; `SdfFieldEvaluator.Compose` | `SDF_BLEND_GROOVE_UNION` / `SDF_BLEND_PIPE_UNION`, `blendShape` / `blendShapeDual` | Radius uses Data1.x (`smooth`). With h = sqrt(a²+b²), groove = max(min(a,b), r-h), pipe = min(min(a,b), h-r). Analytic tube gradient = (a·grad(a)+b·grad(b))/h; the groove negates it. Compose bounds as hypot(La,Lb), including PopField; never apply a single program-wide sqrt(2) multiplier. |
| `SdfBlendOp.GrooveSubtraction` = 13, `PipeSubtraction` = 14 | `SDF_BLEND_GROOVE_SUBTRACTION` / `SDF_BLEND_PIPE_SUBTRACTION` | The same tube against the subtraction: groove = max(max(a,-b), r-h), pipe = min(max(a,-b), h-r); candidate wins when -b > a. Same hypot(La,Lb) compose bound. |
| `SdfBlendOp.Morph` = 12, `StairsUnion` = 15, `StairsSubtraction` = 16; `SdfProgramBuilder.PushFieldMorph` / `PushFieldStairs`; `SdfProgram.Validation` | `SDF_BLEND_MORPH` / `SDF_BLEND_STAIRS_*`, the PopField compose tail in `mapCore` / `mapGradCore` | PopField only; a ShapeBlend carrying one is refused by name. Morph: Data0 = (integer lane 0..3, from, to, 0), t = saturate((lane-from)/(to-from)), distance = lerp(saved, candidate, t), material winner at t >= 0.5 with the material blend weight min(t, 1-t) against the other. Stairs: Data1.x = r, Data1.z = integer n >= 1, s = r/n, u = b - r (subtraction: -b - r), d = 0.5·(u + a + abs(mod(u - a + s, 2s) - s)) composed by min (union) or max against -d (subtraction). Data1.y stays the analyzer's 1/L on every pop. Both bound as max(La, Lb). The deterministic field reads every lane as zero. |
| `SdfOp.RotatePlane` = 5; `SdfProgramBuilder.RotatePlane` | `SDF_OP_ROTATE_PLANE`; scalar and dual point cases | Shape selects XY/YZ/XZ (0/1/2), Blend selects X/Y/Z driver (0/1/2), Data0 = (rate, origin, 0, 0). BendX/Y/Z and TwistY builder methods expand to this primitive. |
| `SdfProgram.InstanceMaskWordCount` (`max(1, ceil(n/32))`) | `sdfInstanceMaskWordCount` (reader's INNER word iteration only) | mask width formula |
| `SdfWorldEngine` pushWords[7] = LIVE uploaded program's width | `CompositeParams.instanceMaskWordCount` / `worldInstanceMaskBase` (sdf-world.hlsli) | mask buffer INDEXING (entry width + tile base) — host-pushed, never shader-derived |
| `PushConstantByteLength` = 32 B, words 0..7 | `CompositeParams` (8 uints: extent, tileGrid, viewportCount, childMask, screenMask, instanceMaskWordCount) | Stage 0/1 push |
| `DynamicTransformByteLength` = 48 B/slot | `sdfDynamicTransforms` (three float4 rows: position + shadow participation, quaternion, four anonymous render lanes) | `TransformDynamic` loads lanes 0..3; rigid fast paths load the same lanes and winning frame slot. Unbound chains use zero lanes. `WorldLookMotion.Lanes` is a compact array of up to four expressions, with interior nulls retained. |
| `SdfOp.LaneErode` = 34; `SdfProgramBuilder.LaneErode` | `SDF_OP_LANE_ERODE`, scalar and dual cases | Data0=(integer lane 0..3, from, to, noiseScale), Data1.x=target reach. t=saturate((lane-from)/(to-from)); t>=1 skips the next shape. The ragged term is multiplied by 4*t*(1-t), preserving both endpoints. Winning lanes/frame slot survive field scopes and material selection. Render-only; the fixed evaluator has no dynamic lane table. |
| `SdfProgramBuilder.MaxInstances` = 65536 | `SDF_MAX_INSTANCES` | instance cap, at most 2048 mask words/tile. `InstanceMaskWordCountFor` derives width from the declared program count; smaller programs pack identically. World reserves 367 shapes per stamp (`WorldPlacementPolicy.MaxShapesPerStamp`, worst-case pool draw `MaxStampRegistrations x MaxShapesPerStamp` = 46976 — the largest value whose shipped-overworld COMPOSED boot probe, adjacency-band reservations included, still measures at least 4096 instances of headroom under this ceiling), so its boot reservation needs this larger ceiling. Changes require paired shader bytecode, `WorldRenderEnvelopeLawTests`, and real GPU validation; buffer growth must be measured with the actual reserved program. |
| `SdfProgramBuilder.MaxScreenSurfaces` = 32 (raised from 8, the many-eyes arc leg 1; was 4 pre-Arc-3); material sentinel `ScreenMaterialId + 1 + screenIndex`; capped at 32 by the single-`uint` `screenMask` push word | 32 combined-image-sampler bindings (`screenSource0..31` at bindings 12-43 / registers t5-t36, samplers s0-s31; `sdfInstanceMasks`/`sdfScreenLights` shifted to t37/t38; the glyph atlas to binding 44 / t39 / s32) — the run is DERIVED (`ScreenSourceBindingBase + i`, `BuildScreenSourceBindings`), never hand-listed, so the descriptor pool auto-sizes from `GpuDescriptorPoolSizes.ForSets`; `SdfScreenLightEnv`/the screen-light buffer + grid rows all key off `MaxScreenSurfaces`; the HLSL side names the width `SdfScreenSurfaceCount` and `SdfScreenLightEnv` derives from it | diegetic screens. THE SENTINEL BAND IS CLOSED ON BOTH SIDES: `Build()` accepts only `ScreenMaterialId` (plain, reads no side table) or `ScreenMaterialId + 1 + i` for an `i` a DECLARED `SdfScreenSurface` occupies, and `sampleScreenSurface` bounds `screenIndex` against `SdfScreenSurfaceCount` before touching `screenSurfaces[]`/`sdfDecalCells[]` — the sibling of `sdf-world-rt-debug`'s `hitMaterial >= SDF_SCREEN_MATERIAL` guard, and for the same reason (D3D12 zeroes an OOB structured-buffer read by spec; Vulkan defines it only under `robustBufferAccess`). A surface's `Right`/`Up` MUST be unit and orthogonal, and its `HalfWidth`/`HalfHeight` strictly positive — both refused by `ScreenSlab`, by the `SdfProgram` ctor, and by `WorldDefinitionValidator` — because the shader projects the hit onto the two axes and DIVIDES by their half-extents (`dot(local, right)/right.w`), while the slab's geometry and the server's `WorldColliderSet.ScreenBox` ride the frame derived from them. The slab's DEPTH half-extent stays unconstrained: nothing divides by it |
| `SdfWorldEngine.SetScreenSurface(index, origin, right, up, halfW, halfH)` — writes a host mirror; DIRTY-GATED (2026-07-16, perf plan Phase 1.2): the call compares against the mirror and is a no-op unless a value actually changed, so a MOVING screen slab (a walking creature's face) still samples correctly every frame `SdfEngineNode` polls per-index transform providers via `ISdfFrameSource.ScreenSurfaceTransforms` (default-implemented), while a static/unchanged poll costs no upload. Per-ring-slot dirty bits (`m_screenSurfaceDirty`, same pattern as `m_decalDirty`) — a real change dirties EVERY slot, `PrepareFrame` uploads + clears only the current slot's bit, so no slot ever renders a stale table. The screen-LIGHT buffer stays unconditional (excluded on purpose — see plan) | `screenSurfaces` StructuredBuffer read per pixel — NO kernel change was needed | moving screens |
| `SdfWorldEngine` screen-light buffer via `SetScreenLight` + `SdfFrame.AmbientScale/SunScale` (entries cover screens 0..31 + env — sized by `MaxScreenSurfaces`) | `sdfScreenLights` (t38, after `sdfInstanceMasks` t37; the glyph atlas t39 and `sdfDecalCells` t40 follow) + `SdfScreenLightEnv` (= 32) decode; the `renderView` light loop iterates all 32 | per-frame screen glow + room dimming |
| `SdfViewSnapshot.RenderScale` (default 1) → `PackViewports` quantizes ONE `RenderScaleQ` byte (1..255; child slots forced 255) into ViewportData's 6th float4 row (`ViewportByteLength` = 96 B) AND packs it 8-bit into `CompositeParams2.scaleQPacked` (`BuildCompositePush`) | `ViewportData.renderScale.x` + `worldRenderDims` (`max(1,(dim·q+127)/255)`, INTEGER — beam/instance-cull/views all derive the identical reduced extent) ↔ the composite's `scaleQPacked` unpack + bilinear upsample (`q == 255` = the exact-copy path, byte-identical) | per-view render scale — presentation-only downscale (reveal/immersed policy lives in `ScreenLayoutDirector`); native is bit-exact BY CONSTRUCTION. Post: `world-render-scale` (blur-envelope, calibrated live) |
| Grid-lock overlay (`GridOverlayState` record struct, `Puck.SdfVm` root namespace since 2026-07-10 — the `From(SnapConfig,…)` factory stays demo-side as `Puck.Demo.Editing.GridOverlayFactory` → `SdfFrame.GridFlags`/`GridWorldPitch`/`GridFloorY`/`GridObjectOrigin`/`GridObjectFrame`/`GridObjectPitch`/`GridObjectPatchRadius`, packed by `SdfWorldEngine.PackScreenLights` into `sdfScreenLights` rows 9..12; `ScreenLightByteLength` = `(MaxScreenSurfaces + 5)` float4) | `sdfScreenLights[SdfGridWorld=9]` (x=flags bit0 world/bit1 object, y=floorY, zw=world pitch XZ), `[SdfGridObjOrigin=10]` (xyz origin, w pitch X), `[SdfGridObjFrame=11]` (frame quat), `[SdfGridObjParams=12]` (x pitch Z, y patch radius); the `applyWorldFloorGrid`/`applyObjectGrid` tints at the `renderView` material call site (guarded `#ifdef SDF_SCREEN_SOURCES`) | the editors' grid visualization — env row 8 STAYS put (it doubles as the screen-count loop bound); adding a grid lane touches BOTH sides + `PackScreenLights`. Session-only authoring state (never sim/wire format); default 0 = byte-identical upload |
| The **tile-cull plane layout** — `SdfWorldEngine.TilePlaneCount` (= 4; sizes `m_tileBuffer` = `TilePlaneCount · viewportCap · tileGridX · tileGridY` floats) | `WorldTilePlaneCount` (= 4u) + `worldTileMarchStartIndex` (plane 0, no stride) / `worldTileFirstExitIndex` (1·stride) / `worldTileSecondEntryIndex` (2·stride) / `worldTileFarBoundIndex` ((count−1)·stride) in sdf-world.hlsli; stride = `tileGrid.x·tileGrid.y·viewportCount` | the four-bound teleport (Larsson "The Gunk") + the **F1 far bound** — plane 0 = the classic marchStart (the ONLY plane sdf-cull-args + the compositor read, so their indexing is stride-independent), planes 1/2 = the proven-empty gap `[firstExit, secondEntry]`, plane 3 = the far bound. sdf-beam WRITES all four (`TileBounds`), sdf-world-views READS planes 1/2/3. Every plane is a total function (the view's far distance = "no gap/no bound"). Growing the plane count touches `TilePlaneCount` + `WorldTilePlaneCount` + a new accessor on BOTH sides |
| The **FAR DISTANCE** — `SdfFrame.FarDistance` (default `SdfFrame.DefaultFarDistance` = 40, the retired `MaxDistance` shader constant; refused by `PrepareFrame` unless finite and positive) packed by `SdfWorldEngine.PackViewports` into every viewport row's `renderScale.w` lane (the 96-byte `ViewportData` row's last lane; covered by the cadence signature) | `worldFarDistance(view)` (sdf-world.hlsli) — read by `coneMarchTileBounds`/`coneMarchFarBound` (the beam's entry/gap/tail proofs and every "nothing proven" plane sentinel), `renderView`'s far exit, `marchOvershootDepth`, and the depth/overshoot debug ramps; sdf-beam seeds `TileBounds` with it, sdf-world-views pushes the F1 "off" side to `farDistance + 1`. There is NO shader constant for it any more. The rt-debug kernel's twin is `RtParams.farDistance` (a push lane no live host packs; its `NoRayHit` sentinel is `SDF_FAR_DISTANCE`) | the depth every camera march ends at — WORLD DATA: `render.farDistance` (`WorldRenderDefaults.FarDistance`, nullable; `WorldRenderFarDistance.Resolve` in `Puck.World.Client` maps absent → the pinned 40, so an unauthored world marches unchanged), validated by name into `[WorldRenderDefaults.MinFarDistance = 1, MaxFarDistance = 8192]` (8192 = the largest power of two whose float spacing 2^13·2^-23 ≈ 0.00098 still resolves `SurfaceEpsilon`), re-read off the live definition every frame by `WorldFramePresenter` (a `world.row.set render` lands on the next frame), echoed by `world.budget` (reach multiplier, horizon-ray steps per unit of camera height against `SdfWorldEngine.PrimaryMarchSteps` = 128 — KEEP IN SYNC with `MaxSteps`, and the fog remnant `exp(−fogDensity·far)` at the far plane). The rows below say "the far distance" where they used to say MaxDistance |
| The **F1 FAR BOUND** (perf plan Phase 5.1) — `SdfFrame.DisableFarBound` (default false = ON) packed by `SdfWorldEngine.PackScreenLights` into the far-field row `.x` at `(MaxScreenSurfaces + 7)`; `ScreenLightByteLength` = `(MaxScreenSurfaces + 8)` float4 | producer `coneMarchTileBounds`/`coneMarchFarBound` (sdf-beam) → `TileBounds.farBound` (plane 3); consumer `renderView`'s `if (traveled >= farBound) break;` beside the teleport; lever `sdfScreenLights[SdfFarFieldParams=39].x` → `worldFarBoundDisabled()` (disable pushes `farBound = farDistance+1` so the "off" side is exactly pre-F1) | the depth past which a tile's cone cannot produce any FOOTPRINT-ACCEPTED hit through the far distance. ⚠ LOAD-BEARING PROOF: the tail proves clearance against the FOOTPRINT-INFLATED threshold `min(map(center), sdfMapStepBound) − (chord + footprint)·t > SurfaceEpsilon`, stepping `≤ clearance/(1 + chord + footprint)` — NOT bare `ConeEpsilon` (the fine march accepts hits up to `footprint·t ≈ 0.001·t`, so an ε-proof is anti-conservative). footprint = `2·view.right.w / rectDims.y`, computed identically in beam (from `regionSizePx`) and views. OUTPUT-IDENTICAL on the shipped shading path (both render skyColor in `[farBound, farDistance]`); only step counts + the termination debug view change. March-path change (solidity + parity families + hero canary), re-golden the termination debug view only |
| The **fine march's exit discipline** — no C# side (`renderView`, sdf-world.hlsli; the strict `SDF_STRICT_MARCH` path shares it) | (1) ONE accept rule, `fieldDistance < max(SurfaceEpsilon, pixelFootprint·traveled)`, applied by the in-loop hit arm AND by the exhaustion arm after the loop: the loop tracks the closest approach as `candidateMargin = min(fieldDistance − hitThreshold)` over every evaluated sample — an overshoot-skipped sample included, which is the only way a sample can satisfy the rule without being accepted in-loop — and a ray that ends (step cap or a far exit) unaccepted with `candidateMargin < 0` re-evaluates the field at `candidateT` and shades it (material, blend channel, terminal radius from that eval). No second threshold exists. (2) A far exit (`traveled >= farBound` or `> farDistance`) is taken only on a VALIDATED step: an over-relaxed advance (`stepLength > radius`) that would cross an exit is retaken as the plain step (`radius`, ω = 1, slope reset) first, and the ray exits only if that crosses too — the disjoint-sphere test that validates a relaxed step lives on the NEXT sample, which the exit would skip. (3) The termination debug view classifies "escaped" as `marchStep < MaxSteps` (a break), "exhausted" as the loop running out with no candidate | the rule that keeps adjacent silhouette pixels resolving the same way whichever arm ends them. The failure the validated exit closes is per-pixel, not per-tile: a ray leaving a near object's silhouette accelerates (ω → 10) and its relaxed step vaults the background surface past the far plane — visible as the escaped class dotted along every edge in the termination view, pixel-identical under `world.far-field off` because both exits shared it. Pixels whose relaxed step never crosses an exit are bit-identical to before; parity's per-tile pixel verdicts move only at silhouettes |
| The **environment block** — `Puck.SignedDistance.SdfEnvironment` (one lane table: control row, up to 8 lights × 3 rows, 2 curvature rows, sky control, up to 4 gradient stops, stars, twinkle, 4 cloud rows; `BlendOf` names each lane's cycle blend kind) carried on `SdfFrame.Environment`, packed by `SdfWorldEngine.PackEnvironment` into the screen-light rows after the far-field row with the host bakes (unit directions in double, sun-disc `pow` exponent from the angular radius, twinkle period in engine ticks, cloud offsets/spin integrated from `SampleIndex`) | `SdfEnvBase = 40u` + the `SdfEnv*` row constants and `worldEnvRow`/`worldLight`/`worldSkyStop`/`world*` accessors in sdf-world.hlsli; `worldEnvRow`'s `#else` half returns `SdfEnvironment.Default` (the pinned sun + hemisphere) for kernels that bind no screen-light buffer | KEEP the three IN SYNC: the C# row layout, `PackEnvironment`, the HLSL constants. Lights are a typed list (`SdfLightKind` directional/hemisphere/rim/point); the one directional with `Shadows` drives `softShadowVisibility` and is what `worldSunDirection()`/`worldSunColor()` read (the sky disc, the clouds' lighting, the material specular); every other directional and the hemisphere are scaled by ambient occlusion; rim lights add after the material shade. A **point** light's `Direction` lanes carry a world-space POSITION instead of a direction and `Param` its inverse-square falloff radius (`intensity = weight / (1 + (d/r)^2)`); the light row's otherwise-unused row+2 lane 2 (`c.z`, `worldLight().dynamicSlot`) carries its dynamic-transform slot, -1 for a static authored position — resolved every frame by `worldPointLightPosition` against `sdfDynamicTransforms` when `SDF_DYNAMIC_TRANSFORMS` is bound, falling back to the static position otherwise. Its Lambert diffuse folds into the ordinary light loop, scaled by ambient occlusion like every non-shadow light; its GGX specular is a second pass after the material shade, through `sdfMaterialSpecular` (sdf-vm.hlsli, factored out of `sdfMaterialShade` so both share one BRDF). No shadow march, and refused alongside `render.cycle` (the position lane cannot ride the arc-slerp `render.cycle` gives every other light's direction lane). The sky is a gradient of up to 4 stops (piecewise-linear in direction.y, `skyGradient`), a sun disc bound to a light slot, stars, clouds; `worldSkyEnabled` false takes the pinned two-stop branch bit-identically. Authored as `render.lighting.lights[]`/`.curvature` and `render.sky.layers[]` (`WorldRenderLight`/`WorldRenderSkyLayer` `$type` unions), written into lanes by `WorldRenderCycleTrack`'s one document-to-lanes writer, blended per lane by `SdfEnvironment.Blend` (directions along the arc) for `render.cycle`; echoed by `world.lighting`. A point light's placement anchor (`WorldRenderLight.Point.Anchor`, a `WorldAnchor.Placement` only) resolves to its dynamic-transform slot in `WorldFramePresenter` (`WorldStampPool.TryShapeTransformSlot`), fresh every produced frame — never cached with the statics/cycle keys `WorldRenderCycleTrack` resolves once per revision |
| `sdfMaterialShade` takes accumulated `float3` radiance (not a scalar) | `sdfMaterialShade(..., float3 diffuse, ...)` — the two callers (`sdf-world.hlsli`, `sdf-world-rt-debug`) | shade funnel (colored lights) |
| `SdfMaterial`, `SdfMaterialLayers`, `SdfProgram.PackMaterials`, `MaterialVectorsPerEntry` = 20 | `SdfMaterialData`, `sdfMaterialLoad`, `SDF_MATERIAL_VECTORS_PER_ENTRY` = 20; `shade-layers.hlsli`, `shade-weathering.hlsli` | Rows 0..3: base shading; 4..7: inset frame and paint controls; 8..11: four radial RGB/radius stops; 12..13: weathering controls; 14..17: two threshold/surface pairs; 18..19: deposit surface. All colors and roughness/metal values are authored. Inset uses its explicit origin/rotation/depth/IOR and the winning dynamic frame (world space without one). Weathering selects anonymous lane 0..3, with a floor and seeded coverage. No semantic paint colors or named damage channel. |
| `render.environment` (`WorldRenderEnvironment`: `softboxes[]` ≤4 of `WorldRenderSoftbox` {direction, size, color?, weight?, blur?}, `horizon`: `WorldRenderHorizon` {low?, high?}) + `render.tonemap` (`WorldTonemap` {None, Filmic}) — appended to the END of the `SdfEnvironment` lane table (`RowCount` 38→53: `SoftboxControlRow`=38 [x count, y tonemap mode], `SoftboxesRow`=39..50 [3 rows × 4 softboxes], `HorizonLowRow`=51, `HorizonHighRow`=52); `WorldRenderCycleTrack.WriteEnvironment` writes both directly onto the statics ONCE per revision — NEITHER is a `render.cycle` key field, so every key inherits the identical value through `CopyFrom` and blends to itself exactly under any `SdfEnvironment.BlendOf` classification | `SdfEnvSoftboxControl`/`SdfEnvSoftboxes`/`SdfEnvHorizonLow`/`SdfEnvHorizonHigh` (offset from `SdfEnvBase`=40) + `worldEnvironmentSoftbox`/`worldEnvironmentHorizon`/`worldStudioReflection`/`worldTonemapMode`/`sdfFilmicTonemap` in `sdf-world.hlsli` | analytic studio reflections + filmic tonemap: `worldStudioReflection(reflectDir, roughness)` sums the horizon gradient (lerp low→high over `direction.y ∈ [-1,1]`) and each softbox's smooth angular falloff (`1 - angle/(|size| + max(blur, roughness))`, smoothstep-shaped — roughness widens and dims the catch), added at the material-shade site as `worldStudioReflection(reflect(rayDirection, normal), roughness) * fresnel * ambientOcclusion` (a roughness-aware Schlick fresnel, `f0 + (max(1-roughness, f0) - f0)*(1-nDotV)^5`; NO baked gain on top — a softbox's authored `weight` and the horizon's authored colors are the levers, and a constant multiplier there is a tunable baked as a constant). An absent section (0 softboxes, a black horizon) contributes exactly 0 — byte-identical addition. `sdfFilmicTonemap` (a Narkowicz ACES fit and ONLY the fit — the study's trailing gamma-2.2 encode is omitted because this pipeline's shading is already display-referred, with no sRGB encode anywhere before the rgba8 store; keeping it double-encoded and washed the whole frame out) applies once, at `renderView`'s epilogue, to the FINAL color — a hit's shaded color AND a miss's sky alike, so a silhouette's sky blend and the open sky beside it sit on one curve (tonemapping hits alone haloed every silhouette against an un-mapped sky) — when `worldTonemapMode() == SdfTonemapFilmic`; `sdf-sky.comp` applies the SAME curve, in the same place in the pixel's op order (before its dither), to the sky it writes into a beam-culled tile, so the tile seam stays bit-identical under Filmic as under None. Every debug view bypasses it; `None` (absent, the default) is a no-op |
| `SdfLightKind.Occluder` = 4; `WorldRenderLight.Occluder`; `WorldLightAnchorResolver` | Occluder branch in `sdf-world.hlsli` | Uses ordinary light rows: position, radius, weight and optional dynamic frame slot. Attenuates nearby surface illumination while preserving self-emission. Author an optional entity, entity-part, or placement anchor; resolve its pose each frame and use weight zero while unavailable. Point and Occluder positions blend linearly through render cycles. `SdfEnvironment.RowCount` = 53; no body-centre side table. |
| `DebugViewModes.Names` (`Puck.SdfVm` root namespace since 2026-07-10, order IS the wire value, 11 entries incl. mask/overshoot/evals) | `DebugViewModeCount` (= 11)/`DebugViewModeNormals` + the `viewMode` switch (sdf-world.hlsli `renderView`) — mode 10 (`evals`, perf-plan Phase 0 instrumentation) is the one mode besides final shading that forces `useFinalShading` true, so its `sdfEvalCount` tally (a per-thread static, incremented at every map()-family call site in sdf-world.hlsli — never inside mapCore/sdf-vm.hlsli) reflects the real epilogue cost, not a debug shortcut | debug views — adding a mode touches BOTH plus the switch |
| `SdfDriftMonolith.Emit` (`Puck.SdfVm.Debug`, shared verbatim by the Post drift-ceiling stage and the demo gallery's monolith exhibit — CALIBRATED, change only with a recalibration) | n/a (host-side program emission only) | ⚠ the two hex-stride materials are reached POSITIONALLY through the `WallpaperFold` chain's `materialStride`, so `Emit` must be called into a builder holding NONE of the caller's own materials yet — it owns the whole material palette and must be emitted FIRST, or the positional stride reaches the wrong (caller-owned) material |
| bound-analysis modes | `SDF_BOUND_*` skip in `map()` | bounds gate |
| `SdfProgram.AnalyzeLipschitz`, `AnalyzeChainLipschitz`, `AnalyzeCellPointFactors` | `mapCore` / `mapGradCore` final step-scale multiply and PopField candidate scale | Fold each ShapeBlend and PopField in tape order: ordinary blends use max(La,Lb), chamfers use max(La,Lb,(La+Lb)/sqrt(2)), round seams use hypot(La,Lb). PopField bakes a scope-local reciprocal bound into Data1.y. Cellular relief adds a*f*L times the coordinate derivative bound at its instruction; it does not inherit primitive distanceScale. Nonfinite accumulators refuse. |
adds a rank-1 shear on top of its own `1/max(s)` distanceScale correction — triangle-inequality bound
`max(1/s, 1) + rho·|ds/dy|/s²` over the chain's reach rho (`FlareOperatorNorm`), reach-DEPENDENT like Bend/Twist
but folded through its own `chainFlares` list rather than `chainWarpRates` (a scalar rate doesn't cover it — it
needs amount, bulge AND the distanceScale factor). A **chamfer blend** is the one `SdfBlendOp` that is NOT 1-Lipschitz AND the only composition whose bound can exceed BOTH operands: the bevel-arm gradient is `(∇a ± ∇b)/√2`, so composing fields bounded by `La`/`Lb` carries `max(La, Lb, (La + Lb)/√2)`. That recurrence is folded PER COMPOSITION (`ComposeLipschitz`, walked in `mapCore`'s own order by a second pass over the stream), never per chain — a per-chain latch counts one √2 however many chamfers compose and understates by up to `(1 + √2)/√2 = 1.70711×`, which HOLES thin geometry under three or more chamfers. The accumulator seeds at the `SDF_FAR_DISTANCE` CONSTANT (L = 0), so the FIRST chamfer composition is the identity, TWO reach exactly √2 (byte-identical to the latched value), growth starts at the THIRD, and the fixed point is `1 + √2`. Segment splitting is NO protection — one accumulator crosses every `ResetPoint`. Smooth-min stays exactly 1. A **Displace/DomainWarp** sine field: factor `1 + amplitude·max|frequency_i|` — the INFINITY norm, not `‖f‖₂` (Displace's squared gradient norm is multilinear in the three squared sines ⇒ maximizes at a cube vertex; DomainWarp's `J - I` is a generalized permutation matrix whose spectral norm is its largest entry). `== 1.0f` EXACTLY for an isometric, chamfer/relief/warp-free program (byte-identical); the per-candidate `distanceScale` (Scale / the D2 log-spherical `r/density` correction) is a DISTINCT channel — never merged. Post: `sdf-lipschitz` (CPU bake assert; `warp-free stepScale == 1.0f` EXACTLY is the byte-identity contract) + `world-warp-solidity` / `world-log-sphere-solidity` (single-backend GPU solidity — parity CAN'T catch it, both backends overstep identically) + `world-chamfer` (chamfer cross-backend parity) |
| `SdfOp.LogSphere` (id 21) / `SdfProgramBuilder.LogSphere(shellRatio, twist)` — Data0.x = w (`ln(shellRatio)`, HOST-BAKED), Data0.y = twist (radians/shell), Data0.z = 1/w (HOST-BAKED); `AnalyzeLipschitz` folds `exp(w/2)` into `stepScale` | `SDF_OP_LOG_SPHERE` (21u) in `mapCore` — nearest-shell radial log-fold (`round`, like Repeat), an unconditional Z-spin (isometry, the Droste spiral), then `distanceScale *= shellScale` (the `r/density` correction, SAME channel as `SDF_OP_SCALE`, composes multiplicatively); `SDF_LOGSPHERE_MIN_RADIUS` floors the origin | D2 log-spherical DOMAIN warp — tiles space into infinite self-similar Droste shells. Radial-only fold ⇒ NO polar pinching; the r/density correction rides `distanceScale` (never `stepScale`); the `exp(w/2)` factor keeps the OVER-RELAXED march (omega 1.2) hole-free across shell boundaries. `AnalyzeSegment` gives it `SDF_BOUND_NONE` (unbounded periodic domain, via the `default` case — do NOT add a case). Op-unused programs stay byte-identical. Post: `world-log-sphere` (parity, `WorldLsbExact`) + `world-log-sphere-solidity` |
| PARKED instances (Arc 4): `SdfInstanceRange`/`BeginInstanceDynamic` carry an `Active` flag; an inactive slot packs the `SdfProgram.ParkedBoundRadius` (negative) bound sentinel — the reserved-pool "always fits by construction" contract is untouched, parked slots just become CHEAP | `collectInstanceMaskWord` (sdf-world.hlsli, the sphere-vs-cone tile test) and the full-eval enumeration (sdf-vm.hlsli, segment-range skip) each skip a negative-radius bound with ONE branch | parked-slot skip — beam/views cost tracks LIVE content, not reserved capacity. Demo-side, the pools (players/creator/companions) set `Active` per rebuild; a hidden-below-the-floor placement WITHOUT the flag is the pre-Arc-4 bug (264 always-tested instances = the 0.9→14.7ms regression) |
| The **2D-primitive family** (Vesica id-7 precedent, generalized): `SdfShapeType.RoundedRectangle`=8, `.RegularPolygon`=9, `.Star`=10, `.Trapezoid`=12, `.Ellipse`=13 (`RoundCone`=11, `ScreenSlab`=14 unchanged); `.ChamferedRectangle`=17 joins the family after `SampledRegion`=16; `.ConvexPolygon`=19 (below) is a family member too, after `.Superellipsoid`=18 (enum contiguous 0-19) + `SdfLift { Revolve = 0, Extrude = 1 }` (`SdfLift.cs`) | matching `SDF_SHAPE_ROUNDED_RECT`/`_REGULAR_POLYGON`/`_STAR`/`_TRAPEZOID`/`_ELLIPSE` ids + `SDF_LIFT_REVOLVE`/`SDF_LIFT_EXTRUDE` (packed into Data1.y, decoded `> 0.5`) | SHARED lane layout for the whole family: Data0.xyz = the 2D shape params, Data0.w = the lift amount (revolve offset o OR extrude half-height h), Data1.x = smooth radius, Data1.y = lift mode, Data1.z = per shape (RegularPolygon/Star: the baked `sin(π/m)` ecs.y; RoundedRectangle/Trapezoid/Ellipse: the cap-chamfer radius `sdfExtrudeChamfer2D` bevels an extrude's cap rims by, zero = the plain join; ChamferedRectangle: unused), Data1.w = the edge-rounding radius (the family-wide fillet lane: the Data0 profile params and an extrude's lift amount arrive inset by it and the wrapper subtracts it back out) |
| **`SdfShapeType.Superellipsoid`** (id 18, NOT a 2D-lift family member — a solid 3D formula, no lift) / `SdfProgramBuilder.Superellipsoid(radii, exponent, …)` — Data0 = (radiusX, radiusY, radiusZ, exponent e in [2, 8]), Data1 = (smooth, 1/radiusX, 1/radiusY, 1/radiusZ, HOST-BAKED). `e == 2` (`MinSuperellipsoidExponent`) emits `Ellipsoid` (id 6) directly instead, so this id never carries the ellipsoid limit | `SDF_SHAPE_SUPERELLIPSOID` + `sdfSuperellipsoid(p, radii, inverseRadii, exponent)` = `(pow(sum(pow(abs(p)*invR, e)), 1/e) - 1) * min(radii)`, gated `#ifndef SDF_STRIP_HEAVY` (`SdfViewsKernelVariants.FirstHeavyTouch` names it, forcing `Full`) | generalized ellipsoid ("squircle"/rounded-cube family), EXACTLY 1-Lipschitz for every radius and every admitted exponent — no `AnalyzeLipschitz` step clamp, unlike the approximate `Ellipsoid` #6 (see the builder's remarks for the proof: the scaled gauge is homogeneous degree 1, so `\|∇d\|` is scale-invariant along every ray and its sup on the zero-level surface is exactly `max(r)/min(r)` reduced to exactly 1 by the min(r) baking — `SuperellipsoidLawTests` proves it numerically over a grid of e and points). BOTH sides compute the gauge in its FACTORED form `m·(Σ(qᵢ/m)^e)^(1/e)`, `m = max(q)`, `q = |p|/r` (KEEP IN SYNC) — every pow argument in [0, 1], the sum in [1, 3] — because the plain `Σqᵢ^e` saturates the fixed-point mirror's Q48.16 carrier at ~60 radii for e = 8 (`FixedQ4816.Pow` clamps to `MaxValue`, collapsing a 200-radius query to ~5.8 radii and starving every far march) and overflows float at ~1e5 radii; `SuperellipsoidLawTests` pins the far-query distance. `TryGetLocalBound`/`Reach` deliberately give it NO circumsphere cull bound the way Ellipsoid has none (its field can still be treated as approximate for culling purposes even though 1-Lipschitz for marching); `SdfSolidGeometry.Reach`/`ShapeReachRadius` use `\|radii\|` (the box circumsphere, sound for any exponent — `max(radii)` alone is UNSOUND past e=2, since a rounded-corner solid reaches past it). Fixed-point mirror via `FixedQ4816.Pow` in `Puck.SignedDistance.Queries.SdfFieldEvaluator` (fully supported, not excluded). No Post stage (quarantine) |
| **`SdfShapeType.ConvexPolygon`** (id 19, a 2D-lift family member whose profile is too large to pack inline) / `SdfProgramBuilder.ConvexPolygon(vertices, cornerRadius, lift, liftAmount, …)` — Data0.x = `asfloat((tableOffset << 4) \| vertexCount)` (HOST-PATCHED by `SdfProgram`'s constructor once every profile's table layout is known — the builder emits a 0f placeholder), Data0.w = lift amount; Data1 = (smooth, lift mode, cap chamfer, edge-rounding radius) — the family's usual lanes | `SDF_SHAPE_CONVEX_POLYGON` + `sdfConvexPolygon2D`/`sdfPolygonVertex`, gated `#ifndef SDF_STRIP_HEAVY` | 3–8 clockwise, validated-convex vertices INSIDE THE UNIT SQUARE (`SdfPrismProfile.IsValidConvexHull` — each coordinate in [-1, 1], since the profile rides the Prism's XY scale like every other profile and `SdfSolidGeometry.Reach`'s Prism arm covers exactly that frame; strictly negative 2D cross product at every turn) live in a SIDE TABLE appended to the packed program's own word stream, right after the rigid-leaf plan — no new GPU binding: `sdfWords` is already globally bound and randomly indexed (the segment/instance/world-segment/grid tables all work this way), so the convex-polygon table is one more region of the SAME buffer. The 2D core is the exact iq polygon SDF (running min squared distance to every edge SEGMENT, signed by an even/odd crossing-parity flip) — a true Euclidean distance, so EXACT and 1-Lipschitz like the rest of the family; `TryGetLocalBound` gives it a real cull bound from the farthest vertex (`LiftedBoundRadius`, needing the host-side vertex list via `SdfProgram.ConvexPolygonProfiles`/`TryFindConvexPolygonVertices`, since the packed Data0.x carries no usable float). Fixed-point mirror in `SdfFieldEvaluator` reads the SAME host-side vertex list directly (`CompiledInstruction.ConvexPolygonVertices`, resolved once at `Compile()` — never decodes the packed table offset, which is meaningless as a float). `ConvexProfileLawTests` proves the rectangle special case against `RoundedRectangle` and the sign convention against the moth study's shoulder pentagon. No Post stage (quarantine) |
| **`SdfShapeType.Sweep`** (id 20, NOT a 2D-lift family member — its own lane layout) / `SdfProgramBuilder.Sweep(a, b, c, radiusStart, radiusEnd, bulge, strands, twist, strandOffset, …)` — a quadratic Bezier curve (control points A/B/C, creation units) swept with a radius tapering linearly between two endpoints plus a mid-span bulge (`r(t) = mix(radiusStart, radiusEnd, t) + bulge·sin(π·t)^0.65`), optionally as 1-4 helical strands orbiting the curve (radius `strandOffset`, phase `t·twist·2π + k·2π/strands`). Data0.x = `asfloat(uint table offset)` (HOST-PATCHED, the SAME side-table door ConvexPolygon's vertex table uses — the curve's A/B/C/radiusStart/radiusEnd/bulge live in 3 FIXED uvec4 words in `sdfWords`, unlike ConvexPolygon's variable-length table, so no count needs packing alongside the offset); Data0.yzw = (strands, twist, strandOffset); Data1 = (smooth [ISA-wide], reserved×3) | `SDF_SHAPE_SWEEP` + `sdfSweepCurve`/`sdfSweepClosestT`/`sdfSweepRadiusAt`/`sdfSweepConservativeMargin`/`sdfSweep`, gated `#ifndef SDF_STRIP_HEAVY` | "exact enough", NOT exact: `sdfSweepClosestT` is the standard closed-form closest-point-on-quadratic-bezier (iq's depressed-cubic solve, degenerating to the A-C segment when B is the exact midpoint), evaluated ONCE at the CENTERLINE (ignoring radius/orbit variation elsewhere), so the raw candidate can overestimate true distance near strong bulge/taper/strand-offset — `sdfSweepConservativeMargin` (`1.0·\|bulge\| + 0.7·strandOffset·(1+\|twist\|) + 0.9·\|radiusEnd−radiusStart\|`) is a NUMERICALLY CALIBRATED correction (randomized-grid proof against a fine-sampled reference tube, `SweepLawTests`), not a closed-form Lipschitz bound — the same posture as Ellipsoid #6's eccentricity or Vesica's pre-fix history, not the family's usual exact+1-Lipschitz shapes. `SdfProgramBuilder.Sweep` REFUSES (by name, `CellJitterLipschitz`-style) a declaration whose `\|bulge\|`/taper/`strandOffset` ratio to the authored radii exceeds the envelope the margin was calibrated against (`MaxSweepBulgeRatio`=16, `MaxSweepTaperRatio`=4, `MaxSweepStrandOffsetRatio`=2, all × the relevant radius) — an uncontainable curve is refused, not silently under-margined. No `AnalyzeLipschitz` step-clamp hook: the margin lives INSIDE the shape's own returned distance, not the chain/program stepScale. Fixed-point mirror (`SdfFieldEvaluator.SdfSweep`) supports ONLY `strands == 1` (refused by name at `Compile()` for a higher count — the multi-strand orbit and its per-strand min are render-only) and finds the closest t by SAMPLE-AND-REFINE (9 fixed candidates + 5 halving-step compass probes) rather than the shader's closed-form cubic solve, since the trigonometric (three-real-root) branch needs `acos`, absent from `FixedQ4816` — both approaches answer the same question and feed the same margin, so they need not agree bit-for-bit. `SdfSolidPrimitive.Sweep` carries NO `SdfSolidGeometry.Reach` unit-scale law (its own control points already carry creation-unit dimensions) — `SdfSolidGeometry.SweepReach`/`AppendScaledPrimitive`'s dedicated Sweep branch (REQUIRES a uniform scale, refused otherwise) cover it instead; NOT a closed solid (refused for contact/collider emission by name, alongside Panel/Trims/Flare/Shear/Bumps/Domain at validation — `ShapeCurveDocument`, `puck.creation.v1`'s `curve` facet). No Post stage (quarantine) |
| Builder methods `RoundedRectangle`/`RegularPolygon`/`Star`/`Trapezoid`/`Ellipse` (`SdfProgramBuilder`) + `SdfProgram.TryGetLocalBound` cases / `LiftedBoundRadius` helper | exact 2D cores `sdfRoundBox2D`/`sdfTrapezoid2D`/`sdfStar2D` (shared by RegularPolygon's m=2 case and Star)/`sdfEllipse2D`, lift ops `sdfExtrude2D`/`sdfRevolve2D`, lifted wrappers `sdfRoundedRect`/`sdfPolyStar`/`sdfTrapezoidSolid`/`sdfEllipseSolid` + their `evaluateShape` cases | evaluation + bounds for the family — each shape earns a REAL cull bound (unlike the approximate Ellipsoid #6); exact + factor-1 Lipschitz throughout (no `AnalyzeLipschitz` step clamp needed): extrusion is always exact, revolution is exact off-axis and a harmless conservative bound near the axis. Post: `world-2d-family` (both lift modes, cross-backend, `WorldHighContrast`) |
| `SdfOp.CellJitter` (id 22) / `SdfProgramBuilder.CellJitter(spacing, jitter, seed, tumble, materialVariants, flavor)` — Data0.xyz = spacing (HOST-CLAMPED ≥0.001/axis), Data0.w = jitter (peak-to-peak), Data1.xyz = 1/spacing (HOST-BAKED), Data1.w = clamped tumble [0,1], Material = materialVariants, Shape = seed, **Blend lane (header.z) = `SdfNoiseFlavor` {White=0 byte-identical default, Blue=1 R3 fixed-point low-discrepancy, Gaussian=2 central-limit}** — flavor reshapes ONLY the POSITION offset r0 (tumble/material-variant unaffected); `AnalyzeLipschitz`'s dedicated case (`chainTranslateReach += (sqrt(3)/2) * \|Data0.w\|`, treated exactly like a Translate of that magnitude — the per-axis half-amplitude combines as a VECTOR, since `chainTranslateReach` is a Euclidean-length sum; summing the per-axis `0.5` as a scalar under-counts a jitter-under-a-warp chain and lets the over-relaxed march overstep. Tumble/fold are isometries so nothing else accumulates) | `SDF_OP_CELL_JITTER` (22u) in `mapCore` — repeats like `SDF_OP_REPEAT`, then per-cell hashed position jitter (branched on `SDF_NOISE_*` = header.z), an optional hashed tumble (isometric rotation gated on `data1.w > 0`), and an optional hashed material-variant recolor, all keyed off `sdfPcg3d` (canonical PCG3D on the two's-complement cell index xored with the header seed) | stochastic domain-repeat fold — scatters a prototype into a jittered field from one instruction. Exposed to `puck.sdf.v1` as the geometric-only `cellJitter` op (no materialVariants lane, so the positional-recolor repair the document door refuses to inherit is unreachable); the document decoder's `Replay` appends a trailing `ResetPoint` so a dangling fold can never leak into the next emitter's chain. The hash is INTEGER-ONLY, so cell decisions are bit-identical across both DXC targets; displacement and tumble are BOTH isometries (distanceScale untouched — only the jitter half-amplitude joins `AnalyzeLipschitz`, as a reach term, not a warp rate). ALL THREE flavors keep r0 in [0,1)^3, so the offset stays within ±jitter/2 per axis — the SAME bound White has — so NO Lipschitz change (the reach-independent `L_cj` clamp stays conservative for every flavor); Blue's lattice is INTEGER-ONLY (`asuint` + uint mul-add) so it too is bit-identical cross-backend. `AnalyzeSegment` gives it the `default` case (space-folding op, no world-space sphere is sound past it, segment not skippable — do NOT add a dedicated case). In-cell rule: jitter/2 + prototype reach ≤ min(spacing)/2, REFUSED at `Build()` by name (`CellJitterLipschitz` sees both halves; the old silent margin clamp collapsed `stepScale` toward ~1e-5 and rendered the WHOLE composed field as an immediate-accept solid — the dark-dome failure). `WorldSdfDocumentEmitter.Load` wraps the dry-build so a document violating it is a `world.sdf.load` rejection (`BuilderRejectedProgram`), never a crash. ⚠Containment ≠ nearest-copy (verified 2026-07-08, slice capture): even with the in-cell rule satisfied, the single-cell `round` fold can pick the WRONG copy near a cell wall (a copy jittered toward the boundary is nearer to the adjacent cell's query than that cell's own copy), so the field OVERestimates at boundaries — visible seams, grazing-angle hole risk; keep jitter conservative. The same wrong-neighbor class applies to plain `Repeat`: exact ONLY for an on-center prototype within half-spacing per axis; an off-center/oversized prototype creases the field at cell walls with a march-holing overestimate (`SdfProgramBuilder.Repeat`'s doc carries the contract; iq's 3^k neighbor check judged NOT worth the interpreter cost at current usage). Post: `world-cell-jitter` (parity) + `world-cell-jitter-solidity` (single-backend GPU solidity) |
| `SdfOp.RepeatPolar` (id 23) / `SdfProgramBuilder.RepeatPolar(count, axis = SdfPolarAxis.Y, mirror = false, materialStride = 0)` — Shape = `SdfPolarAxis` {X, Y (default, XZ ground plane), Z}, Blend (header.z) = mirror flag, Material = per-sector stride, Data0 = (angle = 2π/count, 1/angle, count, 1/count) ALL HOST-BAKED, Data1 reserved | `SDF_OP_REPEAT_POLAR` (23u) in `mapCore` — folds the plane perpendicular to the axis into `count` equal angular sectors (nearest-sector `round` on the angle, like `SDF_OP_REPEAT`'s cell fold), an optional per-sector mirror (reflection across the sector bisector), then an optional per-sector material recolor | angular domain-repeat fold — the rotational sibling of `Repeat`/`WallpaperFold`: one authored prototype repeats around the axis (gears, wheels, rotunda columns, clock ticks, petals). The fold is a rotation (+ optional mirror reflection), BOTH isometries, so it is EXACTLY 1-Lipschitz — factor 1, NO `AnalyzeLipschitz` step clamp, same as `Repeat`/`WallpaperFold` (unlike `CellJitter`'s reach term or `LogSphere`'s `exp(w/2)` factor). Post: `world-repeat-polar` (cross-backend parity, Vulkan SPIR-V vs Direct3D 12 DXIL) |
| `SdfOp.Displace` (id 24) / `SdfProgramBuilder.Displace(frequency, amplitude)` — a FIELD op, ordered after the shapes it displaces; Data0.xyz = frequency, Data0.w = amplitude | `SDF_OP_DISPLACE` (24u) in `mapCore` — `result.distance += amplitude·sin(fx·x)·sin(fy·y)·sin(fz·z)` at the current folded point, evaluated in the same FIELD-op slot as `SDF_OP_ONION`/`SDF_OP_DILATE` | sine-product surface relief — the SDF-native height/parallax map, except the relief is REAL geometry (self-shadows/occludes). Separable basis, deterministic float trig (±1 LSB like the twist/bend warps) — parity-safe with no hashed noise table; the integer-hash fBm sibling is `NoiseDisplace` (id 29). NOT 1-Lipschitz: gradient reaches `amplitude·‖frequency‖`, so `AnalyzeLipschitz` folds `1 + amplitude·‖frequency‖` into `chainDisplaceWarpProduct` (a reach-independent metric-stretch factor, the same channel `DomainWarp` multiplies into — like the log-sphere product). Post: `world-displace` (parity) + `world-displace-solidity` (single-backend, the clamp holds the over-relaxed march) + the `sdf-lipschitz` stepScale assert |
| `SdfOp.NoiseDisplace` (id 29) / `SdfProgramBuilder.NoiseDisplace(frequency, amplitude, octaves, gain, lacunarity, seed)` — a FIELD op, ordered after the shapes it displaces; Data0 = (frequency, amplitude, gain, lacunarity), Data1.x = HOST-BAKED `1/Σ gainᵏ` normalization (the octave sum stays in [-1, 1] before amplitude), Shape = seed, Blend = octave count (≤ `MaxNoiseOctaves` = 8) | `SDF_OP_NOISE_DISPLACE` (29u) in `mapCore` — fBm over `sdfValueNoise3` (3D value noise: one integer-only `sdfPcg3d` per lattice corner keyed on the two's-complement cell xored with the per-octave seed streams, quintic-smoothed trilinear blend), and the analytic-gradient dual in `mapGradCore` via `sdfValueNoise3Grad` (KEEP the pair IN SYNC) | bound-preserving hash-lattice noise relief — the fBm/gradient-noise deferral is CLOSED (this row is the integer-hash basis `Displace` deferred to). Cell decisions are bit-identical cross-backend (integer hash); the blend is float mul/add (±1 LSB — silhouette winner flips only, inside the relaxed envelope). NOT 1-Lipschitz: `AnalyzeLipschitz` folds `1 + \|amp\|·freq·(15/4)·√3·Σ(gain·lacunarity)ᵏ/Σgainᵏ` into `chainDisplaceWarpProduct` (`NoiseDisplaceLipschitz`); outward surface reach is `\|amplitude\|` (`MaxScopedFieldReach`), and the op joins Onion/Dilate/Displace in every field-op classification (unmaskable when unscoped, parked-refusal, shadow-transparency). Exposed to `puck.sdf.v1` as the scoped-only `noiseDisplace` op (refused outside a push/pop pair — an unscoped document field op would displace the whole composed world field), and to `puck.creation.v1` as the creation-level `noise` facet (`CreationNoiseDocument`; static stamps only — `WorldPlacementStamper` emits it inside the stamp's field scope as its own shape-free chain, animated/attached/inhabited/look uses refuse at validation, and the canonicalizer refuses a declaration whose derived step factor exceeds `CreationNoiseDocument.MaxStepFactor`, computed by the shared `SdfProgram.NoiseDisplaceStepFactor`). `AnalyzeLipschitz` pass 2 folds a SHAPE-FREE chain's Displace/NoiseDisplace factor additively at the op's own instruction site (a shape-free chain never reaches a ShapeBlend compose, so pass 1's chain product alone would drop it; the op-site fold keeps the addition inside the scope the op acts on, so a Union pop keeps it instance-local instead of summing across instances — and the pop's baked 1/L scale then keeps the whole scope's tax off the global stepScale). Both Displace and NoiseDisplace take a FAR-BAND fast path in mapCore/mapGradCore (KEEP the four case bodies IN SYNC): the relief is bounded by \|amplitude\|, so past `4·\|amplitude\|` of accumulated field the op subtracts \|amplitude\| instead of evaluating — a valid conservative lower bound; the full evaluation runs only near the band that can matter. Note the band rarely fires for a camera standing ON the noised surface (every eval is near-band there) — do not author noiseDisplace on large walkable surfaces expecting the skip to save the frame; shape the walkable relief from geometry and keep noise for silhouette masses the camera stays off. Watch Ellipsoid eccentricity when authoring terrain: a flattened ellipsoid's eccentricity is a GLOBAL march factor too, and a 25:3.2 pancake costs ~7.8× on its own — build broad pads from exact cylinders and keep ellipsoids near-round. Creation-document shapes are exempt since 2026-09-03: every World stamper scopes an eccentric shape (`SdfSolidGeometry.StepFactor > 1` — a non-uniformly scaled `Sphere` baked as an `Ellipsoid`) inside its own `PushField`/`PopField`, or rides the group's/creation's scope, so the pop's 1/L clamps only that candidate (`WorldStampPool.EmitShape`/`GroupNeedsScope`, `CreationStampEmitter.EmitShapeChain` with `inScope`, the static probe reserving the pair). The shipped world went from `stepScale 0.625` to 1 with unchanged geometry (80 → 65 ms on the RTX 2060). `SdfProgram.StepScaleBinder` names the depth-0 chain that binds a global scale below 1 and `world.budget` echoes it ("bound by instance N (Ellipsoid x1.6 at instruction M, unscoped)"), so a program-emitting path that forgets the scope is visible, never silent. Stripped under `SDF_CORE_OPS` (views-core bytecode is byte-identical). Laws: `tests/Puck.SignedDistance.Tests/SdfNoiseDisplaceLawTests.cs` (bitwise step-clamp mirror, identity, refusals) + `tests/Puck.World.Tests/CreationNoiseLawTests.cs` (facet doors, scope emission, op-site clamp fold); no Post stage exists (quarantine) — cross-backend agreement was measured by hand on the real windowed world |
| `SdfOp.CellDisplace` = 35; `SdfCellDisplacement`, builder, validator, `SdfFieldEvaluator.Cells`; `SdfViewsKernelVariants.FirstHeavyTouch` | `SDF_OP_CELL_DISPLACE`, `sdfCellDistanceGrad`, scalar and dual cases under `SDF_STRIP_HEAVY` | Data0=(frequency, amplitude, randomness, 0), Shape=uint seed, Blend=F1/F2MinusF1 (0/1). Fixed 27-cell PCG3D search, top 16 hash bits per feature coordinate. Centered jitter ceilings: F1 0.46, F2MinusF1 0.20. Add amplitude*(F-0.5); gradient bounds 1/2 respectively, outward reach amplitude/2. Restore a continuous bounded sampling frame before relief; low-level unsupported folds refuse. Shape authoring restores its rigid frame and requires an available per-shape scope. Pin exactness against a 125-cell reference. |
| `SdfOp.AxialProfile` = 30; `SdfProgramBuilder.AxialProfile` | `SDF_OP_AXIAL_PROFILE`, scalar and dual cases | Shape selects axis 0..2; Data0=(amount, bulge, origin, inverseSpan), Data1=(distanceCorrection, startScale, 0, 0). s(t)=startScale+amount*t+bulge*sin(pi*t), t=clamp((origin-p[axis])*inverseSpan,0,1). Divide the perpendicular coordinates by s. Bound the Jacobian and expand preceding warp reaches in reverse composition order. Render-only. |
| `SdfOp.Shear` = 31; `SdfProgramBuilder.Shear` | `SDF_OP_SHEAR`, scalar and dual cases | Shape selects target, Blend selects distinct driver (axes 0..2); Data0.xyz=(linear, quadratic, cubic). Add the polynomial to the target coordinate. Its derivative bound includes all three terms over the composed reach. Author as `shear { linear, quadratic, cubic, target, driver }`. Render-only. |
| `SdfOp.GaussianPush` = 32; `SdfProgramBuilder.GaussianPush` | `SDF_OP_GAUSSIAN_PUSH`, scalar and dual cases | One instruction: Data0=(center.xyz,push.x), Data1=(radii.xyz,push.y), Shape=push.z float bits. p -= push*exp(-length((p-center)/radii)^2); analytic Jacobian and bound 1+length(push)*sqrt(2/e)/min(radii). Positive radii and finite push required. Opcode 33 remains retired. Render-only. |
| `SdfOp.DomainWarp` (id 25) / `SdfProgramBuilder.DomainWarp(frequency, amplitude)` — a POINT op, ordered before the shapes it warps; Data0.xyz = frequency, Data0.w = amplitude | `SDF_OP_DOMAIN_WARP` (25u) in `mapCore` — `localPosition += amplitude·(sin(fx·y), sin(fy·z), sin(fz·x))`, each axis driven by the NEXT axis's coordinate (non-separable), before the wrapped chain evaluates | cross-coupled organic domain warp — deterministic float trig, same parity posture as `Displace`. NOT an isometry: the Jacobian is `I` plus a perturbation of spectral norm ≤ `amplitude·‖frequency‖`, so the SAME `1 + amplitude·‖frequency‖` clamp joins `chainDisplaceWarpProduct`, and the point's max travel (`amplitude·√3`) additionally folds into a downstream twist/bend's reach term. Post: `world-domain-warp` (parity) + `world-domain-warp-solidity` (single-backend, the clamp holds the march) + the `sdf-lipschitz` stepScale assert |
| `SdfOp.SymmetryPlane` (id 26) / `SdfProgramBuilder.SymmetryPlane(normal, offset = 0f)` — Data0.xyz = the UNIT plane normal (host-normalized), Data0.w = the plane offset | `SDF_OP_SYMMETRY_PLANE` (26u) in `mapCore` — `p -= 2·min(dot(p, n) + offset, 0)·n`; for `n = x̂, offset = 0` this is `abs(p.x)` to the bit, an exact superset of the RETIRED `SDF_OP_SYMMETRY_X` | arbitrary-plane reflection fold — the general-normal fold that REPLACED the `SymmetryX`/`SymmetryY`/`SymmetryZ` opcodes (ids 13–15 collapsed into id 26; the builder keeps `SymmetryX/Y/Z()` as sugar that emit it): everything on the plane's negative side mirrors onto its positive side, so one authored half repeats mirror-imaged across ANY plane (a kaleidoscope leaf, a bilateral body, the reflect atom of a KIFS fold). A reflection is an ISOMETRY, so it is EXACTLY 1-Lipschitz — factor 1, NO `AnalyzeLipschitz` step clamp, same as `WallpaperFold`/`RepeatPolar`. Post: `world-symmetry-plane` (cross-backend parity, Vulkan SPIR-V vs Direct3D 12 DXIL) |
| The **Glyph op** — `SdfShapeType.Glyph` (SHAPE id 15, the next free shape after `ScreenSlab`=14) / `SdfProgramBuilder.Glyph(uvBottomLeft, uvTopRight, halfWidth, halfHeight, extrudeHalfDepth, distanceScale, material, blend, smooth)` + `SdfProgramBuilder.Text(atlas, text, origin, right, up, worldEmHeight, …)` (lays out via `Puck.Text.TextLayout`, emits one `ResetPoint`+`Translate`+`Rotate`+`Glyph` SEGMENT per char — the SdfVm→Puck.Text edge). LANE LAYOUT: Data0 = (`packedUvMin`, `packedUvMax` [each host-baked unorm2x16 of an atlas UV — packing frees a lane so Data1.x keeps the ISA-wide smooth], `distanceScale` [= atlas `DistanceRange`(texels) × worldPerTexel, HOST-BAKED], `extrudeHalfDepth`); Data1 = (`smooth`, `halfWidth`, `halfHeight`, 0). Uploaded ONCE via `SdfWorldEngine.SetGlyphAtlas(rgba, w, h)` (an `IGpuSurfaceUpload`), threaded through `ISdfFrameSource.GlyphAtlas` (`SdfGlyphAtlas` record, default null) polled once in `SdfEngineNode.EnsureEngine`. | `SDF_SHAPE_GLYPH` (15u) in `evaluateShape`, guarded on `SDF_GLYPH_ATLAS` (defined ONLY by `sdf-world-views.comp` — every other kernel gets the conservative extruded-quad fallback `sdfGlyphQuad`, so the beam cull/rt-debug see a solid cell box, never a hole). `sdfGlyph`: exact 2D quad distance `dQuad` FIRST, atlas tapped ONLY inside the band (`dQuad < 0.5·distanceScale`), `dPlane = max((0.5 − encoded)·distanceScale, dQuad)` then extruded — the band-cull is BOTH the perf trick and the conservative far field. Field from ALPHA (the true single-channel distance) via manual bilinear (`sdfGlyphSampleField`, `SampleLevel` explicit-LOD, s32/t39 combined-image-sampler at Vulkan binding 44 — DERIVED as `ScreenSourceBindingBase + MaxScreenSurfaces`, appended after the 32 screen sources in `SdfWorldEngine.viewsBindings` so D3D12 registers land t39/s32). | text as REAL world geometry: marchable, blendable, ENGRAVABLE (Subtraction) / EMBOSSABLE (Union proud of a slab — NEVER coplanar or the coincident zero-sets speckle) / floating. Reconstruction: GEOMETRY MARCHES THE TRUE SINGLE CHANNEL (alpha) — median-of-3 is C0-only at clash lines and must never be marched (the flat-coverage `GlyphDecal` tier LANDED 2026-07-09 — a SEPARATE material-level tier that samples the SAME atlas's ALPHA with a coverage threshold at SHADE TIME on a `ScreenSlab` carrier, NOT marched geometry: a per-screen decal table + shared cell buffer `sdfDecalCells` at Vulkan binding 45 / D3D12 t40 (after the glyph atlas t39; DERIVED as `GlyphAtlasBindingIndex + 1`), `SdfWorldEngine.SetScreenDecal`/`ClearScreenDecal` ↔ `sampleScreenSurface`'s decal-first branch, the `ISdfFrameSource.ScreenDecals` per-frame seam; Post `world-glyph-decal`; world-glyph geometry stays untouched, byte-identical when no decal is declared — an MSDF atlas would let the decal median-of-3, the alpha is what it samples now). Generation NOW: `Puck.Text.SdfCoverageAtlas.Generate` — an EXACT separable Euclidean distance transform (Felzenszwalb–Huttenlocher, deterministic) over a GDI+ coverage raster; the chamfer(1,√2) alternative overestimates ≤8.24% off-axis and would need a 1/1.0824 step-scale penalty, so exact-EDT + uniform worldPerTexel keeps Glyph FACTOR-1 (1-Lipschitz in texel space, bilinear preserves it — NO `AnalyzeLipschitz` case, like the 2D-lift family; a stretched cell is the caller's risk). Recommended marchable source is a pre-baked `msdf-atlas-gen` MTSDF atlas (true-distance in alpha by construction) — the runtime EDT is the no-toolchain fallback. Post: `world-glyph` (cross-backend parity, `WorldHighContrast` — sampled-texture/material-seam family; the fixture atlas is a deterministic in-process 5×7 font, no font-availability dependency; a no-atlas control proves the atlas reaches the shader). Adapted from SignedDistanceTerminal's `sdfMsdfGlyph`. |
| The **SampledRegion op** — `SdfShapeType.SampledRegion` (SHAPE id 16, the next free shape after `Glyph`=15) / `SdfProgramBuilder.SampledRegion(boxMin, cellSize, dimX, dimY, dimZ, brickWordOffset, boundaryFloor, material, blend = Subtraction)` (`MaxSampledRegionDim = 1023`). LANE LAYOUT: Data0 = (`boxMinX`, `boxMinY`, `boxMinZ`, `cellSize`) — box extent derives as `dims·cellSize`; Data1 = (`smooth` [ISA-wide, = 0 for the hard subtraction a brick composes with], `packedDims` [uint bits: 3×10-bit dims ≤1023/axis, host-packed `dimX \| dimY<<10 \| dimZ<<20`], `brickWordOffset` [uint bits: the brick's base word in the pool], `boundaryFloor` [= margin/λ, host-baked outside-box lower-bound offset]). The two uint bit-fields ride the float lanes as reinterpreted bits (like Glyph's `PackUv`) and round-trip exactly through `WriteVector4`. `TryGetLocalBound` returns the box CIRCUMSPHERE (center = boxMin + extent/2, radius = |extent|/2) — a REAL cull bound, so `AnalyzeSegment`/`ShapeReachRadius`/`PackInstances` treat it as any Subtraction-blend instance and `IsShadowTransparentInstance` auto-flags it (Path B). `AnalyzeLipschitz` = factor 1 EXACTLY (λ is folded into the STORED values at bake, not `stepScale`), so brick-free scenes stay byte-identical AND a brick adds no global step tax. | `SDF_SHAPE_SAMPLED_REGION` (16u) in `evaluateShape` (NOT stripped under `SDF_CORE_OPS` — the core-ops views variant binds the pool), guarded on `SDF_SAMPLED_REGIONS` (the world-views + core-ops + beam kernels bind the pool as of **W0b**; the instance-cull/rt-debug/diagnostic kernels take the fallback). `sdfSampledRegion`: `local = (p−boxMin)/cellSize`; OUTSIDE the box returns `dist(p,box) + boundaryFloor` (a valid scaled lower bound — positive, so Subtraction stays saturated and the accumulator is exact); INSIDE, manual TRILINEAR over 8 `sdfBrickPool` loads (sample CENTRES at integer voxel indices, `sampleCoord = local − 0.5`, clamp-to-edge border half-voxel) with a `precise` lerp chain (fp-contraction pinned OFF → bit-stable SPIR-V/DXIL). WITHOUT `SDF_SAMPLED_REGIONS` (the instance-cull/rt-debug/diagnostic kernels): returns `SDF_FAR_DISTANCE` (the conservative UNION-HULL fallback — a Subtraction compose never bites, region renders uncarved, never holed — the Glyph quad-fallback precedent). WITH `SDF_SAMPLED_REGIONS` but a POOL-LESS engine (capacity-0 filler): `sdfSampledRegion` calls `sdfBrickPool.GetDimensions` and, seeing the single-float filler (`numVoxels <= 1`), takes the SAME `SDF_FAR_DISTANCE` fallback — so a filming view renders a SampledRegion world UNCARVED. ⚠GROUND TRUTH: the stored brick distances are `/√3` scaled, so a ZEROED read (an allocated-but-UNBAKED 64 MB pool, or a filler sampled without the gate) = stored distance 0 = the box interior sitting entirely on the carve surface ⟹ the Subtraction carves a box-shaped HOLE across the whole region. This was a LIVE defect for filmed carves: every offscreen filming view once allocated its own default 64 MB pool it never baked into, so filming a carved world rendered the carve box as a hole (and wasted ~4 GB at the 64-view cap). The GetDimensions gate + capacity-0 view engines fix both. Normals: the `evaluateShapeGradient` `default` arm's 4-tap FD (4 extra pool samples, hit-only). Pool: `[[vk::binding(46,0)]] StructuredBuffer<float> sdfBrickPool` (one f32/voxel), per-consumer D3D12 register via `SDF_BRICK_POOL_REGISTER` (views set t41 after `sdfDecalCells` t40; beam t4 after its mask t3 — the `SDF_INSTANCE_MASKS_REGISTER` pattern). | a SAMPLED distance-field brick: the settled-carve UNION field baked O(1) so the primary/shadow/AO marches stop paying O(carve-count), composed as ONE ordinary Subtraction instance (crack-free by construction — the subject stays fully analytic). W0a shipped the ISA + shape eval; W0b landed the engine tier — the persistent device-local pool (`SdfWorldEngineOptions.BrickPoolVoxelCapacity`, default 64 MB = `SdfWorldEngine.DefaultBrickPoolVoxelCapacity` = `SdfBrickPoolLayout.TotalVoxels`; frozen at construction, 0 = no pool: baking and rendering are SPLIT — a pool-less engine still ACCEPTS a SampledRegion program (rendered uncarved via the GetDimensions fallback, see the sdfSampledRegion row), only `RequestBrickBake` stays a loud rejection), the static `SdfBrickPoolLayout` (8 slots × 128³), the closed-form sphere-union baker `sdf-brick-bake.comp` (distances stored `/√3`, sliced ≤256K voxels/frame off the render's frame-timing bracket), and the `RequestBrickBake`/`GetBrickState` API with the two-revision-bump handoff (`BrickBakeState` Empty→Baking→Ready). `SdfViewsKernelVariants` classifies SampledRegion as CORE so a baked carve scene keeps the faster core-ops variant. ⚠ editing `sdf-world-views.comp.hlsl` does NOT reliably retrigger the `sdf-world-views-core.comp` recompile (it includes, not `#include`s a `.hlsli`) — the stale-bytecode gotcha bit W0b once; delete + rebuild the core `.spv`/`.dxil` after touching the views source. The planner (`SdfCarveBakePlanner`) is W1a. The carve-bake plan document was deleted 2026-08-02; there is no plan of record for the remaining carve-bake work, and the `world-sampled-region` stage that checked it went with `Puck.Post`'s quarantine — this shape is unverified by machine. |
| The **ChamferedRectangle shape** — `SdfShapeType.ChamferedRectangle` (SHAPE id 17, the next free shape after `SampledRegion`=16) / `SdfProgramBuilder.ChamferedRectangle(halfWidth, halfHeight, chamfer, lift, liftAmount, material, blend, smooth, rounding)`. SAME lane layout as `RoundedRectangle`: Data0 = (`halfWidth`, `halfHeight`, chamfer `c`, lift amount); Data1 = (`smooth`, lift mode, UNUSED, edge-rounding radius `r` — the family-wide fillet, applied on top of the chamfer via `ClampRounding` against `SdfProgramBuilder.ChamferedRectangleInradius(halfWidth, halfHeight, c)` = `min(halfWidth, halfHeight, (halfWidth+halfHeight-c)/√2)`). Chamfer itself clamps to `[0, min(halfWidth, halfHeight)]` and, for an extrude, to the lift half-height (`SdfProgramBuilder.ClampChamfer` — the cap bevel rides the same `c`, so a chamfer past the half-height would cut the side faces and shrink the mid-plane outline) — the profile limit degenerates the profile to a diamond/octagon, which is allowed. `c = 0` reduces both the 2D core and the extrude join to the plain box/slab forms exactly (byte-identical). | `SDF_SHAPE_CHAMFERED_RECT` (17u) in `evaluateShape` (a CORE shape, no strip guard, same as `SDF_SHAPE_ROUNDED_RECT`). `sdfChamferBox2D(p, b, c)`: `q = abs(p) - b`; `d = max(length(max(q,0)) + min(max(q.x,q.y),0), (q.x+q.y+c)·SDF_SQRT_HALF)` — exact inside/on the surface, a 1-Lipschitz conservative lower bound outside in the wedge past each bevel vertex (the nearest point is the vertex, not either plane — the chamfer blend's class of bound; rendering and contact need exactly a lower bound). `sdfChamferedRect` lifts it exactly like `sdfRoundedRect` (extrude via `sdfExtrude2D`/revolve via `sdfRevolve2D`) then `- data1.w` for the family rounding lane. The EXTRUDE cap seam additionally bevels via `sdfExtrudeChamfer2D(d, pz, h, c) = max(sdfExtrude2D(d,pz,h), (w.x+w.y+c)·SDF_SQRT_HALF)` (`w = (d, abs(pz)-h)`) — a chamfered box therefore reads chamfered on all twelve edges, not only the four the 2D profile cuts. `sdfExtrudeChamfer2D` also threads through `sdfRoundedRect`/`sdfTrapezoidSolid`/`sdfEllipseSolid` as a CAP-ONLY bevel riding their previously-UNUSED Data1.z lane (RegularPolygon/Star keep Data1.z for their baked `ecs.y` and do not carry it) — zero (every pre-existing program) is the identity. | 45-degree edge chamfers as a general shape/edge treatment (armor plating, beveled panels), not a one-off. 1-Lipschitz — no `AnalyzeLipschitz` step clamp, like the rest of the 2D-lift family. `Puck.SignedDistance.Queries.SdfFieldEvaluator` mirrors the full lane (`SdfChamferBox2D`/`SdfExtrudeChamfer2D`/`SdfChamferedRectangle`, plus the same Data1Z cap-chamfer thread on `SdfRoundedRectangle`/`SdfTrapezoidSolid`), so a chamfered shape stays admissible for deterministic field contact — unlike `RegularPolygon`/`Star`/`Ellipse`, it is NOT added to the field-contact profile refusal. `SdfSolidGeometry.MaxChamfer` is the authoring-door ceiling for the creation-document `chamfer` field (Box/Cylinder/Prism-extrude only; a Prism's ceiling is the profile's TRUE inradius — `SdfProgramBuilder.TrapezoidInradius` for the trapezoid, so a triangle prism admits its inradius where `MaxRounding`'s erosion ceiling admits nothing — and the extrude half-depth; a `Prism` with a `Polygon` profile reads zero — `RegularPolygon`'s Data1.z has no free lane for an independent cap chamfer — and a `ChamferedRectangle` profile reads zero too, since its own profile chamfer already covers the cap, and an extruded one is refused by name where that chamfer exceeds the half-depth). A document Box with `chamfer` emits the chamfered rectangle at the plain Box arm's own zero-set half-extents with the plain Box's `BoxRound·min(scale)` fillet riding the rounding lane on top of the chamfer, so the zero set, the collider, and the `chamfer → 0` limit all agree with the plain Box (a chamfer under `fillet·(2 − √2)` is absorbed by the fillet and emits the plain filleted Box). `SdfPrismProfileKind.ChamferedRectangle` is the Prism PROFILE alternative (its `cornerRadius` fraction maps to `c`, exactly as `RoundedRectangle`'s does) — orthogonal to the `chamfer` field, which bevels an EXISTING profile's cap rims instead of choosing the profile shape. |
| The **scoped accumulator** — `SdfOp.PushField` (id 27) / `SdfOp.PopField` (id 28); `SdfProgramBuilder.PushField(compose = Union, smooth = 0f)` / `PopField()` (depth cap `SdfProgramBuilder.MaxFieldScopeDepth = 1`; the compose blend + smooth ride the POP instruction's Blend lane + Data1.x — the SAME lanes a `ShapeBlend` uses; PUSH carries no data) | `SDF_OP_PUSH_FIELD` (27u) / `SDF_OP_POP_FIELD` (28u) in `mapCore` (`SDF_MAX_FIELD_SCOPE_DEPTH = 1u`) — PUSH saves the running accumulator into a one-deep `(savedFieldDistance, savedFieldMaterial)` slot and reseeds `result` to `SDF_FAR_DISTANCE`; POP restores the parent as the blend LHS and feeds the scope's `result.distance` as a CANDIDATE into the **shared blend tail** (the material-winner switch + `blendShape`) SHAPE now also uses — so a POP costs no second copy of the ten-way blend switch (`composePending` gates the tail) | one-deep SCOPED FIELD ACCUMULATOR — the fix for "a field op / intersection shells the WHOLE scene": every accumulator-reading op (the intersection family, `Onion`/`Dilate`/`Displace`) between a balanced `PushField`/`PopField` acts on the scope's own shapes ONLY, then composes back with the POP's blend. A scope touches the FIELD, never the POINT (`localPosition`/`distanceScale`/`parityMaterialDelta`), so `ResetPoint` is unchanged and per-shape cull bounds after the Push stay sound. THE FUSION TRAP: a POP's candidate is ALREADY in world units — it is NOT re-multiplied by `distanceScale` and does NOT take `parityMaterialDelta` (unlike SHAPE). THE PER-SCOPE STEP CLAMP: `AnalyzeLipschitz` bakes each scope's own Lipschitz bound onto its POP as a `1/L_scope` candidate scale in the instruction's FREE Data1.y lane (patched in place before packing), and mapCore/mapGradCore (and the CPU evaluator's pop) multiply the scope's field by it at the pop — a positively scaled distance keeps its zero set and the scaled candidate is exactly 1-Lipschitz, so a scoped warp/relief/eccentricity taxes only its own candidate's march, never the GLOBAL stepScale. A factor-1 scope stays unpatched (Data1.y = 0 reads as no scale), keeping existing programs byte-identical; the global stepScale still covers everything UNSCOPED (an eccentric ellipsoid outside any scope is a global march factor — keep decor ellipsoids near-round; the World stampers scope creation-document eccentricity automatically and `world.budget` names any unscoped binder, see the NoiseDisplace row). Material tie-break is strict `<` (parent keeps its material on a tie). `AnalyzeSegment` gives a Push/Pop segment `segmentEligible = false` (never whole-skip a scope boundary) but leaves `chainBoundable` TRUE (correction #1 — bounds after the Push survive); `HasUnmaskableCompose` tracks scope depth so a SCOPED field op / intersection is NO LONGER unmaskable (the culling payoff — only a POP with an intersection-family compose at depth 0 is), and `MaxSmoothBlendRadius` folds a POP's soft compose halo. THE MARGIN RULE (the payoff's fine print): a scoped field op is maskable but GROWS the surface OUTWARD past the authored geometry bound, so `PackInstances` must inflate the instance's finite bound by that reach or the beam masks the tiles the grown shell reaches and the surface HOLES at the tile seams — `MaxScopedFieldReach` folds it in the same way `MaxSmoothBlendRadius` folds the POP compose halo (per-op: `Onion(t)` outer surface moves out by `t`, `Dilate(r)` by `r`, `Displace(a)` by `a`; field ops SUM within a scope, max across scopes; an UNscoped field op stays unmaskable, so its 1e30 sentinel covers it and no margin is computed). Verified 2026-07-08 by a scoped-`Dilate(1.5)` sphere with a bound covering only the un-dilated radius: pre-fix the beam clipped the shell into a blocky tile-truncated blob, post-fix the full dilated sphere renders intact. `AnalyzeLipschitz` folds a chamfer compose through the SAME per-composition recurrence a chamfer `ShapeBlend` takes, so repeated pops accumulate (`MaxFieldScopeDepth = 1` forbids nesting, not sequencing). Op-unused (scope-free) programs stay BYTE-IDENTICAL (verified: overworld render sha256-identical). Post: `world-scope` (scoped intersection renders as the intersection of its own members; a scoped instance is maskable with `instanced == flat`; its near-endpoint cluster + the CPU pin also prove `blendSmoothUnion`'s FAR + NEAR endpoints — the scope-seed prerequisite; there is NO separate `sdf-blend-endpoints` stage) |
| `SdfInstruction.Detail` (bool, default false) — every `ShapeBlend`-emitting `SdfProgramBuilder` method (Box/Capsule/Cylinder/Ellipsoid/Plane/RoundCone/Sphere/Torus/Vesica, the 2D-lift family) carries a trailing `detail` parameter that reaches `Shape()`, the one construction site; NOT exposed on `ScreenSlab`/`SampledRegion`/`Glyph`. `SdfProgram`'s packer ORs it into the instruction's Shape lane's otherwise-unused high bit (`ShapeDetailFlag` = 0x80000000u) at pack time — the typed `Instructions` seam stays a clean field, only `Words` carries the bit. `SdfProgram.ValidatePackedContract` refuses Detail on any op but `ShapeBlend` by name. `TryCompileRigidSegment` refuses to collapse a segment containing a Detail instruction into the rigid-leaf plan (that fast path evaluates every leaf unconditionally, with no mode check), forcing it through the generic per-instruction walk where the flag is honored | `SDF_SHAPE_DETAIL_FLAG` (0x80000000u) / `SDF_SHAPE_TYPE_MASK` (0x3FFFFFFFu, shared with the SECONDARY-EXCLUDED bit below) in sdf-vm.hlsli — `SDF_OP_SHAPE`'s case in BOTH `mapCore` and `mapGradCore` (KEEP the two skip checks IN SYNC) tests the flag on `instructionHeader.y` before doing any other work and `break`s (composes nothing) unless `sdfDetailShadingActive` (a static, default false, mirroring `sdfShadowMaskActive`'s pattern) is set; every other decode of `instructionHeader.y` as a shape type masks with `SDF_SHAPE_TYPE_MASK` first. sdf-world.hlsli's `renderView` sets the static true for exactly two hit-only spans: the `needsNormal` block (so the analytic/tap/curvature normal all see detail geometry) and one extra `mapMasked` re-evaluation right before `sdfMaterialLoad(material)` (so a detail shape's own material — and its smooth-seam blend weight — wins its footprint), both false again immediately after | SHADING-ONLY shapes: skipped by the beam cone march, the fine march, shadow, and AO (every march-mode call, which is every call outside those two hit-only spans), so a seam or rivet too thin for the footprint-relative march to resolve at distance never dots out — it is never marched at all, only shaded at an already-found hit. A Detail shape therefore never carves the silhouette, never appears in `SdfFieldEvaluator`'s contact field (below) or a creation's collider (`CreationStampEmitter.EmitFixed`/`VisitFixedPrimitiveCopies` skip a Detail `ShapeDocument` outright, as does `WorldDefinitionValidator`'s solid-collider count), and adds no march-time cost beyond the flag check — the hit-only re-evaluation is ONE extra field eval per LIT pixel (not per march step), paid by every program compiled with this views kernel whether or not it declares a Detail shape (no program-level "has detail" gate exists to skip it; unmeasured on real hardware). Authored as `ShapeDocument.Detail` (`puck.creation.v1`; null = false), refused by name alongside `Panel` or `Trims` on the same shape (both would need a second, independently-Detail-flagged copy this facet does not describe) — `CreationStampEmitter.EmitShapeChain`/`Client.WorldStampPool.EmitShape` thread it into `SdfSolidGeometry.AppendScaledPrimitive`'s shape emission only, never into a shape's own panel/trim copy. `Puck.SignedDistance.Queries.SdfFieldEvaluator` mirrors the march-side exclusion directly (its `CompiledInstruction` carries the same flag; a flagged `ShapeBlend` composes nothing, and a program whose only shape is flagged reads exactly as shape-free — `TryDistance`/`Overlap` answer as they would for an empty stream), since it has no shade-mode counterpart of its own. Post: no stage exists (quarantine); unverified by machine beyond `tests/Puck.SignedDistance.Tests`/`tests/Puck.World.Tests` and a look at the real window |
| `SdfInstruction.Secondary` (bool, default true) — Detail's OPPOSITE exclusion set. `SdfProgramBuilder.MarkSecondary(bool)` (never a `Shape()` parameter — it patches the MOST RECENTLY emitted `ShapeBlend` instruction in place via `with { Secondary = ... }`, and throws if that instruction is not a `ShapeBlend`, so it must chain directly onto the shape method that emitted it, before any field op) is the one mutation site. `SdfProgram`'s packer ORs `!Secondary` into the Shape lane's next-highest bit (`ShapeNoSecondaryFlag` = 0x40000000u, below Detail's). `ValidatePackedContract` refuses `Secondary = false` on any op but `ShapeBlend` by name; `TryCompileRigidSegment` refuses a non-secondary instruction into the rigid-leaf plan for the same reason Detail does (no mode check on that fast path) | `SDF_SHAPE_NO_SECONDARY_FLAG` (0x40000000u) in sdf-vm.hlsli — `SDF_OP_SHAPE`'s case in BOTH `mapCore` and `mapGradCore` tests it right after the Detail check and `break`s WHEN `sdfSecondaryMarchActive` (a static, default false) IS set — the inverse sense from Detail's flag (Detail skips unless shading is active; Secondary skips only while a secondary-ray march IS active). sdf-world.hlsli's `renderView` sets the static true for exactly the lifetime of its `softShadowVisibility`/`calcAO`/`calcFastAO` calls (both branches of the shadow ifdef, and the whole `ambientOcclusion` ternary — see `shadingStepScale` in the gradient-scaling row below), false immediately after each | a shape marches for the camera/beam/fine march and both hit-only shade re-evaluations exactly like an ordinary shape — it drops out ONLY of the soft-shadow and ambient-occlusion field walks (the study's eyelids/small parts: still shaded and collided, they just cast no shadow and cost no AO tap). `CreationStampEmitter.EmitFixed`/`VisitFixedPrimitiveCopies` do NOT skip a non-secondary shape (the opposite of Detail's collider exclusion) — its collider and `SdfFieldEvaluator` contact answer are unchanged whether Secondary is true or false, since neither has a shadow/AO concept of its own. Authored as `ShapeDocument.Secondary` (`puck.creation.v1`; null = true), with NO Panel/Trims restriction (unlike Detail — it packs its own bit on the SAME instruction, never a second copy) — `CreationStampEmitter.EmitShapeChain`/`Client.WorldStampPool.EmitShape` chain `.MarkSecondary(...)` directly onto `SdfSolidGeometry.AppendScaledPrimitive`'s return, before any `Dilate`/`Onion`. Post: no stage exists (quarantine); unverified by machine beyond `tests/Puck.SignedDistance.Tests`/`tests/Puck.World.Tests` and a look at the real window |
| `Puck.SignedDistance.Queries.SdfFieldEvaluator` (GRAVITY ARC Wave 1, `IWorldQuery`+`IFieldEvaluator`, the SECOND `IWorldQuery` provider after `BakedWorldQuery`) — a WARP-FREE CPU interpreter of the live `SdfProgram.Instructions` typed seam (not the packed `Words`), in `FixedQ4816`/`FixedVector3`. Ctor walks the stream once, asserting every op/shape is in the supported rigid subset (throws `ArgumentException` naming the first excluded one) and converting each instruction's Data0/Data1 floats to `FixedQ4816` ONCE into a cached `CompiledInstruction[]` — including a `Rotate`'s baked quaternion, transcribed via `rotatePointByInverseQuaternion`'s cross/mul/add form (no runtime sin/cos). It also refuses NON-UNIFORM `Scale`: the GPU's minimum-axis correction is a safe march lower bound, not Euclidean physical clearance. `SdfSolidGeometry.AppendScaledPrimitive` therefore bakes authored anisotropy into native Box, Sphere/Ellipsoid, axis-symmetric Capsule/Cylinder/Cone, and Plane spellings before a render/contact stream is shared; unsupported anisotropic primitive spellings fail loudly at field construction. `TryFieldGradient` is a 6-tap per-axis central difference over `TryDistance` (still not a `mapGradCore` dual port; the original 4-tap tetrahedron form of Decision B was replaced when its edge-aliasing — a spurious tangential normal component with a two-equal-components fingerprint at blend corners — was measured driving a deterministic tangential runaway in the wall-contact solve); the five `IWorldQuery` verbs sphere-trace `TryDistance` (`Exact` on convergence, `Bounded` on the three non-convergences: a RADIUS cast whose scaled field no longer clears its radius, the iteration budget running out, and a marched point the program's frame cannot express). TWO seam contracts that are NOT the shader's: (1) `TryDistance` evaluates the WHOLE `FixedPosition`, rebased against the world origin via `TryDelta` — the identity inside cell (0,0,0), correct across cells, and the reason a body past ±524,288 units (where `FixedPosition.FromLocal` carries a cell on its own) no longer reads the cell-0 field; (2) a march that exhausts its iteration budget resolves per VERB, by what that verb's TRUE half asserts — `Raycast`/`SphereCast` report a hit at the last marched point with `WorldQueryConfidence.Bounded` and `LineOfSight` reports BLOCKED, because "clear" is the assertion authoritative consumers (NPC visibility, `FixedFieldContactSolver.ResolveCore`) cannot survive being wrong about, while `TryGroundHeight` returns FALSE: it asserts a SURFACE, hands back a bare coordinate with no confidence channel, and a caller grounding a body on a fabricated Y is moved somewhere the world does not have. A shape-free program still MISSES rather than reading solid. THE MARCH APPLIES `SdfProgram.StepScale` (converted to `FixedQ4816` ONCE at construction, like every other program float): the interpreted OP subset is 1-Lipschitz but the BLEND TAIL is not — a chamfer, or an eccentric `Ellipsoid`, makes the field overestimate, and a raw advance tunnels a thin plate. Scale the FIELD then subtract the radius (`f·s − r`), never the clearance (`(f − r)·s` shrinks the radius too and is anti-conservative for a `SphereCast`). The raw clearance still owns exact convergence, but the SCALED clearance owns whether separation is proven: `Overlap` compares the directed-down product `floor(f·s)` with the radius, and a cast advances by `max(floor(f·s), one Q48.16 tick) − r` only while that value remains positive. It never floors an unproved advance upward. The tick floor sits on the FIELD, before the radius comes off, and exists because the accept arm tests the RAW field against `HitEpsilon` (raw 66) while the stop arm tests the SCALED field against zero: below `s = 978/65536` (~0.0149) the stop threshold sits ABOVE the accept threshold, so an unfloored descent stalls one raw tick short of a surface it has already proven is inside `HitEpsilon + 1 tick` and `TryGroundHeight` answers "no ground" over every column. Floored, a POINT cast always advances and can only overstep the true surface by less than one tick (1/66 of the accept band); for any radius of one tick or more `max(floor(f·s), tick) − r ≤ 0` exactly when `floor(f·s) − r ≤ 0`, so SPHERE casts are bit-identical and still never advance into the contact envelope. `StepScale` likewise converts by a directed floor; an extreme positive scale below one Q48.16 tick becomes zero, authorizing no scaled advance at all — a radius cast is `Bounded` at its origin, a point cast is `Bounded` after the one-tick reach, and a shape-bearing overlap is occupied — rather than inventing a larger unsafe multiplier. The iteration budget derives from `BaseMarchIterations · HitEpsilon / max(floor(HitEpsilon·s), one Q48.16 tick)`, keeping point-cast reach invariant at `512 · HitEpsilon` = raw 33,792 while bounding an extreme program at 33,792 iterations. `Overlap` treats a failed world-origin rebase as occupied for a shape-bearing program and false for a shape-free one | `mapCore`'s RIGID op cases (`SDF_OP_RESET`/`_TRANSLATE`/`_ROTATE`/`_SCALE`/`_REPEAT`/`_REPEAT_LIMITED`/`_SYMMETRY_PLANE`/`_ELONGATE`/`_ONION`/`_DILATE`/`_PUSH_FIELD`/`_POP_FIELD`/`_SHAPE`) + `evaluateShape`'s Sphere/Box/ScreenSlab/Torus/Plane/RoundCone/Capsule/Cylinder/Ellipsoid/Vesica/RoundedRectangle/Trapezoid bodies + `blendShape`/`blendSmoothUnion` — the shared blend tail's semantics, INCLUDING op-order effects (a strict material-winner compare before the distance blend), mirrored exactly | a SECOND, INDEPENDENT interpreter of the SAME instruction stream mapCore walks (a deliberate dual implementation, like `SdfProgram`'s own host-side `AnalyzeBounds`/`AnalyzeLipschitz` passes — NOT shader codegen). WARP-FREE means it rejects `TransformDynamic` (no per-frame dynamic-transform table in this evaluator's signature — a future wave could thread one through without touching any other op's status), `BendX`/`BendY`/`BendZ`/`TwistY`/`LogSphere`/`CellJitter`/`RepeatPolar`/`Displace`/`DomainWarp` (runtime trig this wave doesn't implement in fixed point — but `SymmetryPlane`/`RepeatLimited`/`RepeatPolar` each have a rigid-copy spelling in `SdfDomainExpansion`, which is how the contact paths carry a fold this evaluator cannot walk), and `WallpaperFold` (isometric and so tractable in principle, but its 17-group parity-keyed cell logic was judged real added surface, not a five-minute mirror — Wave 1's reconciliation finding: the plan's initial excluded-op list named 9 ops from `AnalyzeSegment`'s bound-skip default-case partition, which is a SUPERSET reflecting a DIFFERENT concern — "no sphere bound is sound past this op" — not "uninterpretable"; `Repeat`/`RepeatLimited`/`SymmetryPlane`/`Elongate`/`Onion`/`Dilate` and ISOTROPIC `Scale` are directly interpreted here as 1-Lipschitz operations). Three shapes are excluded for the same reason at the shape level (not itemized in the arc plan, a Wave 1 finding): `RegularPolygon`/`Star` (`sdfStar2D`'s runtime `atan2`) and `Ellipse` (`sdfEllipse2D`'s analytic cubic solve, `acos`/`pow`); `Glyph` needs texture sampling, while `SampledRegion` needs the engine-owned brick pool. `RoundedRectangle` is supported by mirroring the shader's exact `sdfRoundBox2D` plus lift wrapper. Gravity = `-gradient.Normalize()` is the CONSUMER's one-line derivation (`IFieldEvaluator`'s whole reason to exist as its own seam) — the field itself never encodes "planet" or "down". Verified (a Wave 1 scratch harness, not committed): hand-computed sphere/translated-box/rotated-capsule/SmoothUnion points match to <5e-7 (float-rounding-of-the-input floor, not fixed-point error); a 200-point random sweep vs. an independent double-precision reference measured max\|err\| ≈ 2.3e-5 for sphere and box; `TryFieldGradient` on a sphere at 10 points (axes, diagonals, near-degenerate) measured max\|err\| ≈ 2.2e-3 (measured against the retired 4-tap tetrahedron probe — the 6-tap central difference that replaced it has O(eps^2) truncation instead of O(eps) curvature aliasing; RE-MEASURE before freezing any gradient threshold) against the analytic radial unit vector, well inside GradientEpsilon's documented 0.01-world-unit probe; 1000 seeded points evaluated twice against a multi-op program (Translate+Rotate+Box+ResetPoint+SmoothUnion-Sphere) were BIT-IDENTICAL (0 mismatches on the raw `FixedQ4816.Value`) — the live `tests/Puck.SignedDistance.Tests` gate now pins direct query regressions, while the broader determinism/drift measurements remain without a live Post gate |
| `DynamicTransform.CastsSoftShadow` (DynamicTransform.cs; default `true` = casts) → `SdfWorldEngine.PackDynamicTransforms` packs it into the dynamic transform's POSITION row `.w` lane (0 = casts, 1 = shadow-suppressed) — the lane that was a hardcoded 0 pad, so a default-casts frame is BYTE-IDENTICAL | `sdfShadowParticipationActive` (a `static bool`, false default, declared under `SDF_DYNAMIC_TRANSFORMS` beside `sdfShadowMaskActive` in `sdf-vm.hlsli`) flipped `true`/`false` UNCONDITIONALLY around the ONE `softShadow` call in `sdf-world.hlsli` (matching `sdfShadowMaskActive`'s lifetime) → the per-instance skip in `sdfNextVisibleInstanceRange` (`sdfShadowParticipationActive && meta.x == SDF_BOUND_DYNAMIC && sdfDynamicTransforms[2u*meta.y].w > 0.5 ⟹ continue`, mirroring the parked-radius skip) + the gather-side twin `sdfInstanceShadowSuppressed` skip in `sdfShadowGather`'s two candidate loops (gated on the RAW condition — the gather runs BEFORE the flag flips and is inherently shadow-scoped) | per-frame per-instance soft-shadow PARTICIPATION — a suppressed dynamic instance drops out of the soft-shadow march ONLY (camera/AO/coverage marches keep the flag false and are untouched; static instances have no dynamic slot and always cast). Default = casts, byte-identical for every existing consumer; no program rebuild (it rides the per-frame dynamic-transform upload). Consumer: `Puck.World.Client`'s `WorldSceneEmitter` computes it per entry (local seats always cast; a stand-in casts iff within `WorldRenderSettings.ShadowCrowdRadius` of a joined seat — the 128-player crowd lever, `world.shadows [tier] [crowd-radius]`). The three soft-shadow fallback modes (gather cull / camera-tile / flat) all resolve through `sdfNextVisibleInstanceRange`, so the flag is set unconditionally to cover all three. ⚠ editing `sdf-world.hlsli`/`sdf-vm.hlsli` needs the `sdf-world-views-core.comp` `.spv`/`.dxil` deleted before build (the include-not-#include stale-bytecode gotcha). No dedicated Post stage (a demo/World-greenfield lever — the default-casts path keeps `world-shadow-cull`/`world-swarm` bit-identical) |
| `SdfVolume`, `SdfVolumeKind.Flow`, `SdfWorldEngine.PackVolumes`, `SdfVolume.VectorsPerEntry` = 10 | `shade-volumes.hlsli`, ten float4 rows per entry | Rows: position/slot; quaternion; halfExtent/axis; width/speed/seed/steps; intensity/extinction/rampCount/intensityLane; pulse amplitude/frequency; four RGB/density ramp stops. At most eight volumes, each with 1..4 ordered density stops. Integrate authored emission and extinction inside the oriented box, clip at opaque depth, and use the exact zero-extinction limit. Optional intensityLane is anonymous 0..3; absent means unit gain. |


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
> p4m; gated by Post `world-wallpaper-p4g` (single-cell translation invariance, period-1 not period-2). For every
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
> `UnmaskableBoundRadius`: no cull bound can contain it, and a parked one throws. Gated by `world-instanced`'s
> intersection guard (its scene authors a deliberately under-covering bound the packer must override; note a merely
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
    `study` field naming a `views.studies` row) is `puck-world`'s to describe.
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
cells. Backs two Post
stages, both measured-first and frozen at that measured reality, never
tightened unasked: `world-field-evaluator-determinism` (Tier A — three
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

`dotnet build src/Puck.SdfVm -c Release` runs DXC IN PLACE in the source tree
(build FAILS without DXC; `/p:DxcCommand=` overrides) — commit the
regenerated `.spv`/`.dxil` with the source change. Editing `sdf-world.hlsli`
or `sdf-vm.hlsli` recompiles `sdf-instance-cull.comp`, `sdf-beam.comp`,
`sdf-world-views.comp`, `sdf-sky.comp`, AND `sdf-cull-args.comp`.

**Stage 1 compiles THREE variants** (`SdfViewsKernelVariant`, selected per
program at `UploadProgram` — walk order Full → Folds → CoreOps): the full-ISA
reference (`sdf-world-views.comp`), the fold-ops middle tier
(`sdf-world-views-folds.comp`, `#define SDF_FOLD_OPS` — folds/scopes/simple
exotic shapes kept, the HEAVY warp/noise family stripped), and the core-ops
strip (`sdf-world-views-core.comp`). The strip macros in sdf-vm.hlsli are a
two-tier ladder: `SDF_STRIP_ALL_EXOTIC` (core only) and `SDF_STRIP_HEAVY`
(core + folds; TwistY/Bend*/LogSphere/CellJitter/Displace/DomainWarp/
NoiseDisplace and the RegularPolygon/Star/Trapezoid/Ellipse shape bodies).
KEEP the two sets IN SYNC with `SdfViewsKernelVariants.FirstHeavyTouch`/
`FirstExoticTouch` — a case stripped under a macro must send `Select` to a
fuller variant. The folds tier is why a world full of pattern folds and
grass scopes no longer pays the full interpreter's ~38% occupancy: measured
~1.5× on views for the shipped world.
`ValidateShaderBytecodeSources` fails the build on bytecode without a
same-stem `.hlsl` (Puck.SdfVm only; the other shader-shipping projects lack
the guard — a known follow-up).

**The MASK-FIRST pass order (the uniform-grid instance-cull arc), now preceded
by the sky pre-pass.** SEVEN kernels per frame: `sdf-frame-upload.comp` (2026-09-03:
copies this frame's host-written viewport rows, dynamic transforms, and frame
instance grid from the ring slot's HOST-VISIBLE buffers into single
DEVICE-LOCAL twins, one uint per thread — `SdfWorldEngine.RecordFrameUpload`,
the `upload` timing pass, ~0.02 ms — because every march kernel used to bind
the host-visible ring buffers directly and fetch across PCIe per sample: the
instance-cull walk per tile, `sdfShadowGather` per lit pixel, `mapCore`'s
`sdfDynamicTransforms` read on every dynamic-instance evaluation. The ring
buffers stay the CPU's write target; the twins are what the beam/cull/views
sets bind. Measured on the RTX 2060 shipped world: mask 5.2 → 4.0 ms, views
−2 to −4 ms at the floor tier) → `sdf-sky.comp` (fills every
non-child viewport's source pixel with `skyColor(cameraRayDirection(...))` —
direct, not indirect, over the full render-dims rect, so a tile the beam
later culls already holds real sky rather than stale device memory; it
shares Stage 1's own bindings array/descriptor set — see the "procedural
sky" sync-pair row) → `sdf-instance-cull.comp` (per-tile instance mask — the
host-built CSR uniform grid from `SdfInstanceGrid`, bin-by-CENTER with the
LOAD-BEARING `footprintPad` = max binned radius; dynamic/unmaskable instances
ride an always-tested list; a disabled grid falls back to the flat
per-instance loop, forced by `SdfProgramBuilder.Build(buildInstanceGrid:
false)` / the demo's `sdf.grid off` verb) → `sdf-beam.comp` (cone march over
the TILE-MASKED field via `mapMasked` — bit-exact per the bound-sizing
contract because a masked-out instance's bound excludes the tile's whole
cone; this is what flattened the O(instances) beam wall: 187.8→6.6 ms @4096,
119→1.0 ms @1024 scattered carves) → `sdf-cull-args` → views → composite.
The compositor (`sdf-world-composite.comp`) no longer carries an empty-tile
flattening constant or a cull-buffer binding of its own — every source pixel
is real content every frame, so it is a plain copy/upsample with no tile-cull
knowledge.
The instance cull is deliberately NOT fused into the beam (its register
footprint cost the cone march ~12% occupancy, measured), and it uses direct
mask-buffer bit writes, NOT a per-thread accumulation array (512 B/thread
scratch, also measured worse). `sdfInstanceMasks`' D3D12 register is
per-consumer: Stage 1 t13 (default), the beam t3 via
`SDF_INSTANCE_MASKS_REGISTER` before the include. Timing pass labels are
`["upload", "sky", "mask", "beam", "cull-args", "views", "composite"]` (`SdfWorldEngine.PassLabels`;
`TimingCapacity` 8 is now exactly the mark count, so the next pass label needs the pool widened);
the bench's beam column reports beam+mask so ladders stay comparable, and "views"
is now a pure Stage-1 march number (the cull-args reduction closes its own mark). Gated by
`world-grid-cull` (grid==flat bit-identical via the destructible-slab scene)
plus the existing instanced==flat stages.

## Gotchas (verified, expensive to re-learn)

- **Use compiled-shader reload during iteration.** After `CompileShaders` finishes,
  `world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf` queues the primary
  node's next-frame reload; `world.shaders.status` distinguishes pending from
  applied/unchanged/failed. Do not reboot for HLSL-only edits. The engine stages
  changed pipelines, drains the frame ring, validates beam/full/core/fold ISA,
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
  (gated by `world-shadow-cull`) yet restricted to the shadow ray's neighbourhood,
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
  material-winner flips appear as isolated multi-LSB deltas. The calibrated
  threshold families encode these signatures (`WorldLsbExact`,
  `WorldHighContrast`, `WorldFuzz` — Demo+Post copies KEEP IN SYNC); the hero
  `world` stage stays strict as the canary. Parity posture is RELAXED by
  default (user decision 2026-07-03); `PUCK_PARITY_STRICT=1` opts into
  pixel-perfect. Never re-tighten unasked.
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
- **The shadow/AO de-scale is GRADIENT-SCALED, on top of the program stepScale
  clamp, not instead of it.** `sdfDeScaleField`'s divide-back corrects for the
  program's own worst-case Lipschitz clamp (`stepScale`); it says nothing about
  how far a given SHAPE's own formula departs from a unit SDF at the hit (an
  approximate `Ellipsoid`'s directional gradient, `FlareY`'s y-varying shear).
  `renderView` additionally reads the hit's LOCAL field gradient magnitude —
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
  `stepScale` alone); `src/Puck.World/Assets/studies/moth.glsl`'s `surfaceGradient`/`shadow`/
  `ambientOcclusion` is the reference posture (the study's `distanceScale`
  correction, computed once at the primary hit and reused for the whole
  secondary march — a deliberate approximation this engine also makes).
- SlopeCap)`,
  disjoint-sphere validation of every relaxed step, hit and escape decisions
  on validated samples only, and a validated reach exit; a `ShadowStepMin`-
  floored step is never treated as relaxed (thin-occluder stepping must not
  ping-pong with the validation). Measured HQ frame 165 → 154 ms (DX12) on top
  of the ceiling removal below, captures inside the noise floor. It also has
  no step ceiling (`ShadowStepMin` is the only clamp). The old
  `max(0.6, 0.15·t)` ceiling was the closest-approach parabola's need for dense
  samples; the estimator is BINARY now, sphere tracing never advances past the
  clearance, and the escape exit closes open rays, so the ceiling only bought
  samples (~15 per lit ground pixel where 4 or 5 suffice). The FAST path keeps
  its own ceiling because its wider stride is a soundness trade. Measured: the
  high-quality frame 200 → 165 ms (DX12) together with the AO loop; captures
  inside the noise floor. `sdf-world-rt-debug` keeps ITS ceiling on purpose (a
  calibrated parity probe).
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
  parity probe — do NOT migrate it. Gated by `world-analytic-normal` (the op-chain
  scene: twist+repeat+scoped-onion+smooth) plus every existing world stage, which
  now render analytic by default.
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
