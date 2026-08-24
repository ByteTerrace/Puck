---
name: sdf-world
description: Working on the SDF VM and world renderer — src/Puck.SignedDistance (SdfProgram/SdfProgramBuilder, the packed instruction ISA, and Puck.SignedDistance.Queries' deterministic fixed-point interpreter behind Puck.Maths' IWorldQuery/IFieldEvaluator seams) and src/Puck.SdfVm (SdfWorldEngine/SdfEngineNode, the Assets/Shaders/Sdf kernels, the shared render assembly SdfWorldRenderSpec/SdfWorldRenderBuilder, the Puck.SdfVm.Debug inspection engine, the composition/anchor surface (ISdfSceneEmitter/SdfCompositionFrameSource/SdfMaterialScope/SdfAnchor), and Puck.SdfVm.Views (ViewStack/camera rigs)). Use whenever touching the SDF ISA or packed word layout, the world kernels or their HLSL includes, engine capacities/frames/screen sources, render-assembly/backend selection, the SDF debug/gallery/bench tooling, composing a world program from emitters, anchor/view/camera-rig plumbing, deterministic world queries, or debugging world-render parity or GPU cost. Carries the C#↔HLSL contract pairs and settled engine semantics so they aren't re-derived or accidentally forked.
---

# The SDF world: one contract, two languages

Factual and procedural only: settled contracts, their exact sync points, and
how to verify. The user's current instruction outranks it — if this file
argues against a demanded change, it is stale; update it in the same change.
The render-assembly reference that used to describe its boundary, capacity
envelope, content seams, and unsupported graph requests was deleted on
2026-08-02 and has no replacement — read `SdfWorldRenderSpec`/
`SdfWorldRenderBuilder` directly.

> **Unification-contract alignment** (see docs/vision.md): world content is
> authored and loaded in-session — the `world.row.set`/`world.row.step`
> document-row mutation verbs and `world.load`/`world.save` — never only
> through a CLI flag. `Puck.World` has no content-authoring flags at all.

> **`Puck.Demo.*` symbols below are recorded history.** `Puck.Demo` is
> quarantined under `experimental/`: read it as prior art, but it may not be
> built, run, or revived in place. The contracts stated
> here were accurate when written and are kept because they explain why the
> engine seams have the shape they do. They are not live collaborators, they are
> not available to call, and **nothing plans re-homing them into `Puck.World`** —
> the port plan that would have was deleted. Where this file names a
> `Puck.Demo.*` type as the only implementation of something, read that as: the
> capability is absent from the running product.

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
| `SdfIsa.Version` + `SdfProgram.ValidateIsa` + `SdfWorldEngine` initialization (via `SdfShaderSetVerification`) | `sdf-isa.hlsli` + the report branches in `sdf-beam.comp.hlsl`/both views variants + both `mapCore` dispatch switches | v1 ISA identity is reported by the actual production interpreter bytecode through a runtime GPU readback, cached per device+shader-set; mismatch refuses before `UploadProgram`, an undeclared host opcode refuses by numeric id/instruction index, and an unknown GPU opcode returns the diagnostic material instead of falling through |
| `SdfProgram` packed `Words` layout, op/shape/blend enums | `sdf-vm.hlsli` decode (`evaluateShape`, op switch) | instruction stream |
| `SdfProgram.InstanceMaskWordCount` (`max(1, ceil(n/32))`) | `sdfInstanceMaskWordCount` (reader's INNER word iteration only) | mask width formula |
| `SdfWorldEngine` pushWords[7] = LIVE uploaded program's width | `CompositeParams.instanceMaskWordCount` / `worldInstanceMaskBase` (sdf-world.hlsli) | mask buffer INDEXING (entry width + tile base) — host-pushed, never shader-derived |
| `PushConstantByteLength` = 32 B, words 0..7 | `CompositeParams` (8 uints: extent, tileGrid, viewportCount, childMask, screenMask, instanceMaskWordCount) | Stage 0/1 push |
| `DynamicTransformByteLength` = 32 B/slot | `sdfDynamicTransforms` (2×float4: position, quaternion) | dynamic transforms |
| `SdfProgramBuilder.MaxInstances` = 16384 | `SDF_MAX_INSTANCES` | instance cap (mask = 512 words/tile; everything DERIVES from the LIVE count via `InstanceMaskWordCountFor`, so a smaller program packs byte-identically — only the shader's `min(count, SDF_MAX_INSTANCES)` clamp constant tracks the cap. Raised 4096→16384 2026-07-09: the deferred-pending-measurement gate was the survey-row-15 uniform-grid cull, which landed mask-first and flattened the beam wall; the raise's only static cost is the ~41 MB mask buffer. The bench notes holding those measurements were deleted 2026-08-02 and nothing can reproduce them: the benchmark project was quarantined the same day, so no instrument exists) |
| `SdfProgramBuilder.MaxScreenSurfaces` = 32 (raised from 8, the many-eyes arc leg 1; was 4 pre-Arc-3); material sentinel `ScreenMaterialId + 1 + screenIndex`; capped at 32 by the single-`uint` `screenMask` push word | 32 combined-image-sampler bindings (`screenSource0..31` at bindings 12-43 / registers t5-t36, samplers s0-s31; `sdfInstanceMasks`/`sdfScreenLights` shifted to t37/t38; the glyph atlas to binding 44 / t39 / s32) — the run is DERIVED (`ScreenSourceBindingBase + i`, `BuildScreenSourceBindings`), never hand-listed, so the descriptor pool auto-sizes from `GpuDescriptorPoolSizes.ForSets`; `SdfScreenLightEnv`/the screen-light buffer + grid rows all key off `MaxScreenSurfaces`; the HLSL side names the width `SdfScreenSurfaceCount` and `SdfScreenLightEnv` derives from it | diegetic screens. THE SENTINEL BAND IS CLOSED ON BOTH SIDES: `Build()` accepts only `ScreenMaterialId` (plain, reads no side table) or `ScreenMaterialId + 1 + i` for an `i` a DECLARED `SdfScreenSurface` occupies, and `sampleScreenSurface` bounds `screenIndex` against `SdfScreenSurfaceCount` before touching `screenSurfaces[]`/`sdfDecalCells[]` — the sibling of `sdf-world-rt-debug`'s `hitMaterial >= SDF_SCREEN_MATERIAL` guard, and for the same reason (D3D12 zeroes an OOB structured-buffer read by spec; Vulkan defines it only under `robustBufferAccess`). A surface's `Right`/`Up` MUST be unit and orthogonal, and its `HalfWidth`/`HalfHeight` strictly positive — both refused by `ScreenSlab`, by the `SdfProgram` ctor, and by `WorldDefinitionValidator` — because the shader projects the hit onto the two axes and DIVIDES by their half-extents (`dot(local, right)/right.w`), while the slab's geometry and the server's `WorldColliderSet.ScreenBox` ride the frame derived from them. The slab's DEPTH half-extent stays unconstrained: nothing divides by it |
| `SdfWorldEngine.SetScreenSurface(index, origin, right, up, halfW, halfH)` — writes a host mirror; DIRTY-GATED (2026-07-16, perf plan Phase 1.2): the call compares against the mirror and is a no-op unless a value actually changed, so a MOVING screen slab (a walking creature's face) still samples correctly every frame `SdfEngineNode` polls per-index transform providers via `ISdfFrameSource.ScreenSurfaceTransforms` (default-implemented), while a static/unchanged poll costs no upload. Per-ring-slot dirty bits (`m_screenSurfaceDirty`, same pattern as `m_decalDirty`) — a real change dirties EVERY slot, `PrepareFrame` uploads + clears only the current slot's bit, so no slot ever renders a stale table. The screen-LIGHT buffer stays unconditional (excluded on purpose — see plan) | `screenSurfaces` StructuredBuffer read per pixel — NO kernel change was needed | moving screens |
| `SdfWorldEngine` screen-light buffer via `SetScreenLight` + `SdfFrame.AmbientScale/SunScale` (entries cover screens 0..31 + env — sized by `MaxScreenSurfaces`) | `sdfScreenLights` (t38, after `sdfInstanceMasks` t37; the glyph atlas t39 and `sdfDecalCells` t40 follow) + `SdfScreenLightEnv` (= 32) decode; the `renderView` light loop iterates all 32 | per-frame screen glow + room dimming |
| `SdfViewSnapshot.RenderScale` (default 1) → `PackViewports` quantizes ONE `RenderScaleQ` byte (1..255; child slots forced 255) into ViewportData's 6th float4 row (`ViewportByteLength` = 96 B) AND packs it 8-bit into `CompositeParams2.scaleQPacked` (`BuildCompositePush`) | `ViewportData.renderScale.x` + `worldRenderDims` (`max(1,(dim·q+127)/255)`, INTEGER — beam/instance-cull/views all derive the identical reduced extent) ↔ the composite's `scaleQPacked` unpack + bilinear upsample (`q == 255` = the exact-copy path, byte-identical) | per-view render scale — presentation-only downscale (reveal/immersed policy lives in `ScreenLayoutDirector`); native is bit-exact BY CONSTRUCTION. Post: `world-render-scale` (blur-envelope, calibrated live) |
| Grid-lock overlay (`GridOverlayState` record struct, `Puck.SdfVm` root namespace since 2026-07-10 — the `From(SnapConfig,…)` factory stays demo-side as `Puck.Demo.Editing.GridOverlayFactory` → `SdfFrame.GridFlags`/`GridWorldPitch`/`GridFloorY`/`GridObjectOrigin`/`GridObjectFrame`/`GridObjectPitch`/`GridObjectPatchRadius`, packed by `SdfWorldEngine.PackScreenLights` into `sdfScreenLights` rows 9..12; `ScreenLightByteLength` = `(MaxScreenSurfaces + 5)` float4) | `sdfScreenLights[SdfGridWorld=9]` (x=flags bit0 world/bit1 object, y=floorY, zw=world pitch XZ), `[SdfGridObjOrigin=10]` (xyz origin, w pitch X), `[SdfGridObjFrame=11]` (frame quat), `[SdfGridObjParams=12]` (x pitch Z, y patch radius); the `applyWorldFloorGrid`/`applyObjectGrid` tints at the `renderView` material call site (guarded `#ifdef SDF_SCREEN_SOURCES`) | the editors' grid visualization — env row 8 STAYS put (it doubles as the screen-count loop bound); adding a grid lane touches BOTH sides + `PackScreenLights`. Session-only authoring state (never sim/wire format); default 0 = byte-identical upload |
| The **tile-cull plane layout** — `SdfWorldEngine.TilePlaneCount` (= 4; sizes `m_tileBuffer` = `TilePlaneCount · viewportCap · tileGridX · tileGridY` floats) | `WorldTilePlaneCount` (= 4u) + `worldTileMarchStartIndex` (plane 0, no stride) / `worldTileFirstExitIndex` (1·stride) / `worldTileSecondEntryIndex` (2·stride) / `worldTileFarBoundIndex` ((count−1)·stride) in sdf-world.hlsli; stride = `tileGrid.x·tileGrid.y·viewportCount` | the four-bound teleport (Larsson "The Gunk") + the **F1 far bound** — plane 0 = the classic marchStart (the ONLY plane sdf-cull-args + the compositor read, so their indexing is stride-independent), planes 1/2 = the proven-empty gap `[firstExit, secondEntry]`, plane 3 = the far bound. sdf-beam WRITES all four (`TileBounds`), sdf-world-views READS planes 1/2/3. Every plane is a total function (MaxDistance = "no gap/no bound"). Growing the plane count touches `TilePlaneCount` + `WorldTilePlaneCount` + a new accessor on BOTH sides |
| The **F1 FAR BOUND** (perf plan Phase 5.1) — `SdfFrame.DisableFarBound` (default false = ON) packed by `SdfWorldEngine.PackScreenLights` into the far-field row `.x` at `(MaxScreenSurfaces + 7)`; `ScreenLightByteLength` = `(MaxScreenSurfaces + 8)` float4 | producer `coneMarchTileBounds`/`coneMarchFarBound` (sdf-beam) → `TileBounds.farBound` (plane 3); consumer `renderView`'s `if (traveled >= farBound) break;` beside the teleport; lever `sdfScreenLights[SdfFarFieldParams=39].x` → `worldFarBoundDisabled()` (disable pushes `farBound = MaxDistance+1` so the "off" side is exactly pre-F1) | the depth past which a tile's cone cannot produce any FOOTPRINT-ACCEPTED hit through MaxDistance. ⚠ LOAD-BEARING PROOF: the tail proves clearance against the FOOTPRINT-INFLATED threshold `min(map(center), sdfMapStepBound) − (chord + footprint)·t > SurfaceEpsilon`, stepping `≤ clearance/(1 + chord + footprint)` — NOT bare `ConeEpsilon` (the fine march accepts hits up to `footprint·t ≈ 0.001·t`, so an ε-proof is anti-conservative). footprint = `2·view.right.w / rectDims.y`, computed identically in beam (from `regionSizePx`) and views. OUTPUT-IDENTICAL on the shipped shading path (both render skyColor in `[farBound, MaxDistance]`); only step counts + the termination debug view change. March-path change (solidity + parity families + hero canary), re-golden the termination debug view only |
| The **F2 SHADOW LIGHT-SIDE EXIT** (perf plan Phase 5.1) — `SdfFrame.DisableShadowFarExit` (default false = ON) packed by `SdfWorldEngine.PackScreenLights` into the SAME far-field row's `.y` lane at `(MaxScreenSurfaces + 7)` (F1 rides `.x`; no `ScreenLightByteLength` growth) | consumer `softShadow` (sdf-world.hlsli) reads `worldShadowFarExitDisabled()` (`sdfScreenLights[SdfFarFieldParams=39].y`); the exit returns the running `result` when `ShadowSharpness·(clearanceTrue − (reach − traveled)) >= result·reach` | the no-further-darkening early out: `result` is a running MIN and the field is 1-Lipschitz along the ray, so once `clearanceTrue ≥ (reach − traveled) + result·reach/ShadowSharpness` no future sample can lower it. SOUND vs the classic penumbra term AND the true continuous penumbra (`≥ ShadowSharpness·cMin/reach ≥ result`, `cMin = clearanceTrue − remaining > 0`). ⚠ NOT bit-identical: the Aaltonen closest-approach parabola can undershoot past the exit point (its worst case → 0 at the near-radial-escape knife-edge `c'/prev → 2`, just inside the `y≥c` guard), so NO finite margin closes the strong form — skipping it brightens toward truth, never above it. MARCH-PATH change (solidity + parity families) |
| The **procedural sky** — `SdfFrame.SkyEnabled`/`SkyZenithColor`/`SkyHorizonColor`/`SkyGroundColor`/`SkyFogDensity`/`SkySunDiscRadians`/`SkySunDiscIntensity`/`SkyStarDensity`/`SkyStarBrightness`/`SkyStarSeed`, plus `SkyStarTwinkleShare`/`SkyStarTwinkleDepth`/`SkyStarTwinkleRate` and `SkyCloudColor`/`SkyCloudCoverage`/`SkyCloudSoftness`/`SkyCloudScale`/`SkyCloudSeed`/`SkyCloudDrift`/`SkyCloudSpin`/`SkyCloudCurl`/`SkyCloudShear`, packed by `SdfWorldEngine.PackSkyFrame` into nine rows AFTER the five lighting rows (`SdfSunFrameA..SdfAmbientColor`, `MaxScreenSurfaces+8..+12`); `ScreenLightByteLength` = `(MaxScreenSurfaces + 22)` float4; the twinkle rate is HOST-BAKED to a period in engine ticks (`EngineTicks.PerSecond / rate`) and the cloud drift and shear are HOST-INTEGRATED from `SampleIndex` into layer offsets wrapped modulo 4096 and the spin into an angle modulo 2π; the sun-disc `pow()` exponent is HOST-BAKED from `SkySunDiscRadians` (`ln(0.5)/ln(cos(discRadians))`, clamped) | `SdfSkyZenith=45`/`SdfSkyHorizon=46`/`SdfSkyGround=47`/`SdfSkySunStars=48`/`SdfSkyTwinkle=49`/`SdfSkyCloudsA=50`/`SdfSkyCloudsB=51`/`SdfSkyCloudsC=52`/`SdfSkyCloudsD=53` decode via `worldSkyEnabled`/`worldSkyZenithColor`/`worldSkyHorizonColor`/`worldSkyGroundColor`/`worldSkyFogDensity`/`worldSkySunDiscIntensity`/`worldSkySunDiscExponent`/`worldSkyStarDensity`/`worldSkyStarBrightness`/`worldSkyStarSeed`/`worldSkyStarTwinkleShare`/`worldSkyStarTwinkleDepth`/`worldSkyStarTwinklePeriodTicks`/`worldSkyCloudColor`/`worldSkyCloudCoverage`/`worldSkyCloudSoftness`/`worldSkyCloudScale`/`worldSkyCloudSeed`/`worldSkyCloudOffset`/`worldSkyCloudShearOffset`/`worldSkyCloudSpinAngle`/`worldSkyCloudCurl`; `skyColor` branches on `worldSkyEnabled` — false takes the PINNED two-stop gradient through the instructions it held before this section existed (bit-identical); true takes a three-stop gradient plus an additive sun disc plus `sdfStarField` (an octahedral cell grid, `sdfPcg3d`-keyed per cell, hash-placed star position, a second hash of the first giving each star its blackbody tint (`StarSpectrum`, ~3000–15000 K) and its power-law apparent luminosity (`N(>F) ∝ F^-3/2`, floor `StarLuminosityFloor` of the peak) and an optional twinkling share riding `params.sampleIndex % periodTicks` — no texture, no session state) and finally `sdfCloudLayer` lerped over all of it by its coverage mask (a plane at unit height, `direction.xz / direction.y`, turned by the spin angle plus a Coriolis curl `curl · 2r/(1+r²)`, domain-warped four-octave `sdfLatticeNoise` fbm — one `sdfPcg3d` per lattice corner — the shaping fbm read at its own shear offset, thresholded at `1 - coverage` with the authored softness, cores shaded by `CloudCoreShade`, faded below `CloudHorizonFade`; `worldSkyCloudCoverage() == 0` short-circuits) | authored via `render.lighting`/`render.sky` (`Puck.World.WorldRenderLighting`/`WorldRenderSky`). `worldSkyFogDensity` is read UNCONDITIONALLY (independent of the enabled gate) — its pinned default reproduces the retired `FogDensity` shader constant's exact bits, so an absent `render.sky` renders byte-identically to before this section existed. Every accessor carries an `#else` fallback for the `SDF_SCREEN_SOURCES`-undefined kernels, the same shape `worldSunDirection`'s fallback already takes, because `skyColor` (called from `renderView`) compiles into every kernel that includes sdf-world.hlsli regardless of that macro. `sdf-sky.comp` is `skyColor`'s SECOND caller (`renderView`'s miss branch is the first): a direct, un-culled pre-pass that writes `skyColor(cameraRayDirection(view, localUv))` into every pixel of every non-child viewport's source texture before `sdf-beam.comp` culls any tile, so a tile the beam later culls already holds real sky rather than a flat constant — it `#define`s `SDF_SCREEN_SOURCES` FOR THIS REASON (the only configuration under which the real, non-pinned-literal accessors — and `sdfScreenLights` itself — are declared at all), reusing Stage 1's own bindings array/descriptor set rather than declaring a second layout |
| `sdfMaterialShade` takes accumulated `float3` radiance (not a scalar) | `sdfMaterialShade(..., float3 diffuse, ...)` — the two callers (`sdf-world.hlsli`, `sdf-world-rt-debug`) | shade funnel (colored lights) |
| `DebugViewModes.Names` (`Puck.SdfVm` root namespace since 2026-07-10, order IS the wire value, 11 entries incl. mask/overshoot/evals) | `DebugViewModeCount` (= 11)/`DebugViewModeNormals` + the `viewMode` switch (sdf-world.hlsli `renderView`) — mode 10 (`evals`, perf-plan Phase 0 instrumentation) is the one mode besides final shading that forces `useFinalShading` true, so its `sdfEvalCount` tally (a per-thread static, incremented at every map()-family call site in sdf-world.hlsli — never inside mapCore/sdf-vm.hlsli) reflects the real epilogue cost, not a debug shortcut | debug views — adding a mode touches BOTH plus the switch |
| `SdfDriftMonolith.Emit` (`Puck.SdfVm.Debug`, shared verbatim by the Post drift-ceiling stage and the demo gallery's monolith exhibit — CALIBRATED, change only with a recalibration) | n/a (host-side program emission only) | ⚠ the two hex-stride materials are reached POSITIONALLY through the `WallpaperFold` chain's `materialStride`, so `Emit` must be called into a builder holding NONE of the caller's own materials yet — it owns the whole material palette and must be emitted FIRST, or the positional stride reaches the wrong (caller-owned) material |
| bound-analysis modes | `SDF_BOUND_*` skip in `map()` | bounds gate |
| `SdfProgram.AnalyzeLipschitz` → per-program `stepScale` (1/L) baked into the segment-directory header's FREE `.y` lane (`PackBounds`), read back via `SdfProgram.StepScale` | `sdf-vm.hlsli` `mapCore` reads `asfloat(sdfWords[segmentOffset].y)` (guarded `> 0`) and multiplies its FINAL returned distance by it ONCE, after the walk | Lipschitz step clamp — a non-1-Lipschitz warp cannot overstep and hole. The warp factors are NOT one formula: a **Bend** (BendX/Y/Z) keys on a coordinate INSIDE the plane it rotates, so its exact operator norm is `1 + a` (a = rate·ρ), while **TwistY** keys OUTSIDE its rotated plane and collapses to `sqrt((2 + a² + a·sqrt(a²+4))/2)` — using the twist form for a bend under-clamps by up to 24% and HOLES the march (`BendOperatorNorm` vs `TwistOperatorNorm`). Log-spherical: factor `exp(w/2)`. Eccentric ellipsoid: factor = eccentricity. A **chamfer blend** is the one `SdfBlendOp` that is NOT 1-Lipschitz AND the only composition whose bound can exceed BOTH operands: the bevel-arm gradient is `(∇a ± ∇b)/√2`, so composing fields bounded by `La`/`Lb` carries `max(La, Lb, (La + Lb)/√2)`. That recurrence is folded PER COMPOSITION (`ComposeLipschitz`, walked in `mapCore`'s own order by a second pass over the stream), never per chain — a per-chain latch counts one √2 however many chamfers compose and understates by up to `(1 + √2)/√2 = 1.70711×`, which HOLES thin geometry under three or more chamfers. The accumulator seeds at the `SDF_FAR_DISTANCE` CONSTANT (L = 0), so the FIRST chamfer composition is the identity, TWO reach exactly √2 (byte-identical to the latched value), growth starts at the THIRD, and the fixed point is `1 + √2`. Segment splitting is NO protection — one accumulator crosses every `ResetPoint`. Smooth-min stays exactly 1. A **Displace/DomainWarp** sine field: factor `1 + amplitude·max|frequency_i|` — the INFINITY norm, not `‖f‖₂` (Displace's squared gradient norm is multilinear in the three squared sines ⇒ maximizes at a cube vertex; DomainWarp's `J - I` is a generalized permutation matrix whose spectral norm is its largest entry). `== 1.0f` EXACTLY for an isometric, chamfer/relief/warp-free program (byte-identical); the per-candidate `distanceScale` (Scale / the D2 log-spherical `r/density` correction) is a DISTINCT channel — never merged. Post: `sdf-lipschitz` (CPU bake assert; `warp-free stepScale == 1.0f` EXACTLY is the byte-identity contract) + `world-warp-solidity` / `world-log-sphere-solidity` (single-backend GPU solidity — parity CAN'T catch it, both backends overstep identically) + `world-chamfer` (chamfer cross-backend parity) |
| `SdfOp.LogSphere` (id 21) / `SdfProgramBuilder.LogSphere(shellRatio, twist)` — Data0.x = w (`ln(shellRatio)`, HOST-BAKED), Data0.y = twist (radians/shell), Data0.z = 1/w (HOST-BAKED); `AnalyzeLipschitz` folds `exp(w/2)` into `stepScale` | `SDF_OP_LOG_SPHERE` (21u) in `mapCore` — nearest-shell radial log-fold (`round`, like Repeat), an unconditional Z-spin (isometry, the Droste spiral), then `distanceScale *= shellScale` (the `r/density` correction, SAME channel as `SDF_OP_SCALE`, composes multiplicatively); `SDF_LOGSPHERE_MIN_RADIUS` floors the origin | D2 log-spherical DOMAIN warp — tiles space into infinite self-similar Droste shells. Radial-only fold ⇒ NO polar pinching; the r/density correction rides `distanceScale` (never `stepScale`); the `exp(w/2)` factor keeps the OVER-RELAXED march (omega 1.2) hole-free across shell boundaries. `AnalyzeSegment` gives it `SDF_BOUND_NONE` (unbounded periodic domain, via the `default` case — do NOT add a case). Op-unused programs stay byte-identical. Post: `world-log-sphere` (parity, `WorldLsbExact`) + `world-log-sphere-solidity` |
| PARKED instances (Arc 4): `SdfInstanceRange`/`BeginInstanceDynamic` carry an `Active` flag; an inactive slot packs the `SdfProgram.ParkedBoundRadius` (negative) bound sentinel — the reserved-pool "always fits by construction" contract is untouched, parked slots just become CHEAP | `collectInstanceMaskWord` (sdf-world.hlsli, the sphere-vs-cone tile test) and the full-eval enumeration (sdf-vm.hlsli, segment-range skip) each skip a negative-radius bound with ONE branch | parked-slot skip — beam/views cost tracks LIVE content, not reserved capacity. Demo-side, the pools (players/creator/companions) set `Active` per rebuild; a hidden-below-the-floor placement WITHOUT the flag is the pre-Arc-4 bug (264 always-tested instances = the 0.9→14.7ms regression) |
| The **2D-primitive family** (Vesica id-7 precedent, generalized): `SdfShapeType.RoundedRectangle`=8, `.RegularPolygon`=9, `.Star`=10, `.Trapezoid`=12, `.Ellipse`=13 (enum contiguous 0-14; `RoundCone`=11, `ScreenSlab`=14 unchanged) + `SdfLift { Revolve = 0, Extrude = 1 }` (`SdfLift.cs`) | matching `SDF_SHAPE_ROUNDED_RECT`/`_REGULAR_POLYGON`/`_STAR`/`_TRAPEZOID`/`_ELLIPSE` ids + `SDF_LIFT_REVOLVE`/`SDF_LIFT_EXTRUDE` (packed into Data1.y, decoded `> 0.5`) | SHARED lane layout for the whole family: Data0.xyz = the 2D shape params, Data0.w = the lift amount (revolve offset o OR extrude half-height h), Data1.x = smooth radius, Data1.y = lift mode, Data1.zw = per-shape host-baked constants (e.g. Star's baked `cos`/`sin(π/m)` ecs) |
| Builder methods `RoundedRectangle`/`RegularPolygon`/`Star`/`Trapezoid`/`Ellipse` (`SdfProgramBuilder`) + `SdfProgram.TryGetLocalBound` cases / `LiftedBoundRadius` helper | exact 2D cores `sdfRoundBox2D`/`sdfTrapezoid2D`/`sdfStar2D` (shared by RegularPolygon's m=2 case and Star)/`sdfEllipse2D`, lift ops `sdfExtrude2D`/`sdfRevolve2D`, lifted wrappers `sdfRoundedRect`/`sdfPolyStar`/`sdfTrapezoidSolid`/`sdfEllipseSolid` + their `evaluateShape` cases | evaluation + bounds for the family — each shape earns a REAL cull bound (unlike the approximate Ellipsoid #6); exact + factor-1 Lipschitz throughout (no `AnalyzeLipschitz` step clamp needed): extrusion is always exact, revolution is exact off-axis and a harmless conservative bound near the axis. Post: `world-2d-family` (both lift modes, cross-backend, `WorldHighContrast`) |
| `SdfOp.CellJitter` (id 22) / `SdfProgramBuilder.CellJitter(spacing, jitter, seed, tumble, materialVariants, flavor)` — Data0.xyz = spacing (HOST-CLAMPED ≥0.001/axis), Data0.w = jitter (peak-to-peak), Data1.xyz = 1/spacing (HOST-BAKED), Data1.w = clamped tumble [0,1], Material = materialVariants, Shape = seed, **Blend lane (header.z) = `SdfNoiseFlavor` {White=0 byte-identical default, Blue=1 R3 fixed-point low-discrepancy, Gaussian=2 central-limit}** — flavor reshapes ONLY the POSITION offset r0 (tumble/material-variant unaffected); `AnalyzeLipschitz`'s dedicated case (`chainTranslateReach += (sqrt(3)/2) * \|Data0.w\|`, treated exactly like a Translate of that magnitude — the per-axis half-amplitude combines as a VECTOR, since `chainTranslateReach` is a Euclidean-length sum; summing the per-axis `0.5` as a scalar under-counts a jitter-under-a-warp chain and lets the over-relaxed march overstep. Tumble/fold are isometries so nothing else accumulates) | `SDF_OP_CELL_JITTER` (22u) in `mapCore` — repeats like `SDF_OP_REPEAT`, then per-cell hashed position jitter (branched on `SDF_NOISE_*` = header.z), an optional hashed tumble (isometric rotation gated on `data1.w > 0`), and an optional hashed material-variant recolor, all keyed off `sdfPcg3d` (canonical PCG3D on the two's-complement cell index xored with the header seed) | stochastic domain-repeat fold — scatters a prototype into a jittered field from one instruction. Exposed to `puck.sdf.v1` as the geometric-only `cellJitter` op (no materialVariants lane, so the positional-recolor repair the document door refuses to inherit is unreachable); the document decoder's `Replay` appends a trailing `ResetPoint` so a dangling fold can never leak into the next emitter's chain. The hash is INTEGER-ONLY, so cell decisions are bit-identical across both DXC targets; displacement and tumble are BOTH isometries (distanceScale untouched — only the jitter half-amplitude joins `AnalyzeLipschitz`, as a reach term, not a warp rate). ALL THREE flavors keep r0 in [0,1)^3, so the offset stays within ±jitter/2 per axis — the SAME bound White has — so NO Lipschitz change (the reach-independent `L_cj` clamp stays conservative for every flavor); Blue's lattice is INTEGER-ONLY (`asuint` + uint mul-add) so it too is bit-identical cross-backend. `AnalyzeSegment` gives it the `default` case (space-folding op, no world-space sphere is sound past it, segment not skippable — do NOT add a dedicated case). In-cell rule: jitter/2 + prototype reach ≤ min(spacing)/2, REFUSED at `Build()` by name (`CellJitterLipschitz` sees both halves; the old silent margin clamp collapsed `stepScale` toward ~1e-5 and rendered the WHOLE composed field as an immediate-accept solid — the dark-dome failure). `WorldSdfDocumentEmitter.Load` wraps the dry-build so a document violating it is a `world.sdf.load` rejection (`BuilderRejectedProgram`), never a crash. ⚠Containment ≠ nearest-copy (verified 2026-07-08, slice capture): even with the in-cell rule satisfied, the single-cell `round` fold can pick the WRONG copy near a cell wall (a copy jittered toward the boundary is nearer to the adjacent cell's query than that cell's own copy), so the field OVERestimates at boundaries — visible seams, grazing-angle hole risk; keep jitter conservative. The same wrong-neighbor class applies to plain `Repeat`: exact ONLY for an on-center prototype within half-spacing per axis; an off-center/oversized prototype creases the field at cell walls with a march-holing overestimate (`SdfProgramBuilder.Repeat`'s doc carries the contract; iq's 3^k neighbor check judged NOT worth the interpreter cost at current usage). Post: `world-cell-jitter` (parity) + `world-cell-jitter-solidity` (single-backend GPU solidity) |
| `SdfOp.RepeatPolar` (id 23) / `SdfProgramBuilder.RepeatPolar(count, axis = SdfPolarAxis.Y, mirror = false, materialStride = 0)` — Shape = `SdfPolarAxis` {X, Y (default, XZ ground plane), Z}, Blend (header.z) = mirror flag, Material = per-sector stride, Data0 = (angle = 2π/count, 1/angle, count, 1/count) ALL HOST-BAKED, Data1 reserved | `SDF_OP_REPEAT_POLAR` (23u) in `mapCore` — folds the plane perpendicular to the axis into `count` equal angular sectors (nearest-sector `round` on the angle, like `SDF_OP_REPEAT`'s cell fold), an optional per-sector mirror (reflection across the sector bisector), then an optional per-sector material recolor | angular domain-repeat fold — the rotational sibling of `Repeat`/`WallpaperFold`: one authored prototype repeats around the axis (gears, wheels, rotunda columns, clock ticks, petals). The fold is a rotation (+ optional mirror reflection), BOTH isometries, so it is EXACTLY 1-Lipschitz — factor 1, NO `AnalyzeLipschitz` step clamp, same as `Repeat`/`WallpaperFold` (unlike `CellJitter`'s reach term or `LogSphere`'s `exp(w/2)` factor). Post: `world-repeat-polar` (cross-backend parity, Vulkan SPIR-V vs Direct3D 12 DXIL) |
| `SdfOp.Displace` (id 24) / `SdfProgramBuilder.Displace(frequency, amplitude)` — a FIELD op, ordered after the shapes it displaces; Data0.xyz = frequency, Data0.w = amplitude | `SDF_OP_DISPLACE` (24u) in `mapCore` — `result.distance += amplitude·sin(fx·x)·sin(fy·y)·sin(fz·z)` at the current folded point, evaluated in the same FIELD-op slot as `SDF_OP_ONION`/`SDF_OP_DILATE` | sine-product surface relief — the SDF-native height/parallax map, except the relief is REAL geometry (self-shadows/occludes). Separable basis, deterministic float trig (±1 LSB like the twist/bend warps) — parity-safe with no hashed noise table; the integer-hash fBm sibling is `NoiseDisplace` (id 29). NOT 1-Lipschitz: gradient reaches `amplitude·‖frequency‖`, so `AnalyzeLipschitz` folds `1 + amplitude·‖frequency‖` into `chainDisplaceWarpProduct` (a reach-independent metric-stretch factor, the same channel `DomainWarp` multiplies into — like the log-sphere product). Post: `world-displace` (parity) + `world-displace-solidity` (single-backend, the clamp holds the over-relaxed march) + the `sdf-lipschitz` stepScale assert |
| `SdfOp.NoiseDisplace` (id 29) / `SdfProgramBuilder.NoiseDisplace(frequency, amplitude, octaves, gain, lacunarity, seed)` — a FIELD op, ordered after the shapes it displaces; Data0 = (frequency, amplitude, gain, lacunarity), Data1.x = HOST-BAKED `1/Σ gainᵏ` normalization (the octave sum stays in [-1, 1] before amplitude), Shape = seed, Blend = octave count (≤ `MaxNoiseOctaves` = 8) | `SDF_OP_NOISE_DISPLACE` (29u) in `mapCore` — fBm over `sdfValueNoise3` (3D value noise: one integer-only `sdfPcg3d` per lattice corner keyed on the two's-complement cell xored with the per-octave seed streams, quintic-smoothed trilinear blend), and the analytic-gradient dual in `mapGradCore` via `sdfValueNoise3Grad` (KEEP the pair IN SYNC) | bound-preserving hash-lattice noise relief — the fBm/gradient-noise deferral is CLOSED (this row is the integer-hash basis `Displace` deferred to). Cell decisions are bit-identical cross-backend (integer hash); the blend is float mul/add (±1 LSB — silhouette winner flips only, inside the relaxed envelope). NOT 1-Lipschitz: `AnalyzeLipschitz` folds `1 + \|amp\|·freq·(15/4)·√3·Σ(gain·lacunarity)ᵏ/Σgainᵏ` into `chainDisplaceWarpProduct` (`NoiseDisplaceLipschitz`); outward surface reach is `\|amplitude\|` (`MaxScopedFieldReach`), and the op joins Onion/Dilate/Displace in every field-op classification (unmaskable when unscoped, parked-refusal, shadow-transparency). Exposed to `puck.sdf.v1` as the scoped-only `noiseDisplace` op (refused outside a push/pop pair — an unscoped document field op would displace the whole composed world field). Stripped under `SDF_CORE_OPS` (views-core bytecode is byte-identical). Laws: `tests/Puck.SignedDistance.Tests/SdfNoiseDisplaceLawTests.cs` (bitwise step-clamp mirror, identity, refusals); no Post stage exists (quarantine) — cross-backend agreement was measured by hand on the real windowed world |
| `SdfOp.DomainWarp` (id 25) / `SdfProgramBuilder.DomainWarp(frequency, amplitude)` — a POINT op, ordered before the shapes it warps; Data0.xyz = frequency, Data0.w = amplitude | `SDF_OP_DOMAIN_WARP` (25u) in `mapCore` — `localPosition += amplitude·(sin(fx·y), sin(fy·z), sin(fz·x))`, each axis driven by the NEXT axis's coordinate (non-separable), before the wrapped chain evaluates | cross-coupled organic domain warp — deterministic float trig, same parity posture as `Displace`. NOT an isometry: the Jacobian is `I` plus a perturbation of spectral norm ≤ `amplitude·‖frequency‖`, so the SAME `1 + amplitude·‖frequency‖` clamp joins `chainDisplaceWarpProduct`, and the point's max travel (`amplitude·√3`) additionally folds into a downstream twist/bend's reach term. Post: `world-domain-warp` (parity) + `world-domain-warp-solidity` (single-backend, the clamp holds the march) + the `sdf-lipschitz` stepScale assert |
| `SdfOp.SymmetryPlane` (id 26) / `SdfProgramBuilder.SymmetryPlane(normal, offset = 0f)` — Data0.xyz = the UNIT plane normal (host-normalized), Data0.w = the plane offset | `SDF_OP_SYMMETRY_PLANE` (26u) in `mapCore` — `p -= 2·min(dot(p, n) + offset, 0)·n`; for `n = x̂, offset = 0` this is `abs(p.x)` to the bit, an exact superset of the RETIRED `SDF_OP_SYMMETRY_X` | arbitrary-plane reflection fold — the general-normal fold that REPLACED the `SymmetryX`/`SymmetryY`/`SymmetryZ` opcodes (ids 13–15 collapsed into id 26; the builder keeps `SymmetryX/Y/Z()` as sugar that emit it): everything on the plane's negative side mirrors onto its positive side, so one authored half repeats mirror-imaged across ANY plane (a kaleidoscope leaf, a bilateral body, the reflect atom of a KIFS fold). A reflection is an ISOMETRY, so it is EXACTLY 1-Lipschitz — factor 1, NO `AnalyzeLipschitz` step clamp, same as `WallpaperFold`/`RepeatPolar`. Post: `world-symmetry-plane` (cross-backend parity, Vulkan SPIR-V vs Direct3D 12 DXIL) |
| The **Glyph op** — `SdfShapeType.Glyph` (SHAPE id 15, the next free shape after `ScreenSlab`=14) / `SdfProgramBuilder.Glyph(uvBottomLeft, uvTopRight, halfWidth, halfHeight, extrudeHalfDepth, distanceScale, material, blend, smooth)` + `SdfProgramBuilder.Text(atlas, text, origin, right, up, worldEmHeight, …)` (lays out via `Puck.Text.TextLayout`, emits one `ResetPoint`+`Translate`+`Rotate`+`Glyph` SEGMENT per char — the SdfVm→Puck.Text edge). LANE LAYOUT: Data0 = (`packedUvMin`, `packedUvMax` [each host-baked unorm2x16 of an atlas UV — packing frees a lane so Data1.x keeps the ISA-wide smooth], `distanceScale` [= atlas `DistanceRange`(texels) × worldPerTexel, HOST-BAKED], `extrudeHalfDepth`); Data1 = (`smooth`, `halfWidth`, `halfHeight`, 0). Uploaded ONCE via `SdfWorldEngine.SetGlyphAtlas(rgba, w, h)` (an `IGpuSurfaceUpload`), threaded through `ISdfFrameSource.GlyphAtlas` (`SdfGlyphAtlas` record, default null) polled once in `SdfEngineNode.EnsureEngine`. | `SDF_SHAPE_GLYPH` (15u) in `evaluateShape`, guarded on `SDF_GLYPH_ATLAS` (defined ONLY by `sdf-world-views.comp` — every other kernel gets the conservative extruded-quad fallback `sdfGlyphQuad`, so the beam cull/rt-debug see a solid cell box, never a hole). `sdfGlyph`: exact 2D quad distance `dQuad` FIRST, atlas tapped ONLY inside the band (`dQuad < 0.5·distanceScale`), `dPlane = max((0.5 − encoded)·distanceScale, dQuad)` then extruded — the band-cull is BOTH the perf trick and the conservative far field. Field from ALPHA (the true single-channel distance) via manual bilinear (`sdfGlyphSampleField`, `SampleLevel` explicit-LOD, s32/t39 combined-image-sampler at Vulkan binding 44 — DERIVED as `ScreenSourceBindingBase + MaxScreenSurfaces`, appended after the 32 screen sources in `SdfWorldEngine.viewsBindings` so D3D12 registers land t39/s32). | text as REAL world geometry: marchable, blendable, ENGRAVABLE (Subtraction) / EMBOSSABLE (Union proud of a slab — NEVER coplanar or the coincident zero-sets speckle) / floating. Reconstruction: GEOMETRY MARCHES THE TRUE SINGLE CHANNEL (alpha) — median-of-3 is C0-only at clash lines and must never be marched (the flat-coverage `GlyphDecal` tier LANDED 2026-07-09 — a SEPARATE material-level tier that samples the SAME atlas's ALPHA with a coverage threshold at SHADE TIME on a `ScreenSlab` carrier, NOT marched geometry: a per-screen decal table + shared cell buffer `sdfDecalCells` at Vulkan binding 45 / D3D12 t40 (after the glyph atlas t39; DERIVED as `GlyphAtlasBindingIndex + 1`), `SdfWorldEngine.SetScreenDecal`/`ClearScreenDecal` ↔ `sampleScreenSurface`'s decal-first branch, the `ISdfFrameSource.ScreenDecals` per-frame seam; Post `world-glyph-decal`; world-glyph geometry stays untouched, byte-identical when no decal is declared — an MSDF atlas would let the decal median-of-3, the alpha is what it samples now). Generation NOW: `Puck.Text.SdfCoverageAtlas.Generate` — an EXACT separable Euclidean distance transform (Felzenszwalb–Huttenlocher, deterministic) over a GDI+ coverage raster; the chamfer(1,√2) alternative overestimates ≤8.24% off-axis and would need a 1/1.0824 step-scale penalty, so exact-EDT + uniform worldPerTexel keeps Glyph FACTOR-1 (1-Lipschitz in texel space, bilinear preserves it — NO `AnalyzeLipschitz` case, like the 2D-lift family; a stretched cell is the caller's risk). Recommended marchable source is a pre-baked `msdf-atlas-gen` MTSDF atlas (true-distance in alpha by construction) — the runtime EDT is the no-toolchain fallback. Post: `world-glyph` (cross-backend parity, `WorldHighContrast` — sampled-texture/material-seam family; the fixture atlas is a deterministic in-process 5×7 font, no font-availability dependency; a no-atlas control proves the atlas reaches the shader). Adapted from SignedDistanceTerminal's `sdfMsdfGlyph`. |
| The **SampledRegion op** — `SdfShapeType.SampledRegion` (SHAPE id 16, the next free shape after `Glyph`=15) / `SdfProgramBuilder.SampledRegion(boxMin, cellSize, dimX, dimY, dimZ, brickWordOffset, boundaryFloor, material, blend = Subtraction)` (`MaxSampledRegionDim = 1023`). LANE LAYOUT: Data0 = (`boxMinX`, `boxMinY`, `boxMinZ`, `cellSize`) — box extent derives as `dims·cellSize`; Data1 = (`smooth` [ISA-wide, = 0 for the hard subtraction a brick composes with], `packedDims` [uint bits: 3×10-bit dims ≤1023/axis, host-packed `dimX \| dimY<<10 \| dimZ<<20`], `brickWordOffset` [uint bits: the brick's base word in the pool], `boundaryFloor` [= margin/λ, host-baked outside-box lower-bound offset]). The two uint bit-fields ride the float lanes as reinterpreted bits (like Glyph's `PackUv`) and round-trip exactly through `WriteVector4`. `TryGetLocalBound` returns the box CIRCUMSPHERE (center = boxMin + extent/2, radius = |extent|/2) — a REAL cull bound, so `AnalyzeSegment`/`ShapeReachRadius`/`PackInstances` treat it as any Subtraction-blend instance and `IsShadowTransparentInstance` auto-flags it (Path B). `AnalyzeLipschitz` = factor 1 EXACTLY (λ is folded into the STORED values at bake, not `stepScale`), so brick-free scenes stay byte-identical AND a brick adds no global step tax. | `SDF_SHAPE_SAMPLED_REGION` (16u) in `evaluateShape` (NOT stripped under `SDF_CORE_OPS` — the core-ops views variant binds the pool), guarded on `SDF_SAMPLED_REGIONS` (the world-views + core-ops + beam kernels bind the pool as of **W0b**; the instance-cull/rt-debug/diagnostic kernels take the fallback). `sdfSampledRegion`: `local = (p−boxMin)/cellSize`; OUTSIDE the box returns `dist(p,box) + boundaryFloor` (a valid scaled lower bound — positive, so Subtraction stays saturated and the accumulator is exact); INSIDE, manual TRILINEAR over 8 `sdfBrickPool` loads (sample CENTRES at integer voxel indices, `sampleCoord = local − 0.5`, clamp-to-edge border half-voxel) with a `precise` lerp chain (fp-contraction pinned OFF → bit-stable SPIR-V/DXIL). WITHOUT `SDF_SAMPLED_REGIONS` (the instance-cull/rt-debug/diagnostic kernels): returns `SDF_FAR_DISTANCE` (the conservative UNION-HULL fallback — a Subtraction compose never bites, region renders uncarved, never holed — the Glyph quad-fallback precedent). WITH `SDF_SAMPLED_REGIONS` but a POOL-LESS engine (capacity-0 filler): `sdfSampledRegion` calls `sdfBrickPool.GetDimensions` and, seeing the single-float filler (`numVoxels <= 1`), takes the SAME `SDF_FAR_DISTANCE` fallback — so a filming view renders a SampledRegion world UNCARVED. ⚠GROUND TRUTH: the stored brick distances are `/√3` scaled, so a ZEROED read (an allocated-but-UNBAKED 64 MB pool, or a filler sampled without the gate) = stored distance 0 = the box interior sitting entirely on the carve surface ⟹ the Subtraction carves a box-shaped HOLE across the whole region. This was a LIVE defect for filmed carves: every offscreen filming view once allocated its own default 64 MB pool it never baked into, so filming a carved world rendered the carve box as a hole (and wasted ~4 GB at the 64-view cap). The GetDimensions gate + capacity-0 view engines fix both. Normals: the `evaluateShapeGradient` `default` arm's 4-tap FD (4 extra pool samples, hit-only). Pool: `[[vk::binding(46,0)]] StructuredBuffer<float> sdfBrickPool` (one f32/voxel), per-consumer D3D12 register via `SDF_BRICK_POOL_REGISTER` (views set t41 after `sdfDecalCells` t40; beam t4 after its mask t3 — the `SDF_INSTANCE_MASKS_REGISTER` pattern). | a SAMPLED distance-field brick: the settled-carve UNION field baked O(1) so the primary/shadow/AO marches stop paying O(carve-count), composed as ONE ordinary Subtraction instance (crack-free by construction — the subject stays fully analytic). W0a shipped the ISA + shape eval; W0b landed the engine tier — the persistent device-local pool (`SdfWorldEngineOptions.BrickPoolVoxelCapacity`, default 64 MB = `SdfWorldEngine.DefaultBrickPoolVoxelCapacity` = `SdfBrickPoolLayout.TotalVoxels`; frozen at construction, 0 = no pool: baking and rendering are SPLIT — a pool-less engine still ACCEPTS a SampledRegion program (rendered uncarved via the GetDimensions fallback, see the sdfSampledRegion row), only `RequestBrickBake` stays a loud rejection), the static `SdfBrickPoolLayout` (8 slots × 128³), the closed-form sphere-union baker `sdf-brick-bake.comp` (distances stored `/√3`, sliced ≤256K voxels/frame off the render's frame-timing bracket), and the `RequestBrickBake`/`GetBrickState` API with the two-revision-bump handoff (`BrickBakeState` Empty→Baking→Ready). `SdfViewsKernelVariants` classifies SampledRegion as CORE so a baked carve scene keeps the faster core-ops variant. ⚠ editing `sdf-world-views.comp.hlsl` does NOT reliably retrigger the `sdf-world-views-core.comp` recompile (it includes, not `#include`s a `.hlsli`) — the stale-bytecode gotcha bit W0b once; delete + rebuild the core `.spv`/`.dxil` after touching the views source. The planner (`SdfCarveBakePlanner`) is W1a. The carve-bake plan document was deleted 2026-08-02; there is no plan of record for the remaining carve-bake work, and the `world-sampled-region` stage that checked it went with `Puck.Post`'s quarantine — this shape is unverified by machine. |
| The **scoped accumulator** — `SdfOp.PushField` (id 27) / `SdfOp.PopField` (id 28); `SdfProgramBuilder.PushField(compose = Union, smooth = 0f)` / `PopField()` (depth cap `SdfProgramBuilder.MaxFieldScopeDepth = 1`; the compose blend + smooth ride the POP instruction's Blend lane + Data1.x — the SAME lanes a `ShapeBlend` uses; PUSH carries no data) | `SDF_OP_PUSH_FIELD` (27u) / `SDF_OP_POP_FIELD` (28u) in `mapCore` (`SDF_MAX_FIELD_SCOPE_DEPTH = 1u`) — PUSH saves the running accumulator into a one-deep `(savedFieldDistance, savedFieldMaterial)` slot and reseeds `result` to `SDF_FAR_DISTANCE`; POP restores the parent as the blend LHS and feeds the scope's `result.distance` as a CANDIDATE into the **shared blend tail** (the material-winner switch + `blendShape`) SHAPE now also uses — so a POP costs no second copy of the ten-way blend switch (`composePending` gates the tail) | one-deep SCOPED FIELD ACCUMULATOR — the fix for "a field op / intersection shells the WHOLE scene": every accumulator-reading op (the intersection family, `Onion`/`Dilate`/`Displace`) between a balanced `PushField`/`PopField` acts on the scope's own shapes ONLY, then composes back with the POP's blend. A scope touches the FIELD, never the POINT (`localPosition`/`distanceScale`/`parityMaterialDelta`), so `ResetPoint` is unchanged and per-shape cull bounds after the Push stay sound. THE FUSION TRAP: a POP's candidate is ALREADY in world units — it is NOT re-multiplied by `distanceScale` and does NOT take `parityMaterialDelta` (unlike SHAPE). Material tie-break is strict `<` (parent keeps its material on a tie). `AnalyzeSegment` gives a Push/Pop segment `segmentEligible = false` (never whole-skip a scope boundary) but leaves `chainBoundable` TRUE (correction #1 — bounds after the Push survive); `HasUnmaskableCompose` tracks scope depth so a SCOPED field op / intersection is NO LONGER unmaskable (the culling payoff — only a POP with an intersection-family compose at depth 0 is), and `MaxSmoothBlendRadius` folds a POP's soft compose halo. THE MARGIN RULE (the payoff's fine print): a scoped field op is maskable but GROWS the surface OUTWARD past the authored geometry bound, so `PackInstances` must inflate the instance's finite bound by that reach or the beam masks the tiles the grown shell reaches and the surface HOLES at the tile seams — `MaxScopedFieldReach` folds it in the same way `MaxSmoothBlendRadius` folds the POP compose halo (per-op: `Onion(t)` outer surface moves out by `t`, `Dilate(r)` by `r`, `Displace(a)` by `a`; field ops SUM within a scope, max across scopes; an UNscoped field op stays unmaskable, so its 1e30 sentinel covers it and no margin is computed). Verified 2026-07-08 by a scoped-`Dilate(1.5)` sphere with a bound covering only the un-dilated radius: pre-fix the beam clipped the shell into a blocky tile-truncated blob, post-fix the full dilated sphere renders intact. `AnalyzeLipschitz` folds a chamfer compose through the SAME per-composition recurrence a chamfer `ShapeBlend` takes, so repeated pops accumulate (`MaxFieldScopeDepth = 1` forbids nesting, not sequencing). Op-unused (scope-free) programs stay BYTE-IDENTICAL (verified: overworld render sha256-identical). Post: `world-scope` (scoped intersection renders as the intersection of its own members; a scoped instance is maskable with `instanced == flat`; its near-endpoint cluster + the CPU pin also prove `blendSmoothUnion`'s FAR + NEAR endpoints — the scope-seed prerequisite; there is NO separate `sdf-blend-endpoints` stage) |
| `Puck.SignedDistance.Queries.SdfFieldEvaluator` (GRAVITY ARC Wave 1, `IWorldQuery`+`IFieldEvaluator`, the SECOND `IWorldQuery` provider after `BakedWorldQuery`) — a WARP-FREE CPU interpreter of the live `SdfProgram.Instructions` typed seam (not the packed `Words`), in `FixedQ4816`/`FixedVector3`. Ctor walks the stream once, asserting every op/shape is in the supported rigid subset (throws `ArgumentException` naming the first excluded one) and converting each instruction's Data0/Data1 floats to `FixedQ4816` ONCE into a cached `CompiledInstruction[]` — including a `Rotate`'s baked quaternion, transcribed via `rotatePointByInverseQuaternion`'s cross/mul/add form (no runtime sin/cos). It also refuses NON-UNIFORM `Scale`: the GPU's minimum-axis correction is a safe march lower bound, not Euclidean physical clearance. `SdfSolidGeometry.AppendScaledPrimitive` therefore bakes authored anisotropy into native Box, Sphere/Ellipsoid, axis-symmetric Capsule/Cylinder/Cone, and Plane spellings before a render/contact stream is shared; unsupported anisotropic primitive spellings fail loudly at field construction. `TryFieldGradient` is a 6-tap per-axis central difference over `TryDistance` (still not a `mapGradCore` dual port; the original 4-tap tetrahedron form of Decision B was replaced when its edge-aliasing — a spurious tangential normal component with a two-equal-components fingerprint at blend corners — was measured driving a deterministic tangential runaway in the wall-contact solve); the five `IWorldQuery` verbs sphere-trace `TryDistance` (`Exact` on convergence, `Bounded` on the three non-convergences: a RADIUS cast whose scaled field no longer clears its radius, the iteration budget running out, and a marched point the program's frame cannot express). TWO seam contracts that are NOT the shader's: (1) `TryDistance` evaluates the WHOLE `FixedPosition`, rebased against the world origin via `TryDelta` — the identity inside cell (0,0,0), correct across cells, and the reason a body past ±524,288 units (where `FixedPosition.FromLocal` carries a cell on its own) no longer reads the cell-0 field; (2) a march that exhausts its iteration budget resolves per VERB, by what that verb's TRUE half asserts — `Raycast`/`SphereCast` report a hit at the last marched point with `WorldQueryConfidence.Bounded` and `LineOfSight` reports BLOCKED, because "clear" is the assertion authoritative consumers (NPC visibility, `FixedFieldContactSolver.ResolveCore`) cannot survive being wrong about, while `TryGroundHeight` returns FALSE: it asserts a SURFACE, hands back a bare coordinate with no confidence channel, and a caller grounding a body on a fabricated Y is moved somewhere the world does not have. A shape-free program still MISSES rather than reading solid. THE MARCH APPLIES `SdfProgram.StepScale` (converted to `FixedQ4816` ONCE at construction, like every other program float): the interpreted OP subset is 1-Lipschitz but the BLEND TAIL is not — a chamfer, or an eccentric `Ellipsoid`, makes the field overestimate, and a raw advance tunnels a thin plate. Scale the FIELD then subtract the radius (`f·s − r`), never the clearance (`(f − r)·s` shrinks the radius too and is anti-conservative for a `SphereCast`). The raw clearance still owns exact convergence, but the SCALED clearance owns whether separation is proven: `Overlap` compares the directed-down product `floor(f·s)` with the radius, and a cast advances by `max(floor(f·s), one Q48.16 tick) − r` only while that value remains positive. It never floors an unproved advance upward. The tick floor sits on the FIELD, before the radius comes off, and exists because the accept arm tests the RAW field against `HitEpsilon` (raw 66) while the stop arm tests the SCALED field against zero: below `s = 978/65536` (~0.0149) the stop threshold sits ABOVE the accept threshold, so an unfloored descent stalls one raw tick short of a surface it has already proven is inside `HitEpsilon + 1 tick` and `TryGroundHeight` answers "no ground" over every column. Floored, a POINT cast always advances and can only overstep the true surface by less than one tick (1/66 of the accept band); for any radius of one tick or more `max(floor(f·s), tick) − r ≤ 0` exactly when `floor(f·s) − r ≤ 0`, so SPHERE casts are bit-identical and still never advance into the contact envelope. `StepScale` likewise converts by a directed floor; an extreme positive scale below one Q48.16 tick becomes zero, authorizing no scaled advance at all — a radius cast is `Bounded` at its origin, a point cast is `Bounded` after the one-tick reach, and a shape-bearing overlap is occupied — rather than inventing a larger unsafe multiplier. The iteration budget derives from `BaseMarchIterations · HitEpsilon / max(floor(HitEpsilon·s), one Q48.16 tick)`, keeping point-cast reach invariant at `512 · HitEpsilon` = raw 33,792 while bounding an extreme program at 33,792 iterations. `Overlap` treats a failed world-origin rebase as occupied for a shape-bearing program and false for a shape-free one | `mapCore`'s RIGID op cases (`SDF_OP_RESET`/`_TRANSLATE`/`_ROTATE`/`_SCALE`/`_REPEAT`/`_REPEAT_LIMITED`/`_SYMMETRY_PLANE`/`_ELONGATE`/`_ONION`/`_DILATE`/`_PUSH_FIELD`/`_POP_FIELD`/`_SHAPE`) + `evaluateShape`'s Sphere/Box/ScreenSlab/Torus/Plane/RoundCone/Capsule/Cylinder/Ellipsoid/Vesica/RoundedRectangle/Trapezoid bodies + `blendShape`/`blendSmoothUnion` — the shared blend tail's semantics, INCLUDING op-order effects (a strict material-winner compare before the distance blend), mirrored exactly | a SECOND, INDEPENDENT interpreter of the SAME instruction stream mapCore walks (a deliberate dual implementation, like `SdfProgram`'s own host-side `AnalyzeBounds`/`AnalyzeLipschitz` passes — NOT shader codegen). WARP-FREE means it rejects `TransformDynamic` (no per-frame dynamic-transform table in this evaluator's signature — a future wave could thread one through without touching any other op's status), `BendX`/`BendY`/`BendZ`/`TwistY`/`LogSphere`/`CellJitter`/`RepeatPolar`/`Displace`/`DomainWarp` (runtime trig this wave doesn't implement in fixed point — but `SymmetryPlane`/`RepeatLimited`/`RepeatPolar` each have a rigid-copy spelling in `SdfDomainExpansion`, which is how the contact paths carry a fold this evaluator cannot walk), and `WallpaperFold` (isometric and so tractable in principle, but its 17-group parity-keyed cell logic was judged real added surface, not a five-minute mirror — Wave 1's reconciliation finding: the plan's initial excluded-op list named 9 ops from `AnalyzeSegment`'s bound-skip default-case partition, which is a SUPERSET reflecting a DIFFERENT concern — "no sphere bound is sound past this op" — not "uninterpretable"; `Repeat`/`RepeatLimited`/`SymmetryPlane`/`Elongate`/`Onion`/`Dilate` and ISOTROPIC `Scale` are directly interpreted here as 1-Lipschitz operations). Three shapes are excluded for the same reason at the shape level (not itemized in the arc plan, a Wave 1 finding): `RegularPolygon`/`Star` (`sdfStar2D`'s runtime `atan2`) and `Ellipse` (`sdfEllipse2D`'s analytic cubic solve, `acos`/`pow`); `Glyph` needs texture sampling, while `SampledRegion` needs the engine-owned brick pool. `RoundedRectangle` is supported by mirroring the shader's exact `sdfRoundBox2D` plus lift wrapper. Gravity = `-gradient.Normalize()` is the CONSUMER's one-line derivation (`IFieldEvaluator`'s whole reason to exist as its own seam) — the field itself never encodes "planet" or "down". Verified (a Wave 1 scratch harness, not committed): hand-computed sphere/translated-box/rotated-capsule/SmoothUnion points match to <5e-7 (float-rounding-of-the-input floor, not fixed-point error); a 200-point random sweep vs. an independent double-precision reference measured max\|err\| ≈ 2.3e-5 for sphere and box; `TryFieldGradient` on a sphere at 10 points (axes, diagonals, near-degenerate) measured max\|err\| ≈ 2.2e-3 (measured against the retired 4-tap tetrahedron probe — the 6-tap central difference that replaced it has O(eps^2) truncation instead of O(eps) curvature aliasing; RE-MEASURE before freezing any gradient threshold) against the analytic radial unit vector, well inside GradientEpsilon's documented 0.01-world-unit probe; 1000 seeded points evaluated twice against a multi-op program (Translate+Rotate+Box+ResetPoint+SmoothUnion-Sphere) were BIT-IDENTICAL (0 mismatches on the raw `FixedQ4816.Value`) — the live `tests/Puck.SignedDistance.Tests` gate now pins direct query regressions, while the broader determinism/drift measurements remain without a live Post gate |
| `DynamicTransform.CastsSoftShadow` (SdfFrame.cs; default `true` = casts) → `SdfWorldEngine.PackDynamicTransforms` packs it into the dynamic transform's POSITION row `.w` lane (0 = casts, 1 = shadow-suppressed) — the lane that was a hardcoded 0 pad, so a default-casts frame is BYTE-IDENTICAL | `sdfShadowParticipationActive` (a `static bool`, false default, declared under `SDF_DYNAMIC_TRANSFORMS` beside `sdfShadowMaskActive` in `sdf-vm.hlsli`) flipped `true`/`false` UNCONDITIONALLY around the ONE `softShadow` call in `sdf-world.hlsli` (matching `sdfShadowMaskActive`'s lifetime) → the per-instance skip in `sdfNextVisibleInstanceRange` (`sdfShadowParticipationActive && meta.x == SDF_BOUND_DYNAMIC && sdfDynamicTransforms[2u*meta.y].w > 0.5 ⟹ continue`, mirroring the parked-radius skip) + the gather-side twin `sdfInstanceShadowSuppressed` skip in `sdfShadowGather`'s two candidate loops (gated on the RAW condition — the gather runs BEFORE the flag flips and is inherently shadow-scoped) | per-frame per-instance soft-shadow PARTICIPATION — a suppressed dynamic instance drops out of the soft-shadow march ONLY (camera/AO/coverage marches keep the flag false and are untouched; static instances have no dynamic slot and always cast). Default = casts, byte-identical for every existing consumer; no program rebuild (it rides the per-frame dynamic-transform upload). Consumer: `Puck.World`'s `WorldFramePresenter` computes it per entry (local seats always cast; a stand-in casts iff within `WorldRenderSettings.ShadowCrowdRadius` of a joined seat — the 128-player crowd lever, `world.shadows [tier] [crowd-radius]`). The three soft-shadow fallback modes (gather cull / camera-tile / flat) all resolve through `sdfNextVisibleInstanceRange`, so the flag is set unconditionally to cover all three. ⚠ editing `sdf-world.hlsli`/`sdf-vm.hlsli` needs the `sdf-world-views-core.comp` `.spv`/`.dxil` deleted before build (the include-not-#include stale-bytecode gotcha). No dedicated Post stage (a demo/World-greenfield lever — the default-casts path keeps `world-shadow-cull`/`world-swarm` bit-identical) |


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
    compositor copies its surface).
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
engine-side classification a host's own anchor kinds map onto, e.g.
`CameraAnchorKind.Shape → Body`). **Float verdict (recalibration float
sweep, 2026-07-10): PRESENTATION, not simulation state.** An anchor is
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
`Puck.Demo.Overworld.ScreenSlotLedger` arbitration.

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
quantize-once-per-edge discipline, `Puck.Demo.World.WalkGridBaker`'s
query-namespace sibling: every rectangle edge snaps to raw Q48.16 exactly
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
`FieldEvaluatorCapabilities`, never stubbed). **`Puck.SignedDistance.Queries.Debug`:**
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
The RTS proof scenario
(`Puck.Demo.Rts.RtsScenario`) is `IWorldQuery`'s first consumer: its arena
bounds/dais/boulder are AUTHORED float constants fed once through
`WorldQueryBaker.Bake` into a deterministic artifact — never touched per
tick — while the actual per-tick unit sim (`OverworldWorld.RtsUnit`,
`AdvanceRtsUnits`) is 100% `FixedQ4816`.

## Render assembly (Puck.SdfVm)

`SdfWorldRenderSpec` + `SdfWorldRenderBuilder.Build` — hoisted to `Puck.SdfVm`
root namespace 2026-07-10 (previously lived in `Puck.Demo`) — own EVERY
backend-specific choice from one `HostsOnDirectX` field: kernel bytecode
extension (`.spv`/`.dxil`, resolved via `SdfWorldKernels.Load`'s one-arg
default now that the Builder no longer threads a caller-supplied directory),
child `directX` flags, and the `DecorateFrameSource` seam
(`Func<ISdfFrameSource, ISdfFrameSource>?`) — an optional in-place decorator
the Builder applies to `spec.FrameSource` before building the engine node,
identity when absent. The Builder itself never names a host type; the demo's
diegetic-UI coupling (binding bar + console mirrored into world geometry)
lives entirely in `Puck.Demo.Overworld.DiegeticUiInstaller.Install`, wired in
by the overworld's spec as `spec.DecorateFrameSource = fs =>
DiegeticUiInstaller.Install(services, fs)` — reached through the ceiling-era
forwarders `ForgeCommands.DecorateOverworldFrameSource`/
`ResolveRenderTimingToggles` so `OverworldRenderNode` still names only one
symbol. A caller never names a bytecode extension.
`GraphBuilder.UnsupportedReason` WAS the one owner of the world graph's
deferred rejections (cross-backend `produce`, `live-camera` pending its
child node) — pre-flighted in `Program` BEFORE the window host built, so
rejection was an attributed stderr line and exit 2, never a mid-host crash.
It RETIRED with `Puck.Demo`'s composition root, so the pre-flight side has
no live owner today; the doctrine waits for the next graph-building host.

**The capacity probe (the envelope pattern, live in `Puck.World.Client.WorldFramePresenter`).**
Composing `SdfCompositionFrameSource` runs ONE worst-case probe across its
emitters — every diegetic screen lit, all 128 avatars, the reserved placement
instances, and the worst-case animated pool — measures it (the probe is never
rendered), and feeds the result through `SdfWorldRenderSpec.ProgramWordCapacity` /
`InstanceCapacity`, so live rebuilds vary freely BELOW the frozen envelope.
Any NEW optional emission MUST also declare a `Probe` branch on its
`ISdfSceneEmitter`, or a live rebuild can outgrow the buffers and
`UploadProgram` throws loudly.

## Shader build mechanics

`dotnet build src/Puck.SdfVm -c Release` runs DXC IN PLACE in the source tree
(build FAILS without DXC; `/p:DxcCommand=` overrides) — commit the
regenerated `.spv`/`.dxil` with the source change. Editing `sdf-world.hlsli`
or `sdf-vm.hlsli` recompiles `sdf-instance-cull.comp`, `sdf-beam.comp`,
`sdf-world-views.comp`, `sdf-sky.comp`, AND `sdf-cull-args.comp`.
`ValidateShaderBytecodeSources` fails the build on bytecode without a
same-stem `.hlsl` (Puck.SdfVm only; the other shader-shipping projects lack
the guard — a known follow-up).

**The MASK-FIRST pass order (the uniform-grid instance-cull arc), now preceded
by the sky pre-pass.** SIX kernels per frame: `sdf-sky.comp` (fills every
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
`["mask", "beam", "cull-args", "views", "composite"]` (`SdfWorldEngine.PassLabels`);
the bench's beam column reports beam+mask so ladders stay comparable, and "views"
is now a pure Stage-1 march number (the cull-args reduction closes its own mark). Gated by
`world-grid-cull` (grid==flat bit-identical via the destructible-slab scene)
plus the existing instanced==flat stages.

## Gotchas (verified, expensive to re-learn)

- **The soft-shadow march is GRID-CULLED (default ON; `sdf.shadowcull on|off`,
  `SdfFrame.DisableShadowCull`).** `renderView`'s `softShadow` no longer marches
  the CAMERA-tile mask (the wrong occluder set for a ray that leaves the camera
  cone). Instead `sdfShadowGather` (sdf-world.hlsli) walks the SAME view-
  independent `SdfInstanceGrid` the beam cull walks, along the SUN ray, into a
  per-lit-pixel LOCAL mask (`sdfShadowMaskWords`, `SDF_SHADOW_MASK_WORDS = 32` =
  ≤1024 addressable instances) that `mapMasked` reads via the `sdfShadowMaskActive`
  static — so the culled shadow is BIT-IDENTICAL to the flat all-instances march
  (gated by `world-shadow-cull`) yet restricted to the shadow ray's neighbourhood,
  AND newly CORRECT for occluders outside the camera frustum (the corridor case).
  THREE settled pins: (1) the gather cone is the **penumbra cone**
  `ShadowPenumbraChord = 3/ShadowSharpness`, NOT a bare ray — the Aaltonen
  closest-approach refinement couples each sample to the PREVIOUS sample's nearest-
  surface clearance, so a bare ray (chord 0) or the direct 1/k penumbra drops
  penumbra-edge px (measured: 1/k→840, 2/k→125, 3/k→0); a wider cone is always a
  safe superset, only less selective. (2) The fallback is 3-way: gather BUILT (2) →
  the cull; grid present but >1024 instances (1) → the CAMERA-tile mask (the cheap
  pre-cull behaviour — NOT flat, which is ~20× on a dense 4096 scene); NO grid (0)
  → flat all-instances (cheap for few instances, and MATCHES a would-be gather so
  the `sdf.grid` toggle stays render-invariant — the `world-grid-cull` contract).
  (3) PERF is scene-dependent and MEASURED: the per-pixel gather WINS on spread
  scenes (the town reveal 254→116 ms views vs flat; 134→116 vs the old camera-tile)
  but LOSES on dense clustering (1024 carves stacked in one spot 46→101 ms — the
  amortized per-tile camera-tile mask beats the per-pixel gather when the cone can't
  narrow). A density-adaptive gate (skip to camera-tile when the grid is dense) is
  the open follow-up; the lever ships ON for the overworld's benefit.

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
- Per-pass GPU-ms: arm live via the gpu.timing switch (demo) / world.timing verb (world) or the run-doc host.timing field. Delayed captures: `PUCK_CAPTURE_FRAME=N`.

## Verifying

**Almost nothing gates the world path today.** The POST battery whose
world-path stages exercised every kernel is quarantined with `Puck.Post` and
never run, the `--run`/`--capture` entry points that drove the example
documents left with the `Puck.Demo` composition root, and nothing replaced
either. The one on-demand check is `puck parity` (tests/Puck.Parity/): it boots
the authored parity world (`parity.world.json` + its `parity.sdf.json`
companion) offscreen once per backend — stations: sky, materials, a
`state.lattices` height-field, `noiseDisplace`+`cellJitter` — and renders
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
