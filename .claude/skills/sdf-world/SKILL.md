---
name: sdf-world
description: Working on the SDF VM and world renderer — src/Puck.SdfVm (SdfProgram/SdfProgramBuilder, SdfWorldEngine/SdfEngineNode, the Assets/Shaders/Sdf kernels) and the shared render assembly (SdfWorldRenderSpec/SdfWorldRenderBuilder in Puck.Demo). Use whenever touching the SDF ISA or packed word layout, the world kernels or their HLSL includes, engine capacities/frames/screen sources, render-assembly/backend selection, or debugging world-render parity or GPU cost. Carries the C#↔HLSL contract pairs and settled engine semantics so they aren't re-derived or accidentally forked.
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
| `SdfProgramBuilder.MaxInstances` = 4096 | `SDF_MAX_INSTANCES` | instance cap (mask = 128 words/tile; everything DERIVES from the LIVE count via `InstanceMaskWordCountFor`, so a smaller program packs byte-identically — only the shader's `min(count, SDF_MAX_INSTANCES)` clamp constant tracks the cap. 16384 DEFERRED pending the perf-bench beam-slope measure; survey row 15 uniform-grid cull is the gate) |
| `SdfProgramBuilder.MaxScreenSurfaces` = 8 (raised from 4, Arc 3); material sentinel `ScreenMaterialId + 1 + screenIndex` | 8 combined-image-sampler bindings (`screenSource0..7` at bindings 12-19; `sdfInstanceMasks`/`sdfScreenLights` moved to t13/t14) | diegetic screens |
| `SdfWorldEngine.SetScreenSurface(index, origin, right, up, halfW, halfH)` — the surface table re-uploads EVERY frame from a host mirror (no longer only at `UploadProgram`), so a MOVING screen slab (a walking creature's face) samples correctly; `SdfEngineNode` polls per-index transform providers via `ISdfFrameSource.ScreenSurfaceTransforms` (default-implemented) | `screenSurfaces` StructuredBuffer read per pixel — NO kernel change was needed | moving screens |
| `SdfWorldEngine` screen-light buffer via `SetScreenLight` + `SdfFrame.AmbientScale/SunScale` (entries now cover screens 0..7 + env) | `sdfScreenLights` (t14, the LAST views SRV) + `SdfScreenLightEnv` decode; the `renderView` light loop | per-frame screen glow + room dimming |
| Grid-lock overlay (Puck.Demo `GridOverlayState` facade → `SdfFrame.GridFlags`/`GridWorldPitch`/`GridFloorY`/`GridObjectOrigin`/`GridObjectFrame`/`GridObjectPitch`/`GridObjectPatchRadius`, packed by `SdfWorldEngine.PackScreenLights` into `sdfScreenLights` rows 9..12; `ScreenLightByteLength` = `(MaxScreenSurfaces + 5)` float4) | `sdfScreenLights[SdfGridWorld=9]` (x=flags bit0 world/bit1 object, y=floorY, zw=world pitch XZ), `[SdfGridObjOrigin=10]` (xyz origin, w pitch X), `[SdfGridObjFrame=11]` (frame quat), `[SdfGridObjParams=12]` (x pitch Z, y patch radius); the `applyWorldFloorGrid`/`applyObjectGrid` tints at the `renderView` material call site (guarded `#ifdef SDF_SCREEN_SOURCES`) | the editors' grid visualization — env row 8 STAYS put (it doubles as the screen-count loop bound); adding a grid lane touches BOTH sides + `PackScreenLights`. Session-only authoring state (never sim/wire format); default 0 = byte-identical upload |
| Diegetic CAMERAS (Puck.Demo): `CameraEye` (posed marker; world/placement/shape anchors) → `CameraFeedPool` (Arc-4 name; absorbed the former `CameraFeedEngine` — pool ≤4 offscreen 160×144 engines; a feed NEVER samples a screen wired to itself — binds 0; cross-feed TV-in-TV chains are legal one-frame-lag) → `ScreenWire` data (`brick:N`/`feed:N`/`named:NAME`/`none`) via `world.camera`/`world.wire` | each feed = one full world render pass/frame — budget honestly | placeable, wirable cameras |
| `sdfMaterialShade` takes accumulated `float3` radiance (not a scalar) | `sdfMaterialShade(..., float3 diffuse, ...)` — the two callers (`sdf-world.hlsli`, `sdf-world-rt-debug`) | shade funnel (colored lights) |
| `DebugViewModes.Names` (Puck.Demo, order IS the wire value, 6 entries) | `DebugViewModeCount`/`DebugViewModeNormals` + the `viewMode` switch (sdf-world.hlsli `renderView`) | debug views — adding a mode touches BOTH plus the switch |
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
| The **scoped accumulator** — `SdfOp.PushField` (id 27) / `SdfOp.PopField` (id 28); `SdfProgramBuilder.PushField(compose = Union, smooth = 0f)` / `PopField()` (depth cap `SdfProgramBuilder.MaxFieldScopeDepth = 1`; the compose blend + smooth ride the POP instruction's Blend lane + Data1.x — the SAME lanes a `ShapeBlend` uses; PUSH carries no data) | `SDF_OP_PUSH_FIELD` (27u) / `SDF_OP_POP_FIELD` (28u) in `mapCore` (`SDF_MAX_FIELD_SCOPE_DEPTH = 1u`) — PUSH saves the running accumulator into a one-deep `(savedFieldDistance, savedFieldMaterial)` slot and reseeds `result` to `SDF_FAR_DISTANCE`; POP restores the parent as the blend LHS and feeds the scope's `result.distance` as a CANDIDATE into the **shared blend tail** (the material-winner switch + `blendShape`) SHAPE now also uses — so a POP costs no second copy of the ten-way blend switch (`composePending` gates the tail) | one-deep SCOPED FIELD ACCUMULATOR — the fix for "a field op / intersection shells the WHOLE scene": every accumulator-reading op (the intersection family, `Onion`/`Dilate`/`Displace`) between a balanced `PushField`/`PopField` acts on the scope's own shapes ONLY, then composes back with the POP's blend. A scope touches the FIELD, never the POINT (`localPosition`/`distanceScale`/`parityMaterialDelta`), so `ResetPoint` is unchanged and per-shape cull bounds after the Push stay sound. THE FUSION TRAP: a POP's candidate is ALREADY in world units — it is NOT re-multiplied by `distanceScale` and does NOT take `parityMaterialDelta` (unlike SHAPE). Material tie-break is strict `<` (parent keeps its material on a tie). `AnalyzeSegment` gives a Push/Pop segment `segmentEligible = false` (never whole-skip a scope boundary) but leaves `chainBoundable` TRUE (correction #1 — bounds after the Push survive); `HasUnmaskableCompose` tracks scope depth so a SCOPED field op / intersection is NO LONGER unmaskable (the culling payoff — only a POP with an intersection-family compose at depth 0 is), and `MaxSmoothBlendRadius` folds a POP's soft compose halo. THE MARGIN RULE (the payoff's fine print): a scoped field op is maskable but GROWS the surface OUTWARD past the authored geometry bound, so `PackInstances` must inflate the instance's finite bound by that reach or the beam masks the tiles the grown shell reaches and the surface HOLES at the tile seams — `MaxScopedFieldReach` folds it in the same way `MaxSmoothBlendRadius` folds the POP compose halo (per-op: `Onion(t)` outer surface moves out by `t`, `Dilate(r)` by `r`, `Displace(a)` by `a`; field ops SUM within a scope, max across scopes; an UNscoped field op stays unmaskable, so its 1e30 sentinel covers it and no margin is computed). Verified 2026-07-08 by a scoped-`Dilate(1.5)` sphere with a bound covering only the un-dilated radius: pre-fix the beam clipped the shell into a blocky tile-truncated blob, post-fix the full dilated sphere renders intact. `AnalyzeLipschitz` folds a conservative √2 (`hasChamferPop`) for a chamfer compose ("expect the next bug here"). Op-unused (scope-free) programs stay BYTE-IDENTICAL (verified: overworld render sha256-identical). Post: `world-scope` (scoped intersection renders as the intersection of its own members; a scoped instance is maskable with `instanced == flat`) + `sdf-blend-endpoints` (pins `blendSmoothUnion`'s FAR + NEAR endpoints — the scope-seed prerequisite) |


> **Wallpaper groups (verified by direct point-group measurement).** `sdfWallpaperFoldCell` realizes 16 of the 17 IUC
> groups. `SDF_WPG_CMM`'s half-turn must run AFTER the sign pair (before it, the pattern is `pmg`). `SDF_WPG_P6` is a C6
> sector fold about the hex centre, NOT P3's 3-colouring turn plus a half-turn (k(-h) = -k(h) kills the central inversion,
> collapsing it to `p3`). `SDF_WPG_P4G` STILL renders as `p4` (0 mirror classes) — a known defect, marked in the shader.
> For every parity-keyed group (P2/PG/CM/PMG/PGG/CMM/P4/P4M/P4G) and for P3, the authored `cell` is the HALF-period: the
> pattern's translation lattice is the centered/doubled cell (or the √3×√3 hex supercell for P3).


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
- Dynamic-slot bound: `SdfProgram.MaxDynamicTransformSlot` = int.MaxValue−1
  (`slot+1` must fit); the float-lane decode compares in DOUBLE because
  `(float)int.MaxValue` rounds up to 2³¹.

## Render assembly (Puck.Demo)

`SdfWorldRenderSpec` + `SdfWorldRenderBuilder.Build` own EVERY backend-
specific choice from one `HostsOnDirectX` field: kernel bytecode extension
(`.spv`/`.dxil`), child `directX` flags, and decorator availability (the
binding-bar decorator is Vulkan-only — SKIPPED with a stderr notice on D3D12,
never silently bound). A caller never names a bytecode extension.
`GraphBuilder.UnsupportedReason` is the ONE owner of the world graph's
deferred/retired rejections (cross-backend `produce`, the retired `child`
bool, `live-camera` pending its child node) — pre-flighted in `Program`
BEFORE the window host builds, so rejection is an attributed stderr line and
exit 2, never a mid-host crash.

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
`["mask", "beam", "views", "composite"]` (`SdfWorldEngine.PassLabels`); the
bench's beam column reports beam+mask so ladders stay comparable. Gated by
`world-grid-cull` (grid==flat bit-identical via the destructible-slab scene)
plus the existing instanced==flat stages.

## Gotchas (verified, expensive to re-learn)

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
- Per-pass GPU-ms: `PUCK_TIMING=1`. Delayed captures: `PUCK_CAPTURE_FRAME=N`.

## Verifying

The POST battery is the gate (routing lives in the `verifying-puck-changes`
skill): `dotnet run --project src/Puck.Post -c Release`; the world-path
stages exercise every kernel. Live checks: `--run docs/examples/world-*.json`
with `--capture`. The battery's own output is the source of truth for which
stage covers what — do not pin stage names in comments or docs.
