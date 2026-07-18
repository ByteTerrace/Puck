---
name: sdf-world
description: Working on the SDF VM and world renderer — src/Puck.SdfVm (SdfProgram/SdfProgramBuilder, SdfWorldEngine/SdfEngineNode, the Assets/Shaders/Sdf kernels, the shared render assembly SdfWorldRenderSpec/SdfWorldRenderBuilder, the Puck.SdfVm.Debug inspection engine, the composition/anchor surface (ISdfSceneEmitter/SdfCompositionFrameSource/SdfMaterialScope/SdfAnchor), Puck.SdfVm.Views (ViewStack/camera rigs), and Puck.SdfVm.Queries (IWorldQuery)). Use whenever touching the SDF ISA or packed word layout, the world kernels or their HLSL includes, engine capacities/frames/screen sources, render-assembly/backend selection, the SDF debug/gallery/bench tooling, composing a world program from emitters, anchor/view/camera-rig plumbing, deterministic world queries, or debugging world-render parity or GPU cost. Carries the C#↔HLSL contract pairs and settled engine semantics so they aren't re-derived or accidentally forked.
---

# The SDF world: one contract, two languages

Factual and procedural only: settled contracts, their exact sync points, and
how to verify. The user's current instruction outranks it — if this file
argues against a demanded change, it is stale; update it in the same change.
Plan of record for the render assembly:
[docs/sdf-world-render-centralization-plan.md](../../../docs/sdf-world-render-centralization-plan.md)
(status section = what is landed vs open).

> **Unification-contract alignment** (see "The unification contract" atop
> docs/overworld-demo-plan.md): the demo's world content is authored/loaded
> in-session (world-sculpt + `world.load`), not only via `--forge-town`.

> **ISA admission rule (owner-ratified 2026-07-12).** An op or shape earns a
> switch case ONLY if it cannot be composed EXACTLY from existing vocabulary —
> otherwise it ships as a builder macro emitting existing ops. Queued under
> this rule (the ISA-profile arc, docs/sdf-backlog.md — ready to fire; its
> carve-bake precondition landed 2026-07-12): `Star`/`RegularPolygon` retire
> into `RepeatPolar`-based
> builder macros; `Ellipse` STAYS as the one exact-curve citizen (`Ellipsoid`
> #6 remains the approximate path); shapes join ops on the compiled
> kernel-variant axis so unused vocabulary costs no register pressure.

## The C# ↔ HLSL sync pairs (KEEP IN SYNC — the whole list)

The C# ISA and the shader ISA are ONE contract. These are the live pairs;
change either side only with its partner in the same change:

| C# | HLSL | Contract |
|---|---|---|
| `SdfProgram` packed `Words` layout, op/shape/blend enums | `sdf-vm.hlsli` decode (`evaluateShape`, op switch) | instruction stream |
| `SdfProgram.InstanceMaskWordCount` (`max(1, ceil(n/32))`) | `sdfInstanceMaskWordCount` (reader's INNER word iteration only) | mask width formula |
| `SdfWorldEngine` pushWords[7] = LIVE uploaded program's width | `CompositeParams.instanceMaskWordCount` / `worldInstanceMaskBase` (sdf-world.hlsli) | mask buffer INDEXING (entry width + tile base) — host-pushed, never shader-derived |
| `PushConstantByteLength` = 32 B, words 0..7 | `CompositeParams` (8 uints: extent, tileGrid, viewportCount, childMask, screenMask, instanceMaskWordCount) | Stage 0/1 push |
| `DynamicTransformByteLength` = 32 B/slot | `sdfDynamicTransforms` (2×float4: position, quaternion) | dynamic transforms |
| `SdfProgramBuilder.MaxInstances` = 16384 | `SDF_MAX_INSTANCES` | instance cap (mask = 512 words/tile; everything DERIVES from the LIVE count via `InstanceMaskWordCountFor`, so a smaller program packs byte-identically — only the shader's `min(count, SDF_MAX_INSTANCES)` clamp constant tracks the cap. Raised 4096→16384 2026-07-09: the deferred-pending-measurement gate was the survey-row-15 uniform-grid cull, which landed mask-first and flattened the beam wall — see docs/sdf-bench-notes.md; the raise's only static cost is the ~41 MB mask buffer) |
| `SdfProgramBuilder.MaxScreenSurfaces` = 32 (raised from 8, the many-eyes arc leg 1; was 4 pre-Arc-3); material sentinel `ScreenMaterialId + 1 + screenIndex`; capped at 32 by the single-`uint` `screenMask` push word | 32 combined-image-sampler bindings (`screenSource0..31` at bindings 12-43 / registers t5-t36, samplers s0-s31; `sdfInstanceMasks`/`sdfScreenLights` shifted to t37/t38; the glyph atlas to binding 44 / t39 / s32) — the run is DERIVED (`ScreenSourceBindingBase + i`, `BuildScreenSourceBindings`), never hand-listed, so the descriptor pool auto-sizes from `GpuDescriptorPoolSizes.ForSets`; `SdfScreenLightEnv`/the screen-light buffer + grid rows all key off `MaxScreenSurfaces` | diegetic screens |
| `SdfWorldEngine.SetScreenSurface(index, origin, right, up, halfW, halfH)` — writes a host mirror; DIRTY-GATED (2026-07-16, perf plan Phase 1.2): the call compares against the mirror and is a no-op unless a value actually changed, so a MOVING screen slab (a walking creature's face) still samples correctly every frame `SdfEngineNode` polls per-index transform providers via `ISdfFrameSource.ScreenSurfaceTransforms` (default-implemented), while a static/unchanged poll costs no upload. Per-ring-slot dirty bits (`m_screenSurfaceDirty`, same pattern as `m_decalDirty`) — a real change dirties EVERY slot, `PrepareFrame` uploads + clears only the current slot's bit, so no slot ever renders a stale table. The screen-LIGHT buffer stays unconditional (excluded on purpose — see plan) | `screenSurfaces` StructuredBuffer read per pixel — NO kernel change was needed | moving screens |
| `SdfWorldEngine` screen-light buffer via `SetScreenLight` + `SdfFrame.AmbientScale/SunScale` (entries cover screens 0..31 + env — sized by `MaxScreenSurfaces`) | `sdfScreenLights` (t38, after `sdfInstanceMasks` t37; the glyph atlas t39 and `sdfDecalCells` t40 follow) + `SdfScreenLightEnv` (= 32) decode; the `renderView` light loop iterates all 32 | per-frame screen glow + room dimming |
| `SdfViewSnapshot.RenderScale` (default 1) → `PackViewports` quantizes ONE `RenderScaleQ` byte (1..255; child slots forced 255) into ViewportData's 6th float4 row (`ViewportByteLength` = 96 B) AND packs it 8-bit into `CompositeParams2.scaleQPacked` (`BuildCompositePush`) | `ViewportData.renderScale.x` + `worldRenderDims` (`max(1,(dim·q+127)/255)`, INTEGER — beam/instance-cull/views all derive the identical reduced extent) ↔ the composite's `scaleQPacked` unpack + bilinear upsample (`q == 255` = the exact-copy path, byte-identical) | per-view render scale — presentation-only downscale (reveal/immersed policy lives in `ScreenLayoutDirector`); native is bit-exact BY CONSTRUCTION. Post: `world-render-scale` (blur-envelope, calibrated live) |
| Grid-lock overlay (`GridOverlayState` record struct, `Puck.SdfVm` root namespace since 2026-07-10 — the `From(SnapConfig,…)` factory stays demo-side as `Puck.Demo.Editing.GridOverlayFactory` → `SdfFrame.GridFlags`/`GridWorldPitch`/`GridFloorY`/`GridObjectOrigin`/`GridObjectFrame`/`GridObjectPitch`/`GridObjectPatchRadius`, packed by `SdfWorldEngine.PackScreenLights` into `sdfScreenLights` rows 9..12; `ScreenLightByteLength` = `(MaxScreenSurfaces + 5)` float4) | `sdfScreenLights[SdfGridWorld=9]` (x=flags bit0 world/bit1 object, y=floorY, zw=world pitch XZ), `[SdfGridObjOrigin=10]` (xyz origin, w pitch X), `[SdfGridObjFrame=11]` (frame quat), `[SdfGridObjParams=12]` (x pitch Z, y patch radius); the `applyWorldFloorGrid`/`applyObjectGrid` tints at the `renderView` material call site (guarded `#ifdef SDF_SCREEN_SOURCES`) | the editors' grid visualization — env row 8 STAYS put (it doubles as the screen-count loop bound); adding a grid lane touches BOTH sides + `PackScreenLights`. Session-only authoring state (never sim/wire format); default 0 = byte-identical upload |
| The **tile-cull plane layout** — `SdfWorldEngine.TilePlaneCount` (= 4; sizes `m_tileBuffer` = `TilePlaneCount · viewportCap · tileGridX · tileGridY` floats) | `WorldTilePlaneCount` (= 4u) + `worldTileMarchStartIndex` (plane 0, no stride) / `worldTileFirstExitIndex` (1·stride) / `worldTileSecondEntryIndex` (2·stride) / `worldTileFarBoundIndex` ((count−1)·stride) in sdf-world.hlsli; stride = `tileGrid.x·tileGrid.y·viewportCount` | the four-bound teleport (Larsson "The Gunk") + the **F1 far bound** — plane 0 = the classic marchStart (the ONLY plane sdf-cull-args + the compositor read, so their indexing is stride-independent), planes 1/2 = the proven-empty gap `[firstExit, secondEntry]`, plane 3 = the far bound. sdf-beam WRITES all four (`TileBounds`), sdf-world-views READS planes 1/2/3. Every plane is a total function (MaxDistance = "no gap/no bound"). Growing the plane count touches `TilePlaneCount` + `WorldTilePlaneCount` + a new accessor on BOTH sides |
| The **F1 FAR BOUND** (perf plan Phase 5.1) — `SdfFrame.DisableFarBound` (default false = ON) packed by `SdfWorldEngine.PackScreenLights` into the far-field row `.x` at `(MaxScreenSurfaces + 7)`; `ScreenLightByteLength` = `(MaxScreenSurfaces + 8)` float4 | producer `coneMarchTileBounds`/`coneMarchFarBound` (sdf-beam) → `TileBounds.farBound` (plane 3); consumer `renderView`'s `if (traveled >= farBound) break;` beside the teleport; lever `sdfScreenLights[SdfFarFieldParams=39].x` → `worldFarBoundDisabled()` (disable pushes `farBound = MaxDistance+1` so the "off" side is exactly pre-F1) | the depth past which a tile's cone cannot produce any FOOTPRINT-ACCEPTED hit through MaxDistance. ⚠ LOAD-BEARING PROOF: the tail proves clearance against the FOOTPRINT-INFLATED threshold `min(map(center), sdfMapStepBound) − (chord + footprint)·t > SurfaceEpsilon`, stepping `≤ clearance/(1 + chord + footprint)` — NOT bare `ConeEpsilon` (the fine march accepts hits up to `footprint·t ≈ 0.001·t`, so an ε-proof is anti-conservative). footprint = `2·view.right.w / rectDims.y`, computed identically in beam (from `regionSizePx`) and views. OUTPUT-IDENTICAL on the shipped shading path (both render skyColor in `[farBound, MaxDistance]`); only step counts + the termination debug view change. March-path change (solidity + parity families + hero canary), re-golden the termination debug view only |
| The **F2 SHADOW LIGHT-SIDE EXIT** (perf plan Phase 5.1) — `SdfFrame.DisableShadowFarExit` (default false = ON) packed by `SdfWorldEngine.PackScreenLights` into the SAME far-field row's `.y` lane at `(MaxScreenSurfaces + 7)` (F1 rides `.x`; no `ScreenLightByteLength` growth) | consumer `softShadow` (sdf-world.hlsli) reads `worldShadowFarExitDisabled()` (`sdfScreenLights[SdfFarFieldParams=39].y`); the exit returns the running `result` when `ShadowSharpness·(clearanceTrue − (reach − traveled)) >= result·reach` | the no-further-darkening early out: `result` is a running MIN and the field is 1-Lipschitz along the ray, so once `clearanceTrue ≥ (reach − traveled) + result·reach/ShadowSharpness` no future sample can lower it. SOUND vs the classic penumbra term AND the true continuous penumbra (`≥ ShadowSharpness·cMin/reach ≥ result`, `cMin = clearanceTrue − remaining > 0`). ⚠ NOT bit-identical: the Aaltonen closest-approach parabola can undershoot past the exit point (its worst case → 0 at the near-radial-escape knife-edge `c'/prev → 2`, just inside the `y≥c` guard), so NO finite margin closes the strong form — skipping it brightens toward truth, never above it. MARCH-PATH change (solidity + parity families) |
| Diegetic CAMERAS (Puck.Demo): `CameraEye` (posed marker; world/placement/shape anchors) → `CameraFeedPool` (Arc-4 name; absorbed the former `CameraFeedEngine` — pool ≤4 offscreen 160×144 engines; a feed NEVER samples a screen wired to itself — binds 0; cross-feed TV-in-TV chains are legal one-frame-lag) → `ScreenWire` data (`brick:N`/`feed:N`/`named:NAME`/`none`) via `world.camera`/`world.wire` | each feed = one full world render pass/frame — budget honestly | placeable, wirable cameras |
| `sdfMaterialShade` takes accumulated `float3` radiance (not a scalar) | `sdfMaterialShade(..., float3 diffuse, ...)` — the two callers (`sdf-world.hlsli`, `sdf-world-rt-debug`) | shade funnel (colored lights) |
| `DebugViewModes.Names` (`Puck.SdfVm` root namespace since 2026-07-10, order IS the wire value, 11 entries incl. mask/overshoot/evals) | `DebugViewModeCount` (= 11)/`DebugViewModeNormals` + the `viewMode` switch (sdf-world.hlsli `renderView`) — mode 10 (`evals`, perf-plan Phase 0 instrumentation) is the one mode besides final shading that forces `useFinalShading` true, so its `sdfEvalCount` tally (a per-thread static, incremented at every map()-family call site in sdf-world.hlsli — never inside mapCore/sdf-vm.hlsli) reflects the real epilogue cost, not a debug shortcut | debug views — adding a mode touches BOTH plus the switch |
| `SdfDriftMonolith.Emit` (`Puck.SdfVm.Debug`, shared verbatim by the Post drift-ceiling stage and the demo gallery's monolith exhibit — CALIBRATED, change only with a recalibration) | n/a (host-side program emission only) | ⚠ the two hex-stride materials are reached POSITIONALLY through the `WallpaperFold` chain's `materialStride`, so `Emit` must be called into a builder holding NONE of the caller's own materials yet — it owns the whole material palette and must be emitted FIRST, or the positional stride reaches the wrong (caller-owned) material |
| bound-analysis modes | `SDF_BOUND_*` skip in `map()` | bounds gate |
| `SdfProgram.AnalyzeLipschitz` → per-program `stepScale` (1/L) baked into the segment-directory header's FREE `.y` lane (`PackBounds`), read back via `SdfProgram.StepScale` | `sdf-vm.hlsli` `mapCore` reads `asfloat(sdfWords[segmentOffset].y)` (guarded `> 0`) and multiplies its FINAL returned distance by it ONCE, after the walk | Lipschitz step clamp — a non-1-Lipschitz warp cannot overstep and hole. The warp factors are NOT one formula: a **Bend** (BendX/Y/Z) keys on a coordinate INSIDE the plane it rotates, so its exact operator norm is `1 + a` (a = rate·ρ), while **TwistY** keys OUTSIDE its rotated plane and collapses to `sqrt((2 + a² + a·sqrt(a²+4))/2)` — using the twist form for a bend under-clamps by up to 24% and HOLES the march (`BendOperatorNorm` vs `TwistOperatorNorm`). Log-spherical: factor `exp(w/2)`. Eccentric ellipsoid: factor = eccentricity. A **chamfer blend**: factor √2 at a flat/near-parallel seam (dihedral → 180°; exactly 1 at a perpendicular seam, → 0 at an acute one) — the one `SdfBlendOp` that is NOT 1-Lipschitz; smooth-min stays exactly 1. A **Displace/DomainWarp** sine field: factor `1 + amplitude·max|frequency_i|` — the INFINITY norm, not `‖f‖₂` (Displace's squared gradient norm is multilinear in the three squared sines ⇒ maximizes at a cube vertex; DomainWarp's `J - I` is a generalized permutation matrix whose spectral norm is its largest entry). `== 1.0f` EXACTLY for an isometric, chamfer/relief/warp-free program (byte-identical); the per-candidate `distanceScale` (Scale / the D2 log-spherical `r/density` correction) is a DISTINCT channel — never merged. Post: `sdf-lipschitz` (CPU bake assert; `warp-free stepScale == 1.0f` EXACTLY is the byte-identity contract) + `world-warp-solidity` / `world-log-sphere-solidity` (single-backend GPU solidity — parity CAN'T catch it, both backends overstep identically) + `world-chamfer` (chamfer cross-backend parity) |
| `SdfOp.LogSphere` (id 21) / `SdfProgramBuilder.LogSphere(shellRatio, twist)` — Data0.x = w (`ln(shellRatio)`, HOST-BAKED), Data0.y = twist (radians/shell), Data0.z = 1/w (HOST-BAKED); `AnalyzeLipschitz` folds `exp(w/2)` into `stepScale` | `SDF_OP_LOG_SPHERE` (21u) in `mapCore` — nearest-shell radial log-fold (`round`, like Repeat), an unconditional Z-spin (isometry, the Droste spiral), then `distanceScale *= shellScale` (the `r/density` correction, SAME channel as `SDF_OP_SCALE`, composes multiplicatively); `SDF_LOGSPHERE_MIN_RADIUS` floors the origin | D2 log-spherical DOMAIN warp — tiles space into infinite self-similar Droste shells. Radial-only fold ⇒ NO polar pinching; the r/density correction rides `distanceScale` (never `stepScale`); the `exp(w/2)` factor keeps the OVER-RELAXED march (omega 1.2) hole-free across shell boundaries. `AnalyzeSegment` gives it `SDF_BOUND_NONE` (unbounded periodic domain, via the `default` case — do NOT add a case). Op-unused programs stay byte-identical. Post: `world-log-sphere` (parity, `WorldLsbExact`) + `world-log-sphere-solidity` |
| PARKED instances (Arc 4): `SdfInstanceRange`/`BeginInstanceDynamic` carry an `Active` flag; an inactive slot packs the `SdfProgram.ParkedBoundRadius` (negative) bound sentinel — the reserved-pool "always fits by construction" contract is untouched, parked slots just become CHEAP | `collectInstanceMaskWord` (sdf-world.hlsli, the sphere-vs-cone tile test) and the full-eval enumeration (sdf-vm.hlsli, segment-range skip) each skip a negative-radius bound with ONE branch | parked-slot skip — beam/views cost tracks LIVE content, not reserved capacity. Demo-side, the pools (players/creator/companions) set `Active` per rebuild; a hidden-below-the-floor placement WITHOUT the flag is the pre-Arc-4 bug (264 always-tested instances = the 0.9→14.7ms regression) |
| The **2D-primitive family** (Vesica id-7 precedent, generalized): `SdfShapeType.RoundedRectangle`=8, `.RegularPolygon`=9, `.Star`=10, `.Trapezoid`=12, `.Ellipse`=13 (enum contiguous 0-14; `RoundCone`=11, `ScreenSlab`=14 unchanged) + `SdfLift { Revolve = 0, Extrude = 1 }` (`SdfLift.cs`) | matching `SDF_SHAPE_ROUNDED_RECT`/`_REGULAR_POLYGON`/`_STAR`/`_TRAPEZOID`/`_ELLIPSE` ids + `SDF_LIFT_REVOLVE`/`SDF_LIFT_EXTRUDE` (packed into Data1.y, decoded `> 0.5`) | SHARED lane layout for the whole family: Data0.xyz = the 2D shape params, Data0.w = the lift amount (revolve offset o OR extrude half-height h), Data1.x = smooth radius, Data1.y = lift mode, Data1.zw = per-shape host-baked constants (e.g. Star's baked `cos`/`sin(π/m)` ecs) |
| Builder methods `RoundedRectangle`/`RegularPolygon`/`Star`/`Trapezoid`/`Ellipse` (`SdfProgramBuilder`) + `SdfProgram.TryGetLocalBound` cases / `LiftedBoundRadius` helper | exact 2D cores `sdfRoundBox2D`/`sdfTrapezoid2D`/`sdfStar2D` (shared by RegularPolygon's m=2 case and Star)/`sdfEllipse2D`, lift ops `sdfExtrude2D`/`sdfRevolve2D`, lifted wrappers `sdfRoundedRect`/`sdfPolyStar`/`sdfTrapezoidSolid`/`sdfEllipseSolid` + their `evaluateShape` cases | evaluation + bounds for the family — each shape earns a REAL cull bound (unlike the approximate Ellipsoid #6); exact + factor-1 Lipschitz throughout (no `AnalyzeLipschitz` step clamp needed): extrusion is always exact, revolution is exact off-axis and a harmless conservative bound near the axis. Post: `world-2d-family` (both lift modes, cross-backend, `WorldHighContrast`) |
| `SdfOp.CellJitter` (id 22) / `SdfProgramBuilder.CellJitter(spacing, jitter, seed, tumble, materialVariants, flavor)` — Data0.xyz = spacing (HOST-CLAMPED ≥0.001/axis), Data0.w = jitter (peak-to-peak), Data1.xyz = 1/spacing (HOST-BAKED), Data1.w = clamped tumble [0,1], Material = materialVariants, Shape = seed, **Blend lane (header.z) = `SdfNoiseFlavor` {White=0 byte-identical default, Blue=1 R3 fixed-point low-discrepancy, Gaussian=2 central-limit}** — flavor reshapes ONLY the POSITION offset r0 (tumble/material-variant unaffected); `AnalyzeLipschitz`'s dedicated case (`chainTranslateReach += 0.5f * \|Data0.w\|`, treated exactly like a Translate of that magnitude — tumble/fold are isometries so nothing else accumulates) | `SDF_OP_CELL_JITTER` (22u) in `mapCore` — repeats like `SDF_OP_REPEAT`, then per-cell hashed position jitter (branched on `SDF_NOISE_*` = header.z), an optional hashed tumble (isometric rotation gated on `data1.w > 0`), and an optional hashed material-variant recolor, all keyed off `sdfPcg3d` (canonical PCG3D on the two's-complement cell index xored with the header seed) | stochastic domain-repeat fold — scatters a prototype into a jittered field from one instruction. The hash is INTEGER-ONLY, so cell decisions are bit-identical across both DXC targets; displacement and tumble are BOTH isometries (distanceScale untouched — only the jitter half-amplitude joins `AnalyzeLipschitz`, as a reach term, not a warp rate). ALL THREE flavors keep r0 in [0,1)^3, so the offset stays within ±jitter/2 per axis — the SAME bound White has — so NO Lipschitz change (the reach-independent `L_cj` clamp stays conservative for every flavor); Blue's lattice is INTEGER-ONLY (`asuint` + uint mul-add) so it too is bit-identical cross-backend. `AnalyzeSegment` gives it the `default` case (space-folding op, no world-space sphere is sound past it, segment not skippable — do NOT add a dedicated case). In-cell rule: the caller must keep jitter/2 + prototype radius ≤ min(spacing)/2 or displaced content crosses the cell boundary and holes the march (the builder validates only the displacement half it can see). ⚠Containment ≠ nearest-copy (verified 2026-07-08, slice capture): even with the in-cell rule satisfied, the single-cell `round` fold can pick the WRONG copy near a cell wall (a copy jittered toward the boundary is nearer to the adjacent cell's query than that cell's own copy), so the field OVERestimates at boundaries — visible seams, grazing-angle hole risk; keep jitter conservative. The same wrong-neighbor class applies to plain `Repeat`: exact ONLY for an on-center prototype within half-spacing per axis; an off-center/oversized prototype creases the field at cell walls with a march-holing overestimate (`SdfProgramBuilder.Repeat`'s doc carries the contract; iq's 3^k neighbor check judged NOT worth the interpreter cost at current usage). Post: `world-cell-jitter` (parity) + `world-cell-jitter-solidity` (single-backend GPU solidity) |
| `SdfOp.RepeatPolar` (id 23) / `SdfProgramBuilder.RepeatPolar(count, axis = SdfPolarAxis.Y, mirror = false, materialStride = 0)` — Shape = `SdfPolarAxis` {X, Y (default, XZ ground plane), Z}, Blend (header.z) = mirror flag, Material = per-sector stride, Data0 = (angle = 2π/count, 1/angle, count, 1/count) ALL HOST-BAKED, Data1 reserved | `SDF_OP_REPEAT_POLAR` (23u) in `mapCore` — folds the plane perpendicular to the axis into `count` equal angular sectors (nearest-sector `round` on the angle, like `SDF_OP_REPEAT`'s cell fold), an optional per-sector mirror (reflection across the sector bisector), then an optional per-sector material recolor | angular domain-repeat fold — the rotational sibling of `Repeat`/`WallpaperFold`: one authored prototype repeats around the axis (gears, wheels, rotunda columns, clock ticks, petals). The fold is a rotation (+ optional mirror reflection), BOTH isometries, so it is EXACTLY 1-Lipschitz — factor 1, NO `AnalyzeLipschitz` step clamp, same as `Repeat`/`WallpaperFold` (unlike `CellJitter`'s reach term or `LogSphere`'s `exp(w/2)` factor). Post: `world-repeat-polar` (cross-backend parity, Vulkan SPIR-V vs Direct3D 12 DXIL) |
| `SdfOp.Displace` (id 24) / `SdfProgramBuilder.Displace(frequency, amplitude)` — a FIELD op, ordered after the shapes it displaces; Data0.xyz = frequency, Data0.w = amplitude | `SDF_OP_DISPLACE` (24u) in `mapCore` — `result.distance += amplitude·sin(fx·x)·sin(fy·y)·sin(fz·z)` at the current folded point, evaluated in the same FIELD-op slot as `SDF_OP_ONION`/`SDF_OP_DILATE` | sine-product surface relief — the SDF-native height/parallax map, except the relief is REAL geometry (self-shadows/occludes). Separable basis, deterministic float trig (±1 LSB like the twist/bend warps) — parity-safe with no hashed noise table; richer fBm/gradient noise deferred (needs an integer-hash basis like `CellJitter`'s). NOT 1-Lipschitz: gradient reaches `amplitude·‖frequency‖`, so `AnalyzeLipschitz` folds `1 + amplitude·‖frequency‖` into `chainDisplaceWarpProduct` (a reach-independent metric-stretch factor, the same channel `DomainWarp` multiplies into — like the log-sphere product). Post: `world-displace` (parity) + `world-displace-solidity` (single-backend, the clamp holds the over-relaxed march) + the `sdf-lipschitz` stepScale assert |
| `SdfOp.DomainWarp` (id 25) / `SdfProgramBuilder.DomainWarp(frequency, amplitude)` — a POINT op, ordered before the shapes it warps; Data0.xyz = frequency, Data0.w = amplitude | `SDF_OP_DOMAIN_WARP` (25u) in `mapCore` — `localPosition += amplitude·(sin(fx·y), sin(fy·z), sin(fz·x))`, each axis driven by the NEXT axis's coordinate (non-separable), before the wrapped chain evaluates | cross-coupled organic domain warp — deterministic float trig, same parity posture as `Displace`. NOT an isometry: the Jacobian is `I` plus a perturbation of spectral norm ≤ `amplitude·‖frequency‖`, so the SAME `1 + amplitude·‖frequency‖` clamp joins `chainDisplaceWarpProduct`, and the point's max travel (`amplitude·√3`) additionally folds into a downstream twist/bend's reach term. Post: `world-domain-warp` (parity) + `world-domain-warp-solidity` (single-backend, the clamp holds the march) + the `sdf-lipschitz` stepScale assert |
| `SdfOp.SymmetryPlane` (id 26) / `SdfProgramBuilder.SymmetryPlane(normal, offset = 0f)` — Data0.xyz = the UNIT plane normal (host-normalized), Data0.w = the plane offset | `SDF_OP_SYMMETRY_PLANE` (26u) in `mapCore` — `p -= 2·min(dot(p, n) + offset, 0)·n`; for `n = x̂, offset = 0` this is `abs(p.x)` to the bit, an exact superset of the RETIRED `SDF_OP_SYMMETRY_X` | arbitrary-plane reflection fold — the general-normal fold that REPLACED the `SymmetryX`/`SymmetryY`/`SymmetryZ` opcodes (ids 13–15 collapsed into id 26; the builder keeps `SymmetryX/Y/Z()` as sugar that emit it): everything on the plane's negative side mirrors onto its positive side, so one authored half repeats mirror-imaged across ANY plane (a kaleidoscope leaf, a bilateral body, the reflect atom of a KIFS fold). A reflection is an ISOMETRY, so it is EXACTLY 1-Lipschitz — factor 1, NO `AnalyzeLipschitz` step clamp, same as `WallpaperFold`/`RepeatPolar`. Post: `world-symmetry-plane` (cross-backend parity, Vulkan SPIR-V vs Direct3D 12 DXIL) |
| The **Glyph op** — `SdfShapeType.Glyph` (SHAPE id 15, the next free shape after `ScreenSlab`=14) / `SdfProgramBuilder.Glyph(uvBottomLeft, uvTopRight, halfWidth, halfHeight, extrudeHalfDepth, distanceScale, material, blend, smooth)` + `SdfProgramBuilder.Text(atlas, text, origin, right, up, worldEmHeight, …)` (lays out via `Puck.Text.TextLayout`, emits one `ResetPoint`+`Translate`+`Rotate`+`Glyph` SEGMENT per char — the SdfVm→Puck.Text edge). LANE LAYOUT: Data0 = (`packedUvMin`, `packedUvMax` [each host-baked unorm2x16 of an atlas UV — packing frees a lane so Data1.x keeps the ISA-wide smooth], `distanceScale` [= atlas `DistanceRange`(texels) × worldPerTexel, HOST-BAKED], `extrudeHalfDepth`); Data1 = (`smooth`, `halfWidth`, `halfHeight`, 0). Uploaded ONCE via `SdfWorldEngine.SetGlyphAtlas(rgba, w, h)` (an `IGpuSurfaceUpload`), threaded through `ISdfFrameSource.GlyphAtlas` (`SdfGlyphAtlas` record, default null) polled once in `SdfEngineNode.EnsureEngine`. | `SDF_SHAPE_GLYPH` (15u) in `evaluateShape`, guarded on `SDF_GLYPH_ATLAS` (defined ONLY by `sdf-world-views.comp` — every other kernel gets the conservative extruded-quad fallback `sdfGlyphQuad`, so the beam cull/rt-debug see a solid cell box, never a hole). `sdfGlyph`: exact 2D quad distance `dQuad` FIRST, atlas tapped ONLY inside the band (`dQuad < 0.5·distanceScale`), `dPlane = max((0.5 − encoded)·distanceScale, dQuad)` then extruded — the band-cull is BOTH the perf trick and the conservative far field. Field from ALPHA (the true single-channel distance) via manual bilinear (`sdfGlyphSampleField`, `SampleLevel` explicit-LOD, s32/t39 combined-image-sampler at Vulkan binding 44 — DERIVED as `ScreenSourceBindingBase + MaxScreenSurfaces`, appended after the 32 screen sources in `SdfWorldEngine.viewsBindings` so D3D12 registers land t39/s32). | text as REAL world geometry: marchable, blendable, ENGRAVABLE (Subtraction) / EMBOSSABLE (Union proud of a slab — NEVER coplanar or the coincident zero-sets speckle) / floating. Reconstruction: GEOMETRY MARCHES THE TRUE SINGLE CHANNEL (alpha) — median-of-3 is C0-only at clash lines and must never be marched (the flat-coverage `GlyphDecal` tier LANDED 2026-07-09 — a SEPARATE material-level tier that samples the SAME atlas's ALPHA with a coverage threshold at SHADE TIME on a `ScreenSlab` carrier, NOT marched geometry: a per-screen decal table + shared cell buffer `sdfDecalCells` at Vulkan binding 45 / D3D12 t40 (after the glyph atlas t39; DERIVED as `GlyphAtlasBindingIndex + 1`), `SdfWorldEngine.SetScreenDecal`/`ClearScreenDecal` ↔ `sampleScreenSurface`'s decal-first branch, the `ISdfFrameSource.ScreenDecals` per-frame seam; Post `world-glyph-decal`; world-glyph geometry stays untouched, byte-identical when no decal is declared — an MSDF atlas would let the decal median-of-3, the alpha is what it samples now). Generation NOW: `Puck.Text.SdfCoverageAtlas.Generate` — an EXACT separable Euclidean distance transform (Felzenszwalb–Huttenlocher, deterministic) over a GDI+ coverage raster; the chamfer(1,√2) alternative overestimates ≤8.24% off-axis and would need a 1/1.0824 step-scale penalty, so exact-EDT + uniform worldPerTexel keeps Glyph FACTOR-1 (1-Lipschitz in texel space, bilinear preserves it — NO `AnalyzeLipschitz` case, like the 2D-lift family; a stretched cell is the caller's risk). Recommended marchable source is a pre-baked `msdf-atlas-gen` MTSDF atlas (true-distance in alpha by construction) — the runtime EDT is the no-toolchain fallback. Post: `world-glyph` (cross-backend parity, `WorldHighContrast` — sampled-texture/material-seam family; the fixture atlas is a deterministic in-process 5×7 font, no font-availability dependency; a no-atlas control proves the atlas reaches the shader). Adapted from SignedDistanceTerminal's `sdfMsdfGlyph`. |
| The **SampledRegion op** — `SdfShapeType.SampledRegion` (SHAPE id 16, the next free shape after `Glyph`=15) / `SdfProgramBuilder.SampledRegion(boxMin, cellSize, dimX, dimY, dimZ, brickWordOffset, boundaryFloor, material, blend = Subtraction)` (`MaxSampledRegionDim = 1023`). LANE LAYOUT: Data0 = (`boxMinX`, `boxMinY`, `boxMinZ`, `cellSize`) — box extent derives as `dims·cellSize`; Data1 = (`smooth` [ISA-wide, = 0 for the hard subtraction a brick composes with], `packedDims` [uint bits: 3×10-bit dims ≤1023/axis, host-packed `dimX \| dimY<<10 \| dimZ<<20`], `brickWordOffset` [uint bits: the brick's base word in the pool], `boundaryFloor` [= margin/λ, host-baked outside-box lower-bound offset]). The two uint bit-fields ride the float lanes as reinterpreted bits (like Glyph's `PackUv`) and round-trip exactly through `WriteVector4`. `TryGetLocalBound` returns the box CIRCUMSPHERE (center = boxMin + extent/2, radius = |extent|/2) — a REAL cull bound, so `AnalyzeSegment`/`ShapeReachRadius`/`PackInstances` treat it as any Subtraction-blend instance and `IsShadowTransparentInstance` auto-flags it (Path B). `AnalyzeLipschitz` = factor 1 EXACTLY (λ is folded into the STORED values at bake, not `stepScale`), so brick-free scenes stay byte-identical AND a brick adds no global step tax. | `SDF_SHAPE_SAMPLED_REGION` (16u) in `evaluateShape` (NOT stripped under `SDF_CORE_OPS` — the core-ops views variant binds the pool), guarded on `SDF_SAMPLED_REGIONS` (the world-views + core-ops + beam kernels bind the pool as of **W0b**; the instance-cull/rt-debug/diagnostic kernels take the fallback). `sdfSampledRegion`: `local = (p−boxMin)/cellSize`; OUTSIDE the box returns `dist(p,box) + boundaryFloor` (a valid scaled lower bound — positive, so Subtraction stays saturated and the accumulator is exact); INSIDE, manual TRILINEAR over 8 `sdfBrickPool` loads (sample CENTRES at integer voxel indices, `sampleCoord = local − 0.5`, clamp-to-edge border half-voxel) with a `precise` lerp chain (fp-contraction pinned OFF → bit-stable SPIR-V/DXIL). WITHOUT `SDF_SAMPLED_REGIONS` (the instance-cull/rt-debug/diagnostic kernels): returns `SDF_FAR_DISTANCE` (the conservative UNION-HULL fallback — a Subtraction compose never bites, region renders uncarved, never holed — the Glyph quad-fallback precedent). WITH `SDF_SAMPLED_REGIONS` but a POOL-LESS engine (capacity-0 filler): `sdfSampledRegion` calls `sdfBrickPool.GetDimensions` and, seeing the single-float filler (`numVoxels <= 1`), takes the SAME `SDF_FAR_DISTANCE` fallback — so a filming view renders a SampledRegion world UNCARVED. ⚠GROUND TRUTH: the stored brick distances are `/√3` scaled, so a ZEROED read (an allocated-but-UNBAKED 64 MB pool, or a filler sampled without the gate) = stored distance 0 = the box interior sitting entirely on the carve surface ⟹ the Subtraction carves a box-shaped HOLE across the whole region. This was a LIVE defect for filmed carves: pre-2026-07-17 every offscreen `SdfCameraView`/`NestedWorldView` allocated its own default 64 MB pool it never baked into, so filming a carved world rendered the carve box as a hole (and wasted ~4 GB at the 64-view cap). The GetDimensions gate + capacity-0 view engines fix both. Normals: the `evaluateShapeGradient` `default` arm's 4-tap FD (4 extra pool samples, hit-only). Pool: `[[vk::binding(46,0)]] StructuredBuffer<float> sdfBrickPool` (one f32/voxel), per-consumer D3D12 register via `SDF_BRICK_POOL_REGISTER` (views set t41 after `sdfDecalCells` t40; beam t4 after its mask t3 — the `SDF_INSTANCE_MASKS_REGISTER` pattern). | a SAMPLED distance-field brick: the settled-carve UNION field baked O(1) so the primary/shadow/AO marches stop paying O(carve-count), composed as ONE ordinary Subtraction instance (crack-free by construction — the subject stays fully analytic). W0a shipped the ISA + shape eval; W0b landed the engine tier — the persistent device-local pool (`SdfWorldEngineOptions.BrickPoolVoxelCapacity`, default 64 MB = `SdfWorldEngine.DefaultBrickPoolVoxelCapacity` = `SdfBrickPoolLayout.TotalVoxels`; frozen at construction, 0 = no pool: baking and rendering are SPLIT — a pool-less engine still ACCEPTS a SampledRegion program (rendered uncarved via the GetDimensions fallback, see the sdfSampledRegion row), only `RequestBrickBake` stays a loud rejection), the static `SdfBrickPoolLayout` (8 slots × 128³), the closed-form sphere-union baker `sdf-brick-bake.comp` (distances stored `/√3`, sliced ≤256K voxels/frame off the render's frame-timing bracket), and the `RequestBrickBake`/`GetBrickState` API with the two-revision-bump handoff (`BrickBakeState` Empty→Baking→Ready). `SdfViewsKernelVariants` classifies SampledRegion as CORE so a baked carve scene keeps the faster core-ops variant. ⚠ editing `sdf-world-views.comp.hlsl` does NOT reliably retrigger the `sdf-world-views-core.comp` recompile (it includes, not `#include`s a `.hlsli`) — the stale-bytecode gotcha bit W0b once; delete + rebuild the core `.spv`/`.dxil` after touching the views source. The planner (`SdfCarveBakePlanner`) is W1a. Plan of record: `docs/carve-bake-plan.md`. Post `world-sampled-region` (authored W2a, informational this arc). |
| The **scoped accumulator** — `SdfOp.PushField` (id 27) / `SdfOp.PopField` (id 28); `SdfProgramBuilder.PushField(compose = Union, smooth = 0f)` / `PopField()` (depth cap `SdfProgramBuilder.MaxFieldScopeDepth = 1`; the compose blend + smooth ride the POP instruction's Blend lane + Data1.x — the SAME lanes a `ShapeBlend` uses; PUSH carries no data) | `SDF_OP_PUSH_FIELD` (27u) / `SDF_OP_POP_FIELD` (28u) in `mapCore` (`SDF_MAX_FIELD_SCOPE_DEPTH = 1u`) — PUSH saves the running accumulator into a one-deep `(savedFieldDistance, savedFieldMaterial)` slot and reseeds `result` to `SDF_FAR_DISTANCE`; POP restores the parent as the blend LHS and feeds the scope's `result.distance` as a CANDIDATE into the **shared blend tail** (the material-winner switch + `blendShape`) SHAPE now also uses — so a POP costs no second copy of the ten-way blend switch (`composePending` gates the tail) | one-deep SCOPED FIELD ACCUMULATOR — the fix for "a field op / intersection shells the WHOLE scene": every accumulator-reading op (the intersection family, `Onion`/`Dilate`/`Displace`) between a balanced `PushField`/`PopField` acts on the scope's own shapes ONLY, then composes back with the POP's blend. A scope touches the FIELD, never the POINT (`localPosition`/`distanceScale`/`parityMaterialDelta`), so `ResetPoint` is unchanged and per-shape cull bounds after the Push stay sound. THE FUSION TRAP: a POP's candidate is ALREADY in world units — it is NOT re-multiplied by `distanceScale` and does NOT take `parityMaterialDelta` (unlike SHAPE). Material tie-break is strict `<` (parent keeps its material on a tie). `AnalyzeSegment` gives a Push/Pop segment `segmentEligible = false` (never whole-skip a scope boundary) but leaves `chainBoundable` TRUE (correction #1 — bounds after the Push survive); `HasUnmaskableCompose` tracks scope depth so a SCOPED field op / intersection is NO LONGER unmaskable (the culling payoff — only a POP with an intersection-family compose at depth 0 is), and `MaxSmoothBlendRadius` folds a POP's soft compose halo. THE MARGIN RULE (the payoff's fine print): a scoped field op is maskable but GROWS the surface OUTWARD past the authored geometry bound, so `PackInstances` must inflate the instance's finite bound by that reach or the beam masks the tiles the grown shell reaches and the surface HOLES at the tile seams — `MaxScopedFieldReach` folds it in the same way `MaxSmoothBlendRadius` folds the POP compose halo (per-op: `Onion(t)` outer surface moves out by `t`, `Dilate(r)` by `r`, `Displace(a)` by `a`; field ops SUM within a scope, max across scopes; an UNscoped field op stays unmaskable, so its 1e30 sentinel covers it and no margin is computed). Verified 2026-07-08 by a scoped-`Dilate(1.5)` sphere with a bound covering only the un-dilated radius: pre-fix the beam clipped the shell into a blocky tile-truncated blob, post-fix the full dilated sphere renders intact. `AnalyzeLipschitz` folds a conservative √2 (`hasChamferPop`) for a chamfer compose ("expect the next bug here"). Op-unused (scope-free) programs stay BYTE-IDENTICAL (verified: overworld render sha256-identical). Post: `world-scope` (scoped intersection renders as the intersection of its own members; a scoped instance is maskable with `instanced == flat`; its near-endpoint cluster + the CPU pin also prove `blendSmoothUnion`'s FAR + NEAR endpoints — the scope-seed prerequisite; there is NO separate `sdf-blend-endpoints` stage) |
| `Puck.SdfVm.Queries.SdfFieldEvaluator` (GRAVITY ARC Wave 1, `IWorldQuery`+`IFieldEvaluator`, the SECOND `IWorldQuery` provider after `BakedWorldQuery`) — a WARP-FREE CPU interpreter of the live `SdfProgram.Instructions` typed seam (not the packed `Words`), in `FixedQ4816`/`FixedVector3`. Ctor walks the stream once, asserting every op/shape is in the supported rigid subset (throws `ArgumentException` naming the first excluded one) and converting each instruction's Data0/Data1 floats to `FixedQ4816` ONCE into a cached `CompiledInstruction[]` — including a `Rotate`'s baked quaternion, transcribed via `rotatePointByInverseQuaternion`'s cross/mul/add form (no runtime sin/cos). `TryFieldGradient` is a 4-tap tetrahedron central difference over `TryDistance` (Decision B — not a `mapGradCore` dual port); the five `IWorldQuery` verbs sphere-trace `TryDistance` (`WorldQueryConfidence.Exact`) | `mapCore`'s RIGID op cases (`SDF_OP_RESET`/`_TRANSLATE`/`_ROTATE`/`_SCALE`/`_REPEAT`/`_REPEAT_LIMITED`/`_SYMMETRY_PLANE`/`_ELONGATE`/`_ONION`/`_DILATE`/`_PUSH_FIELD`/`_POP_FIELD`/`_SHAPE`) + `evaluateShape`'s Sphere/Box/ScreenSlab/Torus/Plane/RoundCone/Capsule/Cylinder/Ellipsoid/Vesica bodies + `blendShape`/`blendSmoothUnion` — the shared blend tail's semantics, INCLUDING op-order effects (a strict material-winner compare before the distance blend), mirrored exactly | a SECOND, INDEPENDENT interpreter of the SAME instruction stream mapCore walks (a deliberate dual implementation, like `SdfProgram`'s own host-side `AnalyzeBounds`/`AnalyzeLipschitz` passes — NOT shader codegen). WARP-FREE means it rejects `TransformDynamic` (no per-frame dynamic-transform table in this evaluator's signature — a future wave could thread one through without touching any other op's status), `BendX`/`BendY`/`BendZ`/`TwistY`/`LogSphere`/`CellJitter`/`RepeatPolar`/`Displace`/`DomainWarp` (runtime trig this wave doesn't implement in fixed point), and `WallpaperFold` (isometric and so tractable in principle, but its 17-group parity-keyed cell logic was judged real added surface, not a five-minute mirror — Wave 1's reconciliation finding: the plan's initial excluded-op list named 9 ops from `AnalyzeSegment`'s bound-skip default-case partition, which is a SUPERSET reflecting a DIFFERENT concern — "no sphere bound is sound past this op" — not "uninterpretable"; `Scale`/`Repeat`/`RepeatLimited`/`SymmetryPlane`/`Elongate`/`Onion`/`Dilate` sit in that same default case yet ARE directly interpreted here, exactly 1-Lipschitz by construction, needing no step-scale clamp the way the GPU's warped programs do). Three shapes are excluded for the same reason at the shape level (not itemized in the arc plan, a Wave 1 finding): `RegularPolygon`/`Star` (`sdfStar2D`'s runtime `atan2`) and `Ellipse` (`sdfEllipse2D`'s analytic cubic solve, `acos`/`pow`); `Glyph` needs texture sampling. Gravity = `-gradient.Normalize()` is the CONSUMER's one-line derivation (`IFieldEvaluator`'s whole reason to exist as its own seam) — the field itself never encodes "planet" or "down". Verified (Wave 1 harness, D:\kittoes0124\...\scratchpad\field-eval-harness, NOT committed): hand-computed sphere/translated-box/rotated-capsule/SmoothUnion points match to <5e-7 (float-rounding-of-the-input floor, not fixed-point error); a 200-point random sweep vs. an independent double-precision reference measured max\|err\| ≈ 2.3e-5 for sphere and box; `TryFieldGradient` on a sphere at 10 points (axes, diagonals, near-degenerate) measured max\|err\| ≈ 2.2e-3 against the analytic radial unit vector, well inside GradientEpsilon's documented 0.01-world-unit probe; 1000 seeded points evaluated twice against a multi-op program (Translate+Rotate+Box+ResetPoint+SmoothUnion-Sphere) were BIT-IDENTICAL (0 mismatches on the raw `FixedQ4816.Value`) — Wave 2's gate should freeze thresholds no tighter than these measured numbers |
| `DynamicTransform.CastsSoftShadow` (SdfFrame.cs; default `true` = casts) → `SdfWorldEngine.PackDynamicTransforms` packs it into the dynamic transform's POSITION row `.w` lane (0 = casts, 1 = shadow-suppressed) — the lane that was a hardcoded 0 pad, so a default-casts frame is BYTE-IDENTICAL | `sdfShadowParticipationActive` (a `static bool`, false default, declared under `SDF_DYNAMIC_TRANSFORMS` beside `sdfShadowMaskActive` in `sdf-vm.hlsli`) flipped `true`/`false` UNCONDITIONALLY around the ONE `softShadow` call in `sdf-world.hlsli` (matching `sdfShadowMaskActive`'s lifetime) → the per-instance skip in `sdfNextVisibleInstanceRange` (`sdfShadowParticipationActive && meta.x == SDF_BOUND_DYNAMIC && sdfDynamicTransforms[2u*meta.y].w > 0.5 ⟹ continue`, mirroring the parked-radius skip) + the gather-side twin `sdfInstanceShadowSuppressed` skip in `sdfShadowGather`'s two candidate loops (gated on the RAW condition — the gather runs BEFORE the flag flips and is inherently shadow-scoped) | per-frame per-instance soft-shadow PARTICIPATION — a suppressed dynamic instance drops out of the soft-shadow march ONLY (camera/AO/coverage marches keep the flag false and are untouched; static instances have no dynamic slot and always cast). Default = casts, byte-identical for every existing consumer; no program rebuild (it rides the per-frame dynamic-transform upload). Consumer: `Puck.World`'s `WorldFrameSource` computes it per entry (local seats always cast; a stand-in casts iff within `WorldRenderSettings.ShadowCrowdRadius` of a joined seat — the 128-player crowd lever, `world.shadows [tier] [crowd-radius]`). The three soft-shadow fallback modes (gather cull / camera-tile / flat) all resolve through `sdfNextVisibleInstanceRange`, so the flag is set unconditionally to cover all three. ⚠ editing `sdf-world.hlsli`/`sdf-vm.hlsli` needs the `sdf-world-views-core.comp` `.spv`/`.dxil` deleted before build (the include-not-#include stale-bytecode gotcha). No dedicated Post stage (a demo/World-greenfield lever — the default-casts path keeps `world-shadow-cull`/`world-swarm` bit-identical) |


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
envelope), and rebuild-on-revision-change (`ISdfSceneEmitter.Revision`,
summed). `ISdfSceneEmitter.OwnsMaterialScope` (true for a positional-stride
author — `WallpaperFold`/`RepeatPolar` with `materialStride`) tells the
composition host to wrap that emitter's `Emit` in a
`SdfProgramBuilder.BeginMaterialScope()` scope (`SdfMaterialScope`), clamping
any positional reach to the emitter's OWN added materials instead of leaving
it to author discipline (the `SdfDriftMonolith` hazard the scope mechanism
was built to close). `OverworldFrameSource` still hand-builds its own
program (not yet wired onto `SdfCompositionFrameSource`); `WorldSceneEmitter`/
`CreatorSceneEmitter`/`SdfDebugEmitter` (in `Puck.SdfVm.Debug`) are the ported
emission cores.

**Anchors (`Puck.SdfVm` root).** `SdfAnchor` (position + orientation
snapshot, `System.Numerics` float) / `ISdfAnchorSource` (the read seam) /
`SdfAnchorTable` (the sim-side per-tick registry: `BeginTick`/`Publish` key
on NAME not insertion order, so a name that stops publishing stops resolving
without ever being reassigned) / `SdfAnchorKind` (World/Body/Instance — the
engine-side classification a host's own anchor kinds map onto, e.g.
`CameraAnchorKind.Shape → Body`). **Float verdict (recalibration float
sweep, 2026-07-10): PRESENTATION, not simulation state.** An anchor is
published FROM an already-computed sim pose (a `FixedVector3`/`WorldCoord3`
position converted to `Vector3` once at publish time) and its only consumer
is `Views.SdfCameraView.Resolve` (a camera rig pose) — nothing reads
`TryResolveAnchor` back into sim state. Safe by the same rule that makes
`ViewStack`/`ViewTransition` presentation-only.

**Views (`Puck.SdfVm.Views`).** `ISdfCameraRig` + five shapes — `OrbitRig`
(shared `Offset(yaw, pitch, distance)` static, the trig every object-intent
camera in this codebase used to hand-roll), `FollowRig`, `FixedRig`,
`FirstPersonRig`/`DollyRig` (consumer-pending). `ViewStack` — the
hypervisor-identity primitive that absorbed `CameraFeedPool`: `IViewContent`
(`SdfCameraView`/`GuestSurfaceView`/`NestedWorldView`) registers by NAME
(`ViewId`/`Register`/`Release`/`Resolve`/`ResolveGlow`/`IsLive`), budgeted
round-robin refresh (`MaxRegisteredViews` = 64 live, `RefreshBudget` = 4
rendering per frame, deterministic cursor — never wall-clock), and the
SELF-REFERENCE rule (`SetWiredScreens`: inside view V's own render, any
screen surface wired to V binds 0, so a wall of monitors never compounds
frame over frame; one-frame-lag TV-in-TV chains through a DIFFERENT view
stay legal). `ViewTransition`/`ViewLayout`/`ViewBinding` — eases a
`ViewStack` composition between two layouts: the REGION lerps continuously,
the VIEW occupying it is a hard cut at the eased midpoint (0.5). Float
verdict: presentation (an `elapsedSeconds` render-clock parameter the caller
advances deterministically, same shape as `ScreenLayoutDirector`'s existing
pane easing — not simulation state). `ScreenSlotPriority` orders views
informationally; a screen-SURFACE slot claim is the separate
`Puck.Demo.Overworld.ScreenSlotLedger` arbitration.

**Queries (`Puck.SdfVm.Queries`).** `IWorldQuery`
(`Raycast`/`SphereCast`/`Overlap`/`TryGroundHeight`/`LineOfSight`) — fully
`FixedQ4816`/`FixedVector3`/`WorldCoord3`, synchronous, every result tagged
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
`Bounded` answers. `SdfFieldEvaluator` (GRAVITY ARC Wave 1) wraps a LIVE
`SdfProgram` directly — `Exact` answers; see its sync-pair table row above
for the interpreted subset, the excluded-ops reconciliation, and the
measured tolerances. `Puck.SdfVm` gained a direct `Puck.Maths` project
reference for this namespace (previously reached it only transitively) —
a new but ordinary downward edge, see project-map.md. `IFieldEvaluator`
(`TryDistance`/`TryFieldGradient`) is a SEPARATE, narrower interface
`SdfFieldEvaluator` also implements — the field-only seam a gravity/
magnetism/wind consumer binds instead of the five-verb `IWorldQuery`;
`BakedWorldQuery` does NOT implement it (capability checked via
`FieldEvaluatorCapabilities`, never stubbed). **`Puck.SdfVm.Queries.Debug`:**
`WorldQueryDriftInstrument` measures the evaluator's answers against two
INDEPENDENT channels outside an epsilon-shell exclusion around its own zero
set (a near-surface point is not a fair sign test for any coarser
representation) — a GPU render (a sphere-trace invariant: a march can never
accept a hit closer than the field's true value at its origin) and a
`BakedWorldQuery` cross-check sourced from the evaluator's own samples (a
query-PLUMBING consistency check, not a field-math one). Backs two Post
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
`GraphBuilder.UnsupportedReason` is the ONE owner of the world graph's
deferred rejections (cross-backend `produce`, `live-camera` pending its
child node) — pre-flighted in `Program` BEFORE the window host builds, so
rejection is an attributed stderr line and exit 2, never a mid-host crash.

**The capacity probe (the envelope pattern, live in the overworld).**
`OverworldFrameSource` builds ONE worst-case probe program — every diegetic
screen lit, the creator pool in its largest emission form (including its
reserved per-shape modifier ops) — measures it (the probe is never rendered),
and feeds the result through `SdfWorldRenderSpec.ProgramWordCapacity` /
`InstanceCapacity`, so live rebuilds vary freely BELOW the frozen envelope.
Any NEW optional emission MUST also be added to the probe (`BuildProgram`'s
`probeWorstCase` path), or a live rebuild can outgrow the buffers and
`UploadProgram` throws loudly.

**The screen-slot borrow (index 3).** Creator mode's preview easel borrows
screen-surface slot 3 (`CreatorSceneRenderer.PreviewScreenIndex`) while the
mode is up: the frame source suppresses that cabinet's `ScreenSlab` (it
degrades to its lit flat material for the session, relights on exit) and the
render node muxes the slot's provider to the bake preview — BOTH gate on the
same flag in the same rebuild, so the surface table and the sources can never
disagree. Copy that shape for any future slot sharing: one flag, one rebuild.

## Shader build mechanics

`dotnet build src/Puck.SdfVm -c Release` runs DXC IN PLACE in the source tree
(build FAILS without DXC; `/p:DxcCommand=` overrides) — commit the
regenerated `.spv`/`.dxil` with the source change. Editing `sdf-world.hlsli`
or `sdf-vm.hlsli` recompiles `sdf-instance-cull.comp`, `sdf-beam.comp`,
`sdf-world-views.comp`, AND `sdf-cull-args.comp`.
`ValidateShaderBytecodeSources` fails the build on bytecode without a
same-stem `.hlsl` (Puck.SdfVm only; the other shader-shipping projects lack
the guard — a known follow-up).

**The MASK-FIRST pass order (the uniform-grid instance-cull arc).** FIVE
kernels per frame: `sdf-instance-cull.comp` (per-tile instance mask — the
host-built CSR uniform grid from `SdfInstanceGrid`, bin-by-CENTER with the
LOAD-BEARING `footprintPad` = max binned radius; dynamic/unmaskable instances
ride an always-tested list; a disabled grid falls back to the flat
per-instance loop, forced by `SdfProgramBuilder.Build(buildInstanceGrid:
false)` / the demo's `sdf.grid off` verb) → `sdf-beam.comp` (cone march over
the TILE-MASKED field via `mapMasked` — bit-exact per the bound-sizing
contract because a masked-out instance's bound excludes the tile's whole
cone; this is what flattened the O(instances) beam wall: 187.8→6.6 ms @4096,
119→1.0 ms @1024 scattered carves) → `sdf-cull-args` → views → composite.
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

The POST battery is the gate (routing lives in the `verifying-puck-changes`
skill): `dotnet run --project src/Puck.Post -c Release`; the world-path
stages exercise every kernel. Live checks: `--run docs/examples/world-*.json`
with `--capture`. The battery's own output is the source of truth for which
stage covers what — do not pin stage names in comments or docs.
