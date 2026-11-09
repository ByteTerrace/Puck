# Adversarial Design Review: `docs/plans/author-shaders.md`

Reviewed [`docs/plans/author-shaders.md`](../../../docs/plans/author-shaders.md) and its companion research notes in [`docs/plans/author-shaders/`](../../../docs/plans/author-shaders/README.md) against [`AGENTS.md`](../../../AGENTS.md), [`.claude/skills/rendering/SKILL.md`](../../../.claude/skills/rendering/SKILL.md), and the live `Puck.SignedDistance`, `Puck.SdfVm.Model`, `Puck.Shaders.Model`, and `Puck.SdfVm` implementation.

The concerns below require a fresh source review before implementation. Unverified
and elided excerpts have been removed; surviving quotations are mechanically
checked, which does not establish the surrounding claims.

---

## Executive Answers to Review Questions

1. **Does each invariant have a real door or rest on convention?**
   - **Door 1 (`SdfAppearance` admission)** is real on the CPU side ([`SdfAppearance.cs:204-213`](../../../src/Puck.SignedDistance/SdfAppearance.cs#L204-L213)), **but incomplete on the GPU side**: `sdfProgramRange` ([`sdf-program.hlsli:49-65`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-program.hlsli#L49-L65)) only bounds the instruction word slice `(start, count)` in `sdfWords`; it does not bound the decoded source/destination register indices (`dst`, `src0`, `src1`, `src2`) inside each op word unless register indices are bit-field-masked to `< MaxRegisters` (`& ( SDF_SHADE_MAX_REGISTERS - 1u )`) in the generated decoder (Finding 8).
   - **Door 2 (Single `SdfShadeOp` generator for Tier 1 and Tier 2)** enforces that both tiers emit from one C# table, **but does not prevent runtime disagreement** (Finding 1), **and** placing it in [`SdfIsaHlsl`](../../../src/Puck.SdfVm.Model/SdfIsaHlsl.cs#L530-L564) without separating `DescribeFingerprint` from `DescribeFieldFingerprint` will invalidate the baked-mesh store on every shade op change (Finding 8).
   - **Door 3 (`SdfShadeDispatch` per-dispatch work bound)** **fails as designed**: in §3.5, per-bin pixel counts live only in GPU memory (`views-filter` wave-reduced atomics) and drive `DispatchIndirect`, while in §3.7 `SdfShadeDispatch` chunks dispatches on the CPU by `pixelCount * staticCost <= MaxWork` without knowing `pixelCount` on the CPU (Finding 6).
2. **Can the interpreter and a specialized kernel ever disagree?**
   - **Yes (Finding 1).** Even when generated from the exact same per-op C# expression template, Tier 1 evaluates one op per `[loop]` iteration into a register array while Tier 2 unrolls the stream into straight-line HLSL locals compiled under `-O3` ([`ShaderCompiler.Identity.cs:48-57`](../../../src/Puck.Shaders.Model/Compiler/ShaderCompiler.Identity.cs#L48-L57)). Across op boundaries in straight-line code, DXC `-O3` performs FMA contraction (`mad`), algebraic reassociation, and compile-time constant folding (which also differs between DXIL and SPIR-V, as documented in [`sdf-shapes.hlsli:472-474`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli#L472-L474) and [`sdf-program.hlsli:243-247`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-program.hlsli#L243-L247)). When a program feeds that result into a discontinuous or steep ramp (`step(a, b)` in §3.2 or narrow `smoothstep` in `weathered`/`cel`), a 1-ULP difference flips pixel output from `0.0` to `1.0`, violating still tolerance when Tier 2 swaps in.
3. **Is the static per-pixel cost sound and the work bound truly hard?**
   - **No (Finding 6).** First, `shape` (`evaluateShape`, [`sdf-shapes.hlsli:729-751`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli#L729-L751)) is not a single-cost op: `SDF_SHAPE_SPHERE` is a `length()` while `SDF_SHAPE_PATH` loops up to `SDF_MAX_PATH_EDGES` (`64`, line 532) and `SDF_SHAPE_SWEEP` loops `4 * (17 + 6) = 92` Bezier/trig steps (lines 620–694)—an ~100× cost spread within one op. Second, the work bound is not hard on GPU-binned indirect dispatches because CPU command recording does not have the GPU bin counts, and consecutive dispatches inside a single `ExecuteCommandLists` / `vkQueueSubmit` submission do not reset OS WDDM TDR or Linux `amdgpu` per-submission timeouts unless the driver preempts between dispatches or chunks split submissions.
4. **Does binning keep authored code out of the views pass's registers and what does it cost frames with no authored program?**
   - **No in Tier 1 (Finding 1), and binning introduces a data race + mandatory bandwidth overhead on every frame (Finding 5).** In Tier 1, indexing `float4 regs[MaxRegisters]` by a runtime op word inside a rolled `[loop]` violates Puck's own kernel law ([`.claude/skills/rendering/SKILL.md:729-734`](../../../.claude/skills/rendering/SKILL.md), [`shade-layers.hlsli:59-61`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-layers.hlsli#L59-L61)): NVIDIA's Vulkan compiler spills dynamically indexed local arrays to per-thread local memory. And because §3.9 migrates `paint`, `inset`, `weathered`, `flow`, and `cel` to `.puck` programs in `worlds/standard/`, no normal world has "no authored program"; even a 1-bin frame pays for reading the 32-byte visibility record twice (`views-filter` and `views-shade`), writing and reading back filtered AO/shadows + pixel indices in global memory, atomic counting + prefix-sum + scatter passes, and losing 2D 8×8 tile locality for `sdfMeshProbesAt`. Worse, overwriting visibility record words 6 and 7 in place during `views-filter` races with adjacent workgroups reading their 2-pixel apron in `sdfViewsGatherFilter` ([`sdf-views-filter.hlsli:40-58`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-filter.hlsli#L40-L58)).
5. **Is pipeline state enumerable from the document?**
   - **The program hashes are enumerable from the compiled document, but the engine's kernel/pipeline infrastructure cannot compile or swap variable-count per-program kernels without a new subsystem (Finding 7).** [`SdfKernelSet`](../../../src/Puck.SdfVm/SdfKernelSet.cs#L26-L37) requires a fixed `Enum.GetValues<SdfKernel>()` array; [`SdfWorldResidency.ShaderReload.cs:111-118`](../../../src/Puck.SdfVm/SdfWorldResidency.ShaderReload.cs#L111-L118) cancels any in-flight reload when a new reload arrives; [`SdfWorldTables.InstallReload`](../../../src/Puck.SdfVm/SdfWorldTables.Pipelines.cs#L112-L115) calls `m_deviceContext.TryWaitIdle()` (stalling the GPU on every background batch landing); and [`ShaderCompiler.Retention`](../../../src/Puck.Shaders.Model/Compiler/ShaderCompiler.cs#L37-L43) caps the per-user cache at `MaxEntries: 4096`.
6. **Do the steps measure before committing?**
   - **No (Finding 10).** Step 1 deletes `applyWeathering` before Step 2 measures Tier 1; Step 2 measures Tier 1 on `weathered` alone while the other 4 fixed-function layers (`paint`, `inset`, `flow`, `cel`) remain compiled in `sdf-light-stage.hlsli`, biasing both the register count and runtime delta; and Step 5 adds `SdfShadeDispatch` (`MaxWork`) *after* Step 4 may have declined binning.
7. **What did the memo miss that would cost a rewrite later?**
   - **Glass pass flow & contact reveal** ([`sdf-glass-stage.hlsli:260-388`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-glass-stage.hlsli#L260-L388)), **static-copy object-space flow velocity transform** ([`shade-flow.hlsli:95-101`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-flow.hlsli#L95-L101)), and **analytic gradient noise `sdfLatticeNoiseGradient3`** ([`sdf-noise.hlsli:160-188`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-noise.hlsli#L160-L188)) (Finding 2).
   - **Cel shading (`SdfToon`) is per-light + post-light**, not a pre-light surface layer, plus `row 25.w` holds glass `ShadowDensity` and `passGroup` holds live `world.toon` (Finding 3).
   - **`wrap` and `normal` outputs in `views` break `sdfSurfaceStage`'s shadow-cascade unqueried gate (`SDF_SHADOW_UNQUERIED = 255`)**, leaking 100 klx sunlight indoors (Finding 4).
   - **GI probe relight (`SdfMeshIrradiance.Shade` / `sdfProbeShade`)** reads only scalar base material fields (`Albedo`, `Metal`, `Bleed`, `Wrap`, `Emissive`), so procedural materials whose base color or emission lives in `shade` bounce black or wrong in indirect lighting (Finding 9).

---

## Detailed Findings

### Finding 1 — Tier 1 register-file indexing causes local-memory spills on NVIDIA Vulkan and Tier 1 / Tier 2 numerical disagreement

- **Severity:** Blocker
- **Memo section:** `§3.3` ([`docs/plans/author-shaders.md:471-473`](../../../docs/plans/author-shaders.md)), `§3.4` ([`docs/plans/author-shaders.md:503-526`](../../../docs/plans/author-shaders.md)), `§4` Door 2 ([`docs/plans/author-shaders.md:679-681`](../../../docs/plans/author-shaders.md))
- **Code citations:**
  - [`.claude/skills/rendering/SKILL.md:839`](../../../.claude/skills/rendering/SKILL.md):
    ```markdown
    - **Index local arrays by constants only.** NVIDIA's Vulkan compiler keeps a dynamically indexed local array, a `static const` table included, in per-thread local memory. Unroll the loop to the array's length and guard the body (`if (i < count)`), or pick the element by comparison; DXC's SPIR-V unroller leaves a loop with a `break` rolled. RenderDoc's pipeline-executable statistics show the spill as "Local Memory Size" (the low 32 bits).
    ```
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-layers.hlsli:59-61`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-layers.hlsli#L59-L61):
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli:472-474`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli#L472-L474):
  - [`src/Puck.Shaders.Model/Compiler/ShaderCompiler.Identity.cs:48-57`](../../../src/Puck.Shaders.Model/Compiler/ShaderCompiler.Identity.cs#L48-L57):
- **Failing case:**
  1. **Per-thread local memory spill in Tier 1 on NVIDIA Vulkan:** Memo §3.3 and §3.4 specify that a lowered program executes over a `float4` register file of static size `MaxRegisters` inside a `[loop]` interpreter (`sdfEvaluateShadeProgram`), where each op word in `sdfWords` names its source and destination register indices at runtime. Indexing `float4 regs[MaxRegisters]` by a runtime `uint` decoded from `sdfWords` inside a rolled `[loop]` is a dynamically indexed local array. On NVIDIA's Vulkan driver, this places `regs[MaxRegisters]` in per-thread local memory (L1/VRAM spill) on every op read and write, reporting a deceptive low register count while cratering occupancy/latency via local-memory traffic.
  2. **Tier 1 vs Tier 2 disagreement across thresholds:** Memo §3.4 claims: *"Both tiers are generated from the same op table in `SdfIsaHlsl`, so they cannot drift"* and *"when the specialized kernel swaps in, the rendered image does not change (within the still tolerance)."* Generating both tiers from one C# op table only guarantees syntactic equivalence per op. In Tier 1, each op's result is stored to a register slot at the end of a `[loop]` body before the next iteration switches on the next opcode, preventing cross-op instruction fusion or constant folding. In Tier 2 (*"the op stream unrolled into straight-line code, constants folded, registers as locals"*, line 505), DXC under `-O3` fuses `mul` + `add` across adjacent ops into `mad`/FMA, reassociates expressions, and constant-folds literals (and DXIL vs SPIR-V fold built-ins like `normalize` differently). When those values feed `step(a, b)` (§3.2, line 433) or a narrow `smoothstep(a, b, x)` (`weathered` threshold or `cel` band), a 1-ULP difference between Tier 1 and Tier 2 flips the output between `0.0` and `1.0`, creating a visible pop when Tier 2 finishes compiling in the background.
- **Fix:**
  1. Keep `MaxRegisters` small (e.g., 4–6 `float4` accumulators, or see **Proposal 1** below) and in Tier 1 access registers via generated constant-index select/switch helpers (just as [`sdf-lanes.hlsli:11-19`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-lanes.hlsli#L11-L19) selects components by comparison rather than dynamic array indexing), and verify `Local Memory Size == 0` in RenderDoc/Nsight on Vulkan and D3D12.
  2. Perform all constant folding and canonical expression lowering in C# **before** emitting `SdfShadeProgram` (so Tier 1 and Tier 2 see identical pre-folded constants), emit `[precise]` on intermediate locals in the generated per-op template (or emit compound ops like `mad` explicitly in the ISA so FMA boundaries match), and remove discontinuous `step(a, b)` from the surface language in favor of AA-clamped `smoothstep` over pixel footprint `surface.footprint`.

---

### Finding 2 — Deleting `SdfFlow`, rows 27–30 of `SdfAppearance.Pack`, and `shade-flow.hlsli` in Step 6 breaks the glass pass (`sdf-glass.comp.hlsl`), drops static-copy object-space velocity transforms

- **Severity:** Blocker
- **Memo section:** `§3.1` ([`docs/plans/author-shaders.md:359-388`](../../../docs/plans/author-shaders.md)), `§3.2` ([`docs/plans/author-shaders.md:416-439`](../../../docs/plans/author-shaders.md)), `§3.9` ([`docs/plans/author-shaders.md:634-648`](../../../docs/plans/author-shaders.md)), `§5` Step 6 ([`docs/plans/author-shaders.md:766-770`](../../../docs/plans/author-shaders.md))
- **Code citations:**
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-flow.hlsli:1-3`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-flow.hlsli#L1-L3):
    ```hlsl
    // The flow layer (SdfFlow) and the layer frame it reads, shared by every pass that shades a material: the views pass's
    // light stage and the glass pass. One implementation, so an opaque surface and a glass surface wearing one flow move
    // alike.
    ```
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-glass-stage.hlsli:260-274`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-glass-stage.hlsli#L260-L274), [`298`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-glass-stage.hlsli#L298), [`320`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-glass-stage.hlsli#L320), [`385-388`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-glass-stage.hlsli#L385-L388):
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-noise.hlsli:160-188`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-noise.hlsli#L160-L188):
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-flow.hlsli:95-101`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-flow.hlsli#L95-L101):
- **Failing case:**
  1. **Glass pass (`sdf-glass.comp.hlsl`) breaks when `SdfFlow` and rows 27–30 are deleted:** Memo §3.1 states that `shade` runs only in `views` (`sdfLightStage`) and *"does not run in ... the glass raster"*, overlooking `sdf-glass.comp.hlsl` (`glass-shade` compute pass), which calls `sdfShadeFlow` on up to two glass interfaces (`near` and `far`) per pixel, passes `sdfGlassContact(material, (opaque - t))` as the `contact` argument, restores `material.roughness = roughness` prior to computing glass frost while keeping the flow-revealed `material.metal` and `material.roughness` for the foam/reveal lobe, and reads `material.flowColor.rgb` (row 29) for `cover`! Deleting `SdfFlow`, rows 27–30, and `shade-flow.hlsli` in Step 6 breaks glass water/flow and glass contact foam (`SdfGlass.HasContact` in [`SdfMaterial.cs:71-73`](../../../src/Puck.SignedDistance/SdfMaterial.cs#L71-L73)).
  2. **Static-copy object-frame flow velocity has no stamp input or op:** When a static mesh draw (`frameSlot == SDF_TRANSFORM_SLOT_NONE`) wears an object-space flow (`!worldFlow && !slotFlow`), `shade-flow.hlsli:98-101` rotates `flowVelocity` by the draw's mesh transform and carry matrix (`sdfMeshCarryDirection(flowRecord, sdfMeshWorldDirection(flowRecord, flowVelocity))`). Memo §3.2's `surface` stamp exposes only `frameSlot` and its `layerRotation` (which is identity `(0, 0, 0, 1)` when `frameSlot == SDF_TRANSFORM_SLOT_NONE`, [`shade-flow.hlsli:19-20`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-flow.hlsli#L19-L20)), so a migrated `flow` program on a rotated static copy flows in the wrong direction.
- **Fix:**
  1. Either run the Tier 1 interpreter in `sdf-glass.comp.hlsl` (passing `contact = sdfGlassContact(material, (opaque - t))` in the `surface` stamp and outputting `reveal` + revealed surface properties separately from the glass substrate's `roughness` and `albedo` tint), or keep `SdfFlow` / `sdfShadeFlow` until the glass pass is wired to evaluate `shade` programs.
  2. Pre-transform `layerRotation` in `sdfLayerFrame` ([`shade-flow.hlsli:13-29`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-flow.hlsli#L13-L29)) or expose `surface.objectDirection(v)` in the stamp so static mesh draws (`s.meshDraw`) and dynamic slots (`frameSlot`) use one unified rotation in `shade`.

---

### Finding 3 — `cel` (`SdfToon`) is a per-light and post-light stage, not a pre-light surface layer; deleting rows 22–25 in Step 6 breaks `cel`, live `world.toon`, `sdfToonOutline`, and glass `ShadowDensity` (`row 25.w`)

- **Severity:** Blocker
- **Memo section:** `§3.1` ([`docs/plans/author-shaders.md:359-374`](../../../docs/plans/author-shaders.md)), `§3.9` ([`docs/plans/author-shaders.md:634-648`](../../../docs/plans/author-shaders.md)), `§5` Step 6 & Step 7 ([`docs/plans/author-shaders.md:766-779`](../../../docs/plans/author-shaders.md)), `§6` ([`docs/plans/author-shaders.md:813-819`](../../../docs/plans/author-shaders.md))
- **Code citations:**
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-light-stage.hlsli:168-272`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-light-stage.hlsli#L168-L272):
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-light.hlsli:247-270`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-light.hlsli#L247-L270):
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-toon.hlsli:19-30`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-toon.hlsli#L19-L30):
  - [`src/Puck.SignedDistance/SdfAppearance.cs:364-391`](../../../src/Puck.SignedDistance/SdfAppearance.cs#L364-L391) and [`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-material.hlsli:100-103`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-material.hlsli#L100-L103):
- **Failing case:**
  1. **Step 6 cannot migrate `cel(...)` over the surface hook (`shade`):** Memo §3.1 defines Hook 1 (`shade`) as running *before* the light loop and writing only `SdfMaterialData` surface properties, while Hook 2 (`light`, Step 7) is optional and gated on an open owner question (§6, line 813: *"The surface hook does not need the decision"*). Yet §3.9 and Step 6 delete `SdfToon`'s shading half, rows 22–25 of `SdfAppearance.Pack`, and `sdf-toon.hlsli`'s banding in **Step 6**! A pre-light surface hook cannot quantize per-light `NdotL * shadow` (`sdfToonBand`), replace per-light GGX with `sdfToonSpecular`, tint ambient/indirect radiance by `style.shadow.rgb`, bypass `(1.0 - metal)` diffuse attenuation, or apply post-exposure shoulder compression (`sdfToonExpose`).
  2. **Deleting rows 22–25 breaks `sdfToonOutline` and glass `ShadowDensity`:** Row 24 (`toonOutline`, carrying outline width, depth threshold, normal threshold, and shoulder `outline.w`), row 25.rgb (`toonInk`), and row 25.w (`glass.ShadowDensity`, read by `sdfMaterialShadowDensity` in shadow cascade optical depth) all live in rows 22–25.
  3. **Live `world.toon` console override:** `sdfToonStyleOf` falls back to `passGroup.toonBands` when `m.toonBands.x == 0.0`, allowing `world.toon` to toggle cel shading across the whole world at runtime without recompiling materials. Lowering `cel` only into materials that statically author `toon` breaks `world.toon` on unauthored materials.
- **Fix:**
  - Keep `cel` (`SdfToon`, rows 22–25, and `sdf-toon.hlsli`) out of Step 6's surface-layer migration; only `paint`, `inset`, `weathered`, and (once glass is wired) `flow` are pre-light surface layers. If Hook 2 (`light`, Step 7) is approved later, keep row 24 (`toonOutline`), row 25.rgb (`toonInk`), and row 25.w (`glass.ShadowDensity`) in `SdfAppearance.Pack`, and expose `passGroup.toon*` in the `light` stamp so the default `light` program honours `world.toon`.

---

### Finding 4 — Allowing `shade` to output `wrap` (and arbitrary `normal` tilts) in `views` breaks `sdfSurfaceStage`'s shadow-cascade gate (`SDF_SHADOW_UNQUERIED = 255`), leaking 100 klx sun indoors

- **Severity:** High
- **Memo section:** `§3.1` ([`docs/plans/author-shaders.md:361-363`](../../../docs/plans/author-shaders.md)), `§3.2` ([`docs/plans/author-shaders.md:425-427`](../../../docs/plans/author-shaders.md))
- **Code citations:**
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-surface-stage.hlsli:354-361`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-surface-stage.hlsli#L354-L361):
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-filter.hlsli:72-73`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-filter.hlsli#L72-L73), [`93`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-filter.hlsli#L93):
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-light.hlsli:233-254`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-light.hlsli#L233-L254):
  - [`.claude/skills/rendering/SKILL.md:333-339`](../../../.claude/skills/rendering/SKILL.md):
- **Failing case:**
  - Today, none of `paint`, `inset`, `weathered`, or `flow` can modify `material.wrap`, so `sdfSurfaceStage` (`key > -sdfMaterialWrap(material)`) and `sdfLightResponse` (`rawNdotL + s.material.wrap > 0.0`) are guaranteed to use the same `wrap` scalar from row `2.y`.
  - Memo §3.1 (line 362) and §3.2 (line 425) make `wrap` a writable output of a `shade` program in `views`. If a material's base `wrap` is `0.0` (so `sdfSurfaceStage` leaves every pixel with `dot(hitNormal, lightDir) <= 0.0` as `SDF_SHADOW_UNQUERIED = 255`), and its `shade` program outputs `wrap: 0.5` in `views`, then for every shadowed pixel with `-0.5 < dot(hitNormal, lightDir) <= 0.0` where all 4×4 neighbors also had `key <= 0.0`, `sdfViewsFilter` returns `result[s] = 1.0` (`SDF_SHADOW_UNQUERIED` fallback) and `sdfLightResponse` shades it with `NdotL > 0` and `shadow = 1.0`. The same leak occurs when `shade` tilts `normal` across the terminator.
- **Fix:**
  - Either keep `wrap` as a static material-level property on `SdfMaterial` (read by both `sdfSurfaceStage` and `sdfLightResponse`, never increased by `shade`), or clamp `shade`'s output `wrap` to `<= baseMaterial.wrap` (`min(outWrap, baseWrap)`), and in `sdfViewsFilter` ([`sdf-views-filter.hlsli:93`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-filter.hlsli#L93)) return `0.0` (shadowed) rather than `1.0` when `raw[s] == SDF_SHADOW_UNQUERIED` and `weight == 0.0`.

---

### Finding 5 — `views-filter` writing filtered occlusion and shadows into the visibility record causes a cross-group apron data race, doubles visibility bandwidth on every frame, loses 2D tile locality, and omits `viewIndex` (`id.z`)

- **Severity:** High
- **Memo section:** `§3.5` ([`docs/plans/author-shaders.md:547-567`](../../../docs/plans/author-shaders.md))
- **Code citations:**
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-filter.hlsli:35-65`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-filter.hlsli#L35-L65):
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-visibility.hlsli:6-24`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-visibility.hlsli#L6-L24):
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-pixel-entry.hlsli:8-15`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-pixel-entry.hlsli#L8-L15):
- **Failing case:**
  1. **Cross-workgroup read-after-write / write-after-read data race in `views-filter`:** Memo §3.5 says `views-filter` performs *"the group gather, the barrier, `sdfViewsFilter`, and then, new, a write of the filtered occlusion and the four shadow visibilities into the visibility record (two words)"*. In `sdfViewsGatherFilter`, every 8×8 workgroup reads a **2-pixel apron** (`-2..+1`) around its tile from `SdfVisibilityShadingWord` (word 6) and `SdfVisibilityShadowWord` (word 7) of neighboring workgroups' pixels. If `views-filter` overwrites words 6 and 7 in `sdfVisibilityRecordsRW` after the group barrier, an adjacent workgroup executing later (or concurrently on another SM) reads those already-overwritten words in its apron—corrupting both the bilateral average and the `SDF_SHADOW_UNQUERIED` (`255`) sentinel! If instead `views-filter` writes to two *new* words in the visibility record, the record grows from 8 words (32 B) to 10 words (40 B, +25% memory) and still requires binding `sdfVisibilityRecordsRW` (UAV) in `views-filter` followed by a UAV-to-SRV transition barrier before `views-shade`, `resolve`, and `composite`.
  2. **Mandatory bandwidth and locality cost on frames with 0 or 1 authored program:** Because §3.9 moves all standard layers into authored `.puck` programs, every world uses this path. Even when a frame has only 1 active program bin, splitting `views` into `views-filter` + prefix sum + scatter + `views-shade` forces `views-filter` to load the visibility record (`t`, `normal`, `material`, `ambient`, `shadows`) and write filtered output + pixel indices to global memory, and then forces `views-shade` to load all 8 visibility words *a second time* via `sdfLoadSurfaceSample` ([`sdf-views-stage.hlsli:36`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-stage.hlsli#L36)). Moreover, compacting pixels linearly across the frame destroys 8×8 2D tile locality in `views-shade`, degrading cache hit rates on the 3D irradiance probe volume (`sdfMeshProbesAt`), cascade/environment textures, and screen textures.
  3. **Multi-viewport `id.z` omission:** `CSMain` in `sdf-pixel-entry.hlsli:10` indexes viewports via `uint viewIndex = worldViewOf(id.z)`. A flat pixel-index list must encode `(pixel, viewIndex)` (or bin per viewport).
- **Fix:**
  - Never overwrite words 6 and 7 of `sdfVisibilityRecords` in place; if `views` is ever split, write the filtered `(ao, shadowVis)` into a separate compact scratch buffer (`uint2` or `uint` per pixel: 8-bit AO + four 8-bit shadow visibilities = 5 bytes / `uint2`) so `sdfVisibilityRecords` stays a 32-byte read-only SRV after `surface` with zero data race.
  - Better yet, avoid splitting `views` when the scene has only 1 active program or when a single uber-specialized `views` kernel fits the register budget (see **Proposal 1**).
  - If binning is used, sort/scatter **8×8 tiles** (or Morton-ordered 2D subtiles) rather than scanline-linear pixel indices so `views-shade` preserves 2D spatial cache locality for `sdfMeshProbesAt`.

---

### Finding 6 — `SdfShadeDispatch`'s CPU-side work bound contradicts GPU-side indirect binning, does not split OS queue timeouts inside one submission, and prices `evaluateShape` as a single fixed cost despite a ~100× spread

- **Severity:** High
- **Memo section:** `§3.3` ([`docs/plans/author-shaders.md:483-489`](../../../docs/plans/author-shaders.md)), `§3.5` ([`docs/plans/author-shaders.md:547-559`](../../../docs/plans/author-shaders.md)), `§3.7` ([`docs/plans/author-shaders.md:590-601`](../../../docs/plans/author-shaders.md)), `§4` Door 3 ([`docs/plans/author-shaders.md:682-684`](../../../docs/plans/author-shaders.md))
- **Code citations:**
  - `SdfWorldPassRecorder.RecordCompute` originally dispatched the full extent directly.
    Its authored views path now records through `SdfShadeDispatch.Record`; the other
    compute passes retain their direct dispatch.
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli:495-498`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli#L495-L498), [`531-534`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli#L531-L534), [`620-622`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli#L620-L622), [`729-751`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli#L729-L751):
- **Failing case:**
  1. **CPU chunking (`SdfShadeDispatch`) vs GPU indirect bin counts:** Memo §3.5 computes per-program pixel counts on the GPU in `views-filter` and dispatches each bin indirectly (`views-shade`, *"one indirect dispatch per bin"*). Memo §3.7 and §4 Door 3 state that `SdfShadeDispatch` on the CPU enforces `pixelCount * staticCost <= MaxWork` by issuing *"consecutive dispatches of at most the bound each, over consecutive ranges of its pixel list"*. When `SdfWorldPassRecorder.RecordCompute` records the command buffer on the CPU, **the GPU has not run `views-filter` yet**, so the CPU does not know any bin's pixel count! Without a CPU-stalling readback, `SdfShadeDispatch` must either budget every bin for the full render extent `width * height` (emitting `ceil((width * height * staticCost) / MaxWork)` indirect dispatches per bin, most of which launch `(0, 1, 1)` groups) or the memo's Door 3 cannot be implemented as stated.
  2. **Consecutive dispatches in one command list do not reset TDR / queue timeouts:** Even when a pass is split into consecutive `Dispatch` / `DispatchIndirect` calls inside the same `IGpuCommandBuffer` submission, Windows WDDM TDR (2 s per driver preemption quantum / command buffer submission) and Linux `amdgpu` ring timeouts measure the **command submission**, not individual `vkCmdDispatch` calls, unless the GPU hardware preempts at dispatch boundaries or chunks are submitted across separate queue submissions.
  3. **`shape` (`evaluateShape`) static cost is not a single constant:** Memo §3.3 (line 484) assigns each `SdfShadeOp` a fixed non-negative integer cost (`Cost(op)`). The `shape` op calls `evaluateShape` ([`sdf-shapes.hlsli:729`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli#L729)), which branches on `shapeType` across 19 primitives: `SDF_SHAPE_SPHERE` is `length(p) - r`, whereas `SDF_SHAPE_PATH` runs up to 64 edge projections (`sdf-shapes.hlsli:531-569`) and `SDF_SHAPE_SWEEP` runs up to 4 strands × (17 coarse samples + 6 Newton rounds) = 92 cubic Bezier + trig evaluations (`sdf-shapes.hlsli:620-694`). Pricing `shape` as one constant either underprices `SDF_SHAPE_SWEEP` by ~100× (defeating `MaxCost` and `MaxWork`) or overprices `SDF_SHAPE_SPHERE`/`SDF_SHAPE_BOX` by ~100× (refusing ordinary `paint` stacks). Also, `sdfApplyPaints` ([`shade-layers.hlsli:34`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-layers.hlsli#L34)) skips `evaluateShape` outside the paint's bounding box (`if (any(abs(local - slotCenter.yzw) > ...)) continue;`), which an unconditional `shape` op loses unless the AABB guard is preserved.
- **Fix:**
  1. Tighten `MaxCost` on `SdfShadeProgram` so that `width * height * MaxCost` at the render ceiling (e.g. 4K) is already well below the frame bar (~16.6 ms, i.e., `<= 250 ms` worst-case on the slowest bar device, 8× below the 2 s TDR). When `width * height * MaxCost` cannot approach TDR in a single full-screen pass, per-dispatch chunking in `SdfShadeDispatch` is only needed for offline/diagnostic renders or must chunk by 2D viewport tiles on the CPU (`totalPixelsInTile * programStaticCost <= MaxWork`).
  2. Parameterize `Cost(shape)` by the statically known `SdfShapeType` and its vertex/edge/strand count at lowering time (e.g., `Sphere/Box` = 1, `ConvexPolygon(N)` = `N`, `Path(E)` = `2 * E`, `Sweep(S)` = `24 * S`), and include the AABB reject box in the `shape` op (or a `paintShape` op).

---

### Finding 7 — `SdfKernelSet`, `SdfWorldPipelines`, `SdfWorldResidency.ShaderReload`, and `ShaderCompiler` cannot compile or hot-swap dynamic per-program Tier 2 kernels without a new pipeline table

- **Severity:** High
- **Memo section:** `§3.4` ([`docs/plans/author-shaders.md:507-527`](../../../docs/plans/author-shaders.md)), `§3.6` ([`docs/plans/author-shaders.md:569-586`](../../../docs/plans/author-shaders.md)), `§3.8` ([`docs/plans/author-shaders.md:627-632`](../../../docs/plans/author-shaders.md))
- **Code citations:**
  - `SdfKernelSet`'s constructor, as reviewed, took one bytecode per kernel and keyed
    the set by that bytecode alone; tier 2 has since added each kernel's source
    (`SdfKernelSource`) to the set and its identity, so the quotation the review
    pasted no longer matches the file.
  - [`src/Puck.SdfVm/SdfWorldResidency.ShaderReload.cs:111-118`](../../../src/Puck.SdfVm/SdfWorldResidency.ShaderReload.cs#L111-L118), [`236-247`](../../../src/Puck.SdfVm/SdfWorldResidency.ShaderReload.cs#L236-L247):
  - [`src/Puck.SdfVm/SdfWorldTables.Pipelines.cs:112-115`](../../../src/Puck.SdfVm/SdfWorldTables.Pipelines.cs#L112-L115):
    ```csharp
    // Every view's submission in flight records with the pipelines about to be replaced.
    m_deviceContext.TryWaitIdle();
    m_pipelines.Exchange(reload: reload);
    m_pipelines.Commit(reload: reload);
    ```
  - [`src/Puck.Shaders.Model/Compiler/ShaderCompiler.cs:37-43`](../../../src/Puck.Shaders.Model/Compiler/ShaderCompiler.cs#L37-L43):
- **Failing case:**
  - Memo §3.4 states: *"The compilation and swap-in machinery is already in the engine: `SdfKernelTreeLoad` compiles sources shortest-recorded-duration first on the thread pool through `ShaderCompiler` ... `SdfWorldPipelines.PrepareReload` builds the `IGpuComputePipeline` off the frame thread, and `SdfWorldTables.InstallReload` swaps it in at a frame boundary (`SdfWorldResidency.ShaderReload.cs`). Tier 2 uses that exact path."*
  - In reality, `SdfKernelTreeLoad` ([`SdfKernelTreeLoad.cs:29-37`](../../../src/Puck.SdfVm/SdfKernelTreeLoad.cs#L29-L37)), `SdfKernelSet` ([`SdfKernelSet.cs:26-37`](../../../src/Puck.SdfVm/SdfKernelSet.cs#L26-L37)), `SdfWorldPipelines` ([`SdfWorldPipelines.cs:196-205`](../../../src/Puck.SdfVm/SdfWorldPipelines.cs#L196-L205)), and `SdfWorldResidency.ShaderReload` are hardwired to the 21-element `SdfKernel` enum:
    1. `SdfKernelTreeLoad` only iterates `SdfKernelSet.Kernels` looking for `{StemOf(kernel)}.comp.hlsl` files on disk in `passes/`.
    2. `SdfWorldResidency.RequestShaderReload` holds a single `m_reloadLoad` / `m_reloadBuild` slot and **cancels** any in-flight reload when a new request arrives (`SdfWorldResidency.ShaderReload.cs:112-117`), so an operator running `world.shaders.reload` and background Tier 2 specialization would cancel each other.
    3. `SdfWorldTables.InstallReload` calls `m_deviceContext.TryWaitIdle()` on the frame thread (`SdfWorldTables.Pipelines.cs:113`) every time a batch of ready kernels installs! Installing newly compiled Tier 2 shade kernels through `InstallReload` would stall the render thread for a full GPU drain (`TryWaitIdle`) on each completing batch, even though a newly created `IGpuComputePipeline` replaces no in-flight pipeline (in-flight frames used the Tier 1 interpreter pipeline, which stays alive!).
    4. `ShaderCompiler.Retention` (`ShaderCompiler.cs:40`) caps the cache at `4,096` entries across the entire user machine; if an author sweeps parameters during live editing, generated Tier 2 HLSL variants will churn the 4,096-entry cache unless Tier 2 kernels either parameterize palette constants via `sdfWords` (keying the Tier 2 kernel on the *structural op stream* rather than literal float constants) or use a dedicated retention policy.
- **Fix:**
  1. Key Tier 2 specialized kernels by the **canonical op-stream structure** (reading constants/parameters from the material's `sdfWords` slice rather than baking float literals into the HLSL source), so editing a color, roughness, or scale in `.puck` is a zero-compile parameter upload (`< 1 ms`) rather than a new DXC + PSO compile!
  2. Manage Tier 2 compute pipelines in a dedicated `SdfShadePipelineTable` keyed by `SdfShadeStructureHash` (separate from `SdfKernelSet` and `m_reloadLoad`), and install newly built `IGpuComputePipeline` handles atomically without calling `m_deviceContext.TryWaitIdle()` (since no existing pipeline is being destroyed).

---

### Finding 8 — Invariant Doors 1 & 2 Gaps: GPU register-index bounds in `sdfWords` and mesh-bake cache invalidation via `SdfIsaHlsl.DescribeFingerprint`

- **Severity:** Medium
- **Memo section:** `§3.3` ([`docs/plans/author-shaders.md:471-497`](../../../docs/plans/author-shaders.md)), `§3.4` ([`docs/plans/author-shaders.md:519-526`](../../../docs/plans/author-shaders.md)), `§4` Doors 1 & 2 ([`docs/plans/author-shaders.md:666-681`](../../../docs/plans/author-shaders.md))
- **Code citations:**
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-program.hlsli:49-65`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-program.hlsli#L49-L65):
  - [`src/Puck.SdfVm.Model/SdfIsaHlsl.cs:530-564`](../../../src/Puck.SdfVm.Model/SdfIsaHlsl.cs#L530-L564):
- **Failing case:**
  1. **GPU-side register index bounds:** Door 1 checks op count and static cost in `SdfShadeProgram`'s constructor and calls `sdfProgramRange` in HLSL. However, `sdfProgramRange` only checks that the instruction words `[start, start + count)` lie within `sdfWords`. Inside `sdfEvaluateShadeProgram`, each instruction word decodes register numbers (`dst`, `src0`, `src1`, `src2`). Without bit-masking those indices to `< SDF_SHADE_MAX_REGISTERS` in the HLSL decoder (`& (SDF_SHADE_MAX_REGISTERS - 1u)` with power-of-two `MaxRegisters`), the SPIR-V / DXIL compiler cannot prove that `regs[dst]` is in-bounds and emits bounds-checked dynamic indexing or allows out-of-bounds register access if `sdfWords` is corrupted.
  2. **Mesh-bake fingerprint contamination:** Memo §3.3 (line 495) and §3.4 (line 521) add `SdfShadeOp` to `SdfIsaHlsl`, whose stamp (`SDF_ISA_STAMP`) is checked by `ShaderInterfaceLayout.Mismatch` ([`SdfKernelSet.cs:110-133`](../../../src/Puck.SdfVm/SdfKernelSet.cs#L110-L133)). `SdfIsaHlsl` intentionally splits `DescribeFieldFingerprint()` (used by `MeshBakeLayout`, [`SdfKernelSet.cs:104-105`](../../../src/Puck.SdfVm/SdfKernelSet.cs#L104-L105), so world-only changes don't invalidate baked meshes) from `DescribeFingerprint()` (used by `WorldLayout`). If `SdfShadeOp` is folded into `DescribeFieldFingerprint()` instead of `DescribeFingerprint()`, every edit to a `shade` op will invalidate the mesh bake store and force a cold mesh rebake even though `shade` never runs in `sdf-mesh-bake`.
- **Fix:**
  - Make `SDF_SHADE_MAX_REGISTERS` a power of two, mask every decoded register field with `& (SDF_SHADE_MAX_REGISTERS - 1u)` in the generated HLSL decoder, and fold `SdfShadeOp` into `SdfIsaHlsl.DescribeFingerprint()` (for `WorldLayout` / `GlassParameters.Layout`) while leaving `DescribeFieldFingerprint()` (`MeshBakeLayout`) untouched.

---

### Finding 9 — GI probe relight (`SdfMeshIrradiance` / `sdfProbeShade`) ignores `shade` programs, and `world.debug-view` lacks a shade-cost / tier diagnostic

- **Severity:** Medium
- **Memo section:** `§3.1` ([`docs/plans/author-shaders.md:382-385`](../../../docs/plans/author-shaders.md)), `§3.8` ([`docs/plans/author-shaders.md:613-625`](../../../docs/plans/author-shaders.md))
- **Code citations:**
  - [`src/Puck.SdfVm/SdfMeshIrradiance.cs:2251-2253`](../../../src/Puck.SdfVm/SdfMeshIrradiance.cs#L2251-L2253):
    ```csharp
    private (Vector3 Varying, Vector3 Local) Shade(Lighting lighting, Vector3 point, Vector3 normal, int material, bool feedback, bool local, Layout layout, (int X, int Y, int Z)[] lows, SdfMeshIrradianceScene scene) {
        var surface = lighting.MaterialAt(index: material);
        var reflected = ((surface.Albedo * (1f - surface.Metal)) * (surface.Bleed ?? Vector3.One));
    ```
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-probe-lighting.hlsli:22-30`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-probe-lighting.hlsli#L22-L30):
- **Failing case:**
  1. **GI probe relight mismatch:** Both CPU probe relight (`SdfMeshIrradiance.Shade`, line 2147) and GPU probe relight (`sdfProbeShade`, `sdf-probe-lighting.hlsli:22-30`) read only rows 0–3 of the material (`albedo`, `emissive`, `metal`, `wrap`, `bleed`, `emissiveLane`). When an author writes a `shade` program that computes the surface's primary color or emission procedurally (or heavily alters `albedo` via `weathered`/`paint`), the scalar `SdfMaterial.Albedo` / `Emissive` in row 0 may be a placeholder or base undercoat, causing GI bounce and the exposure meter (`TryMeter`) to reflect the wrong color or zero emission.
  2. **No spatial debug view for `shade` cost/tier:** Memo §3.8 adds `world.budget` lines and per-frame GPU counters, and [`sdf-debug-views.hlsli:101-106`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/debug/sdf-debug-views.hlsli#L101-L106) already has `SDF_DEBUG_VIEW_SKY_COST` (`world.debug-view sky-cost`) to show per-pixel sky layer cost as a heatmap, but the memo adds no `world.debug-view shade-cost` (or tier indicator) to see which pixels in a view are hitting high-cost programs or still running on Tier 1.
- **Fix:**
  1. Require every material with a `shade` program to keep representative scalar `albedo`, `metal`, `emissive` (or derive average `albedo`/`emissive` at compile time when the program is constant-foldable or mixes known `SdfSurface` stages, supplemented by `bleed`) in rows 0–3 for `sdfProbeShade` / `SdfMeshIrradiance.Shade`, and document that contract explicitly in §3.1/§3.2.
  2. Add a `shade-cost` mode to `DebugViewModes` / `sdf-debug-views.hlsli` (parallel to `sky-cost`) that visualizes each pixel's `program.StaticCost / MaxCost` and tints Tier 1 vs Tier 2.

---

### Finding 10 — Step sequencing in §5 deletes `applyWeathering` before measuring and conflates single-layer Tier 1 overhead with 5-layer fixed-function register pressure

- **Severity:** Medium
- **Memo section:** `§5` Steps 1–6 ([`docs/plans/author-shaders.md:695-770`](../../../docs/plans/author-shaders.md))
- **Code citations:**
  - [`docs/plans/author-shaders.md:704-725`](../../../docs/plans/author-shaders.md):
  - [`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-light-stage.hlsli:109-121`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-light-stage.hlsli#L109-L121):
- **Failing case:**
  - In Step 1/Step 2, only `applyWeathering` is moved to `sdfEvaluateShadeProgram`, while `sdfApplyPaints`, `sdfApplyInset`, `sdfShadeFlow`, and `sdfToon*` **remain inline as fixed-function HLSL in `sdf-light-stage.hlsli`**. Measuring `# Reg` and frame time in Step 2 measures a kernel that has *both* the new Tier 1 interpreter loop *and* 4 of the 5 old fixed-function layers still inline, and only runs the interpreter on `weathered` (not on a material stacking `paint` + `inset` + `weathered` + `flow`, where the interpreter loops over all four).
  - Conversely, Step 5 ("The per-dispatch work bound and device-loss quarantine") is scheduled *after* Step 4 ("Material binning"), even though Step 2 says Steps 3 and 4 are skipped if Tier 1 meets the bar, and Step 5's `SdfShadeDispatch` assumes Step 4's bins (`"The bin dispatcher becomes SdfShadeDispatch"`).
- **Fix:**
  - Put `SdfShadeDispatch` / static cost admission in Step 1 with `SdfShadeProgram`.
  - Lower all pre-light surface layers (`paint`, `inset`, `weathered`, `flow`) to `SdfShadeProgram` before running the Step 2 register and frame-time A/B comparison against the baseline kernel, so Step 2 measures the true register savings of removing the fixed-function surface layers against the true interpreter cost of running stacked layers.

---

## Where Nothing Was Found (Verified Clean Against the Code)

1. **Isolating `shade` from the SDF field VM (`map` / `sdf-map.hlsli`):**
   - Verified against [`src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-map.hlsli:1-629`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-map.hlsli#L1-L629) and [`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-world-surface.comp.hlsl`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-world-surface.comp.hlsl). The memo's decision in §2 / §3.1 to keep `shade` out of `sdf-map.hlsli`, `sdf-mesh-bake`, shadow cascades, and `sdf-world-surface` is completely sound: baked meshes and shadow cascades depend only on geometry, and `sdf-world-views.comp.hlsl` already includes `field/sdf-shapes.hlsli` and `field/sdf-noise.hlsli` without calling `map()`.
2. **Transpiler lowering of `let`, `template`, and `for` (`SpellingEquivalence`):**
   - Verified that unrolling bounded `for` loops, expanding templates, and folding `let` bindings at `.puck` compile time into one canonical `SdfShadeProgram` op stream aligns with [`AGENTS.md`](../../../AGENTS.md) (*"Ergonomic is optimal: Cost depends on what a document means, never on how it is spelled"*).
3. **Quarantine and device-loss recovery hook point (`SdfWorldResidency.OnDeviceLost`):**
   - Verified against [`src/Puck.SdfVm/SdfWorldResidency.cs:776-791`](../../../src/Puck.SdfVm/SdfWorldResidency.cs#L776-L791). `OnDeviceLost()` is the single place where device objects are torn down and rebuilt on a recreated device, making it the right place to quarantine a faulting `SdfShadeProgram` hash and fall back to the neutral base material.
4. **Work counter infrastructure (`puckCountWork` / `sdfWorkSteps`):**
   - Verified against [`.claude/skills/rendering/references/kernels.md:117-133`](../../../.claude/skills/rendering/references/kernels.md) and [`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-stage.hlsli:70-72`](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-stage.hlsli#L70-L72) that `sdfWorkSteps` is currently unused in `views` (only `sdfWorkTexels` is incremented at line 153), so counting executed shade ops into `sdfWorkSteps` (or a dedicated detail row) uses existing wave-summed counter plumbing at zero extra atomic cost.

---

## Design Alternatives (Proposals)

### Proposal 1 — World-Specialized `views` Switch (Zero-Binning Tier 2) Instead of Multi-Pass `views-filter` / `views-shade` Binning

- **Label:** Proposal
- **Problem with the memo's §3.5 binning design:**
  - Splitting `views` into `views-filter` + prefix-sum + scatter + per-bin `views-shade` indirect dispatches forces **every frame** to read the 32-byte visibility record twice from VRAM, write and read back filtered AO/shadows and pixel indices, run extra dispatches and barriers, and lose 2D 8×8 tile locality for `sdfMeshProbesAt`—even when the world has only a handful of distinct structural shade programs (e.g. the 4 standard layers in `worlds/standard/`).
- **Proposed alternative:**
  1. **Separate structural program identity (`SdfShadeStructureHash`) from material parameter data (`sdfWords`):** Two materials that both use `weathered` (or both use `paint` + `weathered`) with different colors, scales, or thresholds compile to the **exact same `SdfShadeStructureHash`**, reading their per-material floats from `sdfWords[materialParamBase + k]`. In a typical world (and in `worlds/debug`), dozens of materials collapse to **3–8 distinct structural shade programs**.
  2. **Specialize one `sdf-world-views` kernel per world structural program set:** Instead of N separate `views-shade` kernels and a global-memory binning pass, Tier 2 generates a tiny header storing a `switch (programStructureId)` over the world's active structural programs (3–8 cases) directly at the shade call site inside `sdfLightStage`, keeping the single-pass `sdf-world-views` kernel (`sdfViewsGatherFilter` in groupshared memory -> `sdfViewsFilter` -> `sdfLightStage` -> output write) intact!
  3. **Wave-coherent execution without global memory traffic:** Inside an 8×8 tile, pixels on the same surface already share `programStructureId`, so waves take one branch of the `switch` coherently, DXC allocates registers to the union of live ranges (where only one branch is live at a time), and any newly edited program whose `programStructureId` is not yet in the compiled `switch` falls into the `default:` arm, which runs the Tier 1 interpreter until the background compile of the updated `switch` swaps in via `SdfWorldPipelines`!
  4. **Result:** Zero extra passes, zero extra VRAM reads/writes, zero `SDF_SHADOW_UNQUERIED` apron race, full 8×8 2D tile locality for GI probes, and zero parameter-edit recompiles.

### Proposal 2 — Unroll-and-Guard Small Register Files (4 `float4` Accumulators) for Tier 1

- **Label:** Proposal
- **Problem with the memo's §3.3 register file:**
  - A general `float4 regs[MaxRegisters]` array indexed by runtime instruction fields spills to local memory on NVIDIA Vulkan ([`.claude/skills/rendering/SKILL.md:729-734`](../../../.claude/skills/rendering/SKILL.md)).
- **Proposed alternative:**
  - Express `SdfShadeProgram` as a stackless stream over **4 named `float4` registers** (`r0`, `r1`, `r2`, `r3`) plus the mutable output struct (`albedo`, `roughness`, `metal`, `emissive`, `normal`, etc., which are already live scalar/vector locals in `sdfLightStage`), and select `r0..r3` via a 4-way ternary helper (`id == 0 ? r0 : id == 1 ? r1 : id == 2 ? r2 : r3`).
  - Every `paint`, `inset`, `weathered`, and `flow` program in the engine needs at most 4 temporary `float4` values live simultaneously when material outputs (`albedo`, `roughness`, `metal`, `normal`) are written directly to the output struct. Because `r0..r3` are scalarized registers, NVIDIA Vulkan and D3D12 never spill to local memory, and Tier 1 adds only `4 × 4 = 16` VGPRs of interpreter state.
