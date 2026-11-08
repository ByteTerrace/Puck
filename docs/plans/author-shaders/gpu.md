# GPU-side performance of authored shading

Research lane. Findings only; no tracked files edited, no build or GPU run, no commit or push. Read `research-mandates.md`, the delegate and rendering skills, their GPU-cost reference, contributing guidance, and the relevant current source. Source titles, dates and opened URLs are collected below; bracketed source links identify the evidence for each external claim. Recommendations and numerical scenarios are explicitly distinguished from measurements.

**Finding:** authored shading and fast rendering are compatible. The promising baseline is canonical material programs, runtime parameter tables, selective specialization, and coherent compute work over the visibility data Puck already produces. Neither thousands of eagerly compiled permutations nor one permanently expanding kernel is a necessary consequence of author freedom. This is a design inference from the hardware, engine and API evidence below, not a measured Puck speedup.

## 1. Ubershaders, specialization, registers and divergence

### What actually costs GPU time

An ubershader keeps multiple behaviours in one compiled executable and selects among them with data. A specialized shader exposes some selections as compile-time facts, allowing removal of unused paths and their inputs. There are two separate opportunities: avoiding divergent execution and reducing the executable's worst-case resource requirements. A uniform branch can avoid executing an expensive path while still paying its register-allocation cost. AMD explicitly describes a single live-VGPR spike reserving registers for the entire shader even if that branch is never taken. [AMD occupancy][S3]

NVIDIA documents serialization of different paths within a warp; short branches may instead use predication, which schedules instructions but suppresses false lanes' results and operand/address evaluation. Independent Thread Scheduling does not make arbitrary material paths free. Material IDs differing between waves are not themselves divergence; differing control flow *within* a wave is the issue. A scalar, wave-uniform branch can be a good trade when it skips substantial work. [NVIDIA control flow][S4]

Specialization also removes loads and exposes constant folding and unrolling. However, unrolling can enlarge code and live ranges. The question is the compiled result, not whether the source contains `if`, a large struct, or a loop. Khronos' sample demonstrates the optimization opportunity when constants are known before pipeline creation. [Specialization constants][S18]

### Target architecture facts, not benchmark predictions

| Target | Verified architectural limit | Implication |
|---|---|---|
| RTX 2060 / Turing | 32 resident warps/SM, 64K 32-bit registers/SM, at most 255 registers/thread; 16 blocks/SM. | Register-heavy kernels can lose resident warps. NVIDIA also says core FMA dependency latency can be hidden by 4 warps with four-way instruction parallelism, or 16 without it: full occupancy is not intrinsically required. [S1] |
| RTX 4070 / Ada | 48 resident warps/SM, the same 64K register file and 255-register/thread limit; 24 blocks/SM. | More possible warps do not remove the register budget. [S2] |
| Steam Deck / RDNA2 | AMD describes 32-wide SIMD datapaths and 16 wave slots/SIMD for RDNA2, versus 20 for RDNA1. VGPR, LDS, workgroup and barrier constraints affect achievable occupancy. | Inspect the actual wave mode and allocation granularity in RGP/RGA. Do not use an RDNA3 discrete GPU's VGPR capacity as a Deck specification. [S3] |

**Illustrative arithmetic, not measured occupancy:** ignoring allocation granularity, SM partitions, LDS and workgroup admission, 128 registers/thread permits at most `65536 / (32 * 128) = 16` NVIDIA warps; 64 permits 32. That is 50% to 100% of Turing's warp limit, but about 33% to 67% of Ada's. It is not a prediction of 2x throughput. The underlying capacities are [S1/S2]; the calculation is this lane's.

**Measured example, deliberately not transferred to Puck:** AMD's UE guide shows a Radeon 7900 XTX at 4K running `SupportImportanceSampleBRDF` in 0.8 ms with a seven-wave VGPR limit, then 0.55 ms after enabling stochastic interpolation. The guide attributes the improvement to reduced VGPR demand *and* fewer texture reads. This changes the sampling algorithm and is RDNA3, so it is neither a pure specialization experiment nor evidence that Puck may reduce quality. [AMD UE guide][S5]

**Important qualification:** higher occupancy can worsen cache behaviour; extra occupancy helps only if there is useful latency to hide. Spilling registers to memory can hurt severely, and lowering a register cap can cause spilling. A large allocation percentage alone does not prove a shader is register-bound. [S3] The repository already makes the same measurement distinction: `.claude/skills/rendering/references/gpu-cost.md:63` and `:100` distinguish driver-native register counts from DXIL/SPIR-V and PIX's allocation percentage.

I found no defensible universal branch penalty in cycles, or controlled ubershader-versus-specialization speedup applicable to all three exact target devices. Those numbers remain unverified. Published capacities and the measurements below support hypotheses, not a millisecond forecast.

### Why games really compile thousands

Shader source, material instance, compiled entry point, and pipeline are different counts. Specialization selections, render passes and graphics state multiply potential executables/pipelines. For illustration, ten independent binary compile-time switches produce 1,024 possible combinations before passes or backends; this is arithmetic, not a reported engine count. A thousand differently coloured instances need not produce a thousand shaders when colour remains data. Khronos explicitly describes engines' enormous state/shader combination sets and the impracticality of exhaustive precompilation. [GPL rationale][S13]

**Recommendation:** specialize canonical program structure and a small measured set of structural choices; keep continuously changing values in data. Compile reachable combinations, deduplicate equivalent programs, and budget executable count/code bytes. Do not specialize every roughness, colour, seed, time or view value. This combines [S13, S18, S21] with Puck's canonical-lowering and change-proportional-work mandates.

## 2. Visibility buffers and deferred material shading

### The common mechanism

Visibility rendering postpones expensive material work until the visible surface is known. The original Burns/Hunt work stores compact visibility identifiers and classifies samples for later shading. It reports little or no net gain at about two million visibility samples; benefits grow with sample count and suitable cache/memory systems. Its four test GPUs are HD 5750, GTX 560 Ti, Intel HD 4000 and Iris Pro 5200, not today's bar devices. [Visibility Buffer paper][S6]

Deferred attribute interpolation is related but not identical: Schied/Dachsbacher cache triangle attributes and reference them through visibility samples. Their 2015 paper reports a buffer footprint of 30% of a conventional 20-byte/sample G-buffer in its 3840×2160, 8x-MSAA example. That is a memory result under that setup, not a 70% GPU-time reduction. [DAIS][S7]

**Proposed portable material dispatch:** read visible material/program IDs; count pixels or quads by program; prefix-sum counts; scatter work indices; issue indirect compute dispatches for the bins. Parameters remain indexed rows. Grouping by program improves execution coherence; grouping by spatial tile within a program preserves locality. Dispatch work is based on visible samples, not object count. This is an implementation synthesis, informed by [S6, S8, S9], not a claim that every cited engine uses this exact algorithm.

One compute program per unique canonical material program avoids multiplying it by mesh vertex layouts, blend/depth state and render-target combinations. It still creates O(unique programs) kernels, plus possible binding bins. Core DX12/Vulkan indirect dispatch does not magically choose an arbitrary PSO: the host normally records pipeline-bound dispatches, with the GPU providing their work counts. Empty bins, CPU recording, atomics, scans, tail waves and output scatter all remain costs. The distinction between graphics and compute pipeline inputs is explicit in Khronos' pipeline-management sample. [S12]

### Nanite: shader sharing, not one universal material shader

Epic's UE 5.4 notes confirm compute-based material shading and separate shading bins. They report correcting binding hashes that had inflated bin counts by **2–3x** with static lighting, and a compute-material sort change saving **0.16 ms, 4.79 → 4.63 ms**, in CitySample without bundles: group by export target mask, then shader and bindings, then indirect-argument position. Hardware/resolution are not specified beside that number; do not label it a 4070 result or a general raster-to-compute saving. [UE 5.4 release notes][S9]

The primary 2024 talk is Graham Wihlidal's *Nanite GPU-Driven Materials*. Epic's opened announcement verifies its scope: programmable rasterization, artist-authored materials, final shading and software VRS. The linked Box slides were inaccessible and the GDC PDF returned HTTP 403 in both the web tool and a direct request; detailed slide-only numbers are therefore **not independently verified here**. [Epic announcement][S10] Release notes provide the verified binning and compute evidence above. Do not use software-VRS savings as a full-quality specialization claim.

### Independent results and counterexamples

John Hable's 2021 DX12 prototype supports material graphs and generates a G-buffer from visibility, including analytic derivatives. PIX measurements on an **RTX 3070 at 1920×1088** show low-density whole-frame totals of **2.569 ms deferred versus 3.01 ms visibility**. At high density, the relevant prepass/material/lighting/visibility-utility sum is **6.43 versus 4.34 ms**. The denominator differs between those comparisons; neither is Puck's `views` pass. His material work rises with triangle density even at fixed pixel count because vertex fetches lose coherence. [Hable][S8]

Activision's *Geometry Rendering Pipeline Architecture* explicitly mixes Forward+ and visibility-buffer techniques after merging heterogeneous geometry into intermediate representations. This is evidence against treating visibility as universally best for every primitive or material. The opened abstract does not substantiate specific material-bin timings; no such numbers are claimed here. [Drobot][S11]

Material/lighting separation can reduce peak live state but incurs an intermediate buffer. Analytic derivatives permit compact pixel shading, yet arbitrary authored texture-coordinate calculations can require extra work; Hable discusses dependent texture reads and difficult refraction cases. [S8] **Puck implication:** preserve exact sampling semantics by using analytic footprints where supported, or coherent quads/explicit gradients where needed. Arbitrarily shuffled pixels cannot safely inherit screen-space `ddx`/`ddy` meaning. The compiler must describe derivative requirements before choosing a bin layout.

### Keeping pipeline counts controlled

**Recommendation:** distinguish program identity, parameter binding identity, and visible-work bin identity. Many bins may use the same executable; many material instances may use the same parameter layout/program. Epic's 2–3x bin-count correction illustrates why incidental object identity must not enter keys. [S9] Puck's equivalent spellings should lower to one program and one specialization identity. This is a proposed canonicalization rule, not proof that arbitrary source languages are semantically equivalent or automatically deduplicable.

## 3. Pipeline creation and caching without stutter

### There are several caches

Source-to-DXIL/SPIR-V compilation, application pipeline-object reuse, serialized driver pipeline data, and driver-managed caches solve different costs. Having DXIL/SPIR-V on disk does not mean the device executable is ready; Vulkan pipeline creation still performs internal compilation. Khronos measures **24.4 ms with a cache versus 50.4 ms without** when recreating two sample pipelines on Mali G76. Even the cached case exceeds a 16.67 ms frame budget if done synchronously. These are sample creation timings, not a bound for desktop GPUs. [S12]

### Direct3D 12

Use PSOs prepared before first draw/dispatch, an in-memory key-to-object cache and persistent pipeline libraries where supported. `ID3D12Device1::CreatePipelineLibrary` documents shared-data savings, capability querying, adapter/driver mismatch handling and a requirement that the input blob remain alive. It also explicitly requires application synchronization when multiple threads load the **same** PSO; this qualifies AMD's broader statement that runtime synchronization removes the need for locks. [Microsoft API][S14] [AMD guide][S17]

Microsoft's sample implements an ubershader while a specialized PSO compiles, trading some GPU performance for smooth frames. This is an executable example of asynchronous fallback, not a proof that any arbitrary external shader has an equivalent fallback. [PSO cache sample][S15]

### Vulkan pipeline caches and extensions

| Mechanism | What it helps | Boundary |
|---|---|---|
| `VkPipelineCache` | Reuses driver work during graphics/compute pipeline creation; save and restore between runs. | Does not eliminate first-use creation or guarantee a cheap cache hit. Validate vendor/device/cache UUID; a miss must rebuild. [S12, S16] |
| `VK_EXT_pipeline_creation_cache_control` / core 1.3 flags | `FAIL_ON_PIPELINE_COMPILE_REQUIRED` lets the application decline compilation on a latency-sensitive attempt and schedule work elsewhere. | This is not an async compiler or a hard wall-clock deadline for the API call. Feature support must be enabled. [S24] |
| `VK_EXT_graphics_pipeline_library` | Separately compiles vertex input, pre-rasterization shaders, fragment shader, fragment output; reuses shared pieces. | It is a **graphics** pipeline facility, not the primary answer for Puck's compute `views` kernels. Specialization constants accompany their shader stages, not final library linking. [S13] |
| `VK_EXT_shader_object` | Creates/binds shader stages independently of monolithic pipelines, with dynamic graphics state; supports compute too. | Compilation remains in `vkCreateShadersEXT`; it is stage linking, not arbitrary in-shader function dispatch. Query support; do not require it for the baseline. [S19] |

GPL has an important two-speed model: fast link, then compile an optimized link off-thread and replace it. The proposal reports a vendor estimate of up to **50% device-performance penalty** for unoptimized linking in the general case, with worse outliers; this is an estimate, not a universal measured penalty. The `graphicsPipelineLibraryFastLinking` property specifically identifies implementations where non-LTO linking is comparable to command recording. Extension support alone is not that guarantee. [S13, S25]

Shader objects expose binary export through `vkGetShaderBinaryDataEXT`; do not assume an existing `VkPipelineCache` serializes them. The proposal still distinguishes compiling from SPIR-V and restoring implementation binaries. [S19, S26] For Puck's current compute-focused workload, an additional shader-object backend needs an actual measured benefit over ordinary cached compute PSOs.

### Steam and the Deck

Valve's 2019 client notes document downloading pipeline collections; its 2020 notes document processing the local source pipeline database after driver updates. Fossilize records Vulkan creation inputs and SPIR-V, then replays them on devices to build pipeline caches without running the game. The portable replay database and the resulting device/driver cache are different artifacts. [Valve client notes][S27] [Driver-update processing][S28] [Fossilize][S23]

This is the relevant model for Steam's Vulkan shader pre-caching on Deck: prepare driver compilation ahead of gameplay instead of paying for every newly encountered pipeline during a frame. Downloads/processing can recur as content or drivers change. Coverage is limited to available shader/pipeline descriptions; a newly authored Puck program cannot be assumed present. I did not verify the exact current SteamOS cache-distribution policy, per-title coverage, or precompiled-binary-versus-local-replay mix on a physical Deck; no claim of universal hitch elimination is warranted. [S23, S27, S28]

DXVK provides an independent implementation check: its README says GPL allows compilation when D3D shaders load, reducing/eliminating stutter in many games, but shaders first loaded at draw time still cause some stutter. It also warns of high CPU use while compiling. This describes DXVK's D3D8–11 translation, not Puck's native Vulkan backend or all Proton/D3D12 games. [DXVK][S22]

### Proposed Puck policy

Discover demanded programs from compiled world documents before exposure; prewarm those programs and needed pipeline layouts asynchronously with bounded host workers. Keep persistent driver caches disposable and separately keyed from portable program artifacts. Queue compile work once per canonical identity; expose queued/running/failed states and costs. Publish a complete compatible pipeline version at a frame boundary and retire old versions after GPU completion. These are recommendations based on [S12–S17, S21–S24] and Puck's mandates.

For a hot edit, keeping the previous material until the replacement is ready is feasible. For a **new** arbitrary HLSL/SPIR-V program, a generic ubershader cannot automatically reproduce it. Choices consistent with the no-quality-reduction mandate are preparation before activation, or an equivalent baseline generated from a supported common IR. A magenta/default material is an error/authoring indicator, not an acceptable performance fallback. Guaranteeing both no waits and immediate display of never-before-seen arbitrary code is not established by any cited caching API.

## 4. Specialization constants and function linking

**Vulkan specialization constants** are supplied at pipeline creation, allowing dead-code removal and loop optimization unavailable when values arrive later through UBOs/push constants. Khronos' Mali G76 sample reports **4.4% performance improvement** for a simple light loop; its offline compiler's shortest arithmetic path changes **0.5 → 0.3 cycles**. The latter is a static estimate, not a measured wall time. Every selected value set may still require a separate device executable. [S18]

**DXIL libraries** provide compiler-level composition: DXC's `IDxcLinker` registers libraries and links a named entry point/target into a shader blob the runtime consumes. That does not replace PSO creation or imply cheap runtime calls between material functions. A D3D12 *pipeline library* is a cache of pipelines; a DXIL *library* is compiler input. They are different facilities. [DXC interface][S20] There is no Vulkan-style specialization-constant parameter in the ordinary DX12 compute PSO interface; use compiler-generated variants or library linking for the same structural choices rather than assuming `vk::constant_id` optimizes DXIL.

**SPIR-V linking** in SPIRV-Tools combines modules into a library or executable; its documentation still calls the linker work in progress. Link, optimize, and validate against Vulkan's target environment before creating the pipeline. Do not confuse module linking with GPL's pipeline-stage linking or with hardware function calls. No compile-time improvement for Puck's actual DXC output was measured here. [SPIRV-Tools][S29]

**Slang specialization** is especially relevant to the middle path: precompile modules to binary IR, then supply types/constants at link time, avoiding repeated parsing/type checking for each macro permutation. It supports typed generics and link-time specialization. That gives an ergonomic compiler architecture for many program variants but does not eliminate backend optimization or driver compilation. Slang documents a mechanism, not a guaranteed under-30-second Puck edit loop. [Slang][S21]

**Recommendation:** evaluate a canonical authored shading IR with typed module interfaces, parameters and derivative requirements; cache reusable IR separately from specialized DXIL/SPIR-V and device pipelines. Benchmark Slang against the existing DXC route on actual material programs before adopting it. Whichever frontend wins, engine-provided materials should use the same program path as author materials, not become a second implementation. Avoid dynamic function calls as an assumed register-pressure fix: inlining, call ABI, code size and driver behaviour must be inspected.

## 5. What is verified about Puck, and what might 4.3 ms become?

### Current source evidence

| Observation from this checkout | Source |
|---|---|
| `views` runs one invocation/pixel in an 8×8 workgroup, loading a surface record. | `src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-pixel-entry.hlsli:1`; `passes/sdf-views-stage.hlsli:36` |
| It gathers a spatial tile with an apron, reaches a group barrier, and applies a 4×4 AO/shadow filter before lighting. | `passes/sdf-views-filter.hlsli:23`, `:35`, `:76`, `passes/sdf-views-stage.hlsli:29`, `:52` |
| Material decode exposes rows 0–31 and optional paint, weathering, flow, toon and glass data. | `shade/sdf-material.hlsli:9`, `:106` |
| Paint evaluation already precedes the full material load, expressly shortening live state. Flow, emissive, lighting and indirect/specular paths follow. | `passes/sdf-light-stage.hlsli:132`, `:136`, `:142`, `:188`, `:209`, `:232`; `shade/sdf-material.hlsli:172` |
| Views includes debug selection and atmospheric transmittance after light evaluation. | `passes/sdf-views-stage.hlsli:58`, `:59`, `:71` |
| Shader compilation targets DXIL SM 6.6 and SPIR-V Vulkan 1.3, with source closure/options/compiler identity. | `src/Puck.Shaders.Model/Compiler/ShaderCompiler.Identity.cs:8`, `:36`, `:82` |
| Both device-cache mechanisms already exist; DirectX handles same-name loads with a lock, Vulkan validates cache identity and coordinates serialization. | `src/Puck.DirectX/Interop/DirectXPipelineLibrary.cs:10`, `:53`, `:285`; `src/Puck.Vulkan/Interop/VulkanPipelineCache.cs:7`, `:69`, `:105` |
| Reload builds are already prepared in the background and taken for installation. | `src/Puck.SdfVm/SdfWorldResidency.ShaderReload.cs:33`, `:91`, `:95`, `:258` |

Shader-relative paths in the table are under `src/Puck.SdfVm/Assets/Shaders/Sdf/`. Source structure supports the ubershader/material-table description; it does **not** prove every decoded field survives optimization or every listed load executes. There is already useful lifetime discipline and cache infrastructure to extend rather than duplicate.

The **~4.3 ms at worst stops on RTX 4070** and earlier high-register report are supplied by the brief and the round's mandates file (`mandates.md` beside this file). A search of available scratch/planning documents found no underlying capture or counter export establishing those exact readings. I did not remeasure them. In particular, no native register count, spill size, active-lane ratio or current shader identity was confirmed.

### Savings: an accounting model, not a promise

Let `f` be the fraction of the 4.3 ms that the change accelerates, `s` its speedup, and `B` the new classification/dispatch/intermediate-buffer overhead in milliseconds:

`new_views = 4.3 * ((1 - f) + f / s) + B`

The following are deliberately hypothetical, using **B = 0.20 ms**, which has not been measured:

| Accelerated fraction f | Speedup s | New cost | Saving |
|---|---:|---:|---:|
| 25% | 1.25× | 4.285 ms | 0.015 ms |
| 50% | 1.5× | 3.783 ms | 0.517 ms |
| 75% | 2× | 2.888 ms | 1.413 ms |

The real outcome may regress. Register improvements can accelerate code beyond the removed branches, so `f` is an attribution aid rather than a clean source-line partition. Benefits must exceed binning, added reads/writes, barriers, underfilled dispatches and any loss of spatial locality. These numbers are arithmetic from the supplied baseline, not vendor results or a recommended target.

**First candidate:** specialize the active material-program structure and view-wide features while retaining the coherent pixel layout. This can expose dead paths without paying a full reorder. **Second candidate:** classifying coherent tiles/quads by program. **Third candidate:** compact pixel bins plus a separate spatial filter, if measured register/divergence savings cover the new traffic. All are hypotheses to test against the same quality and authored semantics.

The current filter is a critical constraint: `sdfViewsGatherFilter` derives a group's origin from screen coordinates and requires every group lane at its barrier. Sending arbitrary packed material pixels into it would break its assumptions. A binned implementation must retain coherent tiles, or execute the spatial stage once before compaction and provide its outputs to the material pass. This follows directly from `passes/sdf-views-stage.hlsli:24–58`, not from analogy with Nanite.

### Measurement handoff to the lead

No GPU run is authorized for this findings-only lane. The lead owns these bounded follow-ups in the debug world:

1. Reproduce the reported worst stops at highest quality, record resolution/view count, native shader identity, clocks, GPU pass time and work counts. Inspect driver-native registers, spills, occupancy limiters, instruction stalls, memory traffic and divergence; distinguish views from surface, glass and background work. Decides whether specialization attacks the actual limiter.
2. Compare baseline, layout-preserving specialization and tile/bin variants with identical world/input/camera/exposure. Include mixed-material silhouettes, dense foliage, paint/weathering/flow, toon, screens, glass and nested views. Count programs, bins, active pixels, empty/tail work, scratch traffic and all new passes. Decides whether savings survive the full frame and material diversity.
3. Run DX12 and Vulkan on RTX 2060/4070 and Vulkan on Deck. Use cold and warm pipeline caches, a new material, an edit to a shared helper, and parameter-only edits. Record creation latency off-thread, frame-time tails, cache hit/miss counts and edit-to-visible latency. Decides baseline portability, cache usefulness and the under-30-second edit target.
4. Compare generated baseline/specialized shader outputs for equivalent programs, including derivatives and boundary cases. Check generated resource interfaces disallow simulation state. Decides whether the faster path preserves authored meaning, not whether it matches historical wrong output.

This is a measurement request, not a claim of verification. The rendering/GPU-cost skill was useful: it exposed the spatial-filter constraint and the PIX metric caveat; the verification skill routes quiet timings to the lead. No skill was edited or evaluated in this lane.

## Disagreements and limits that affect the decision

- **Universal specialization advice versus production constraints:** the specialization sample recommends specializing control flow; AMD recommends an uber fallback and limiting PSOs. These optimize different costs. Use specialization where stable structure and repeated GPU savings repay compilation, storage and dispatch overhead. [S18, S17]
- **More occupancy versus more performance:** AMD explicitly rejects that equivalence; NVIDIA's latency-hiding example also needs fewer than maximum warps. Register counts are evidence to investigate, not the success metric. [S3, S1]
- **Visibility always wins:** the original paper reports weak low-sample gains, Hable measures a low-density loss, and Activision deliberately mixes approaches. Puck already shades visible records in compute, so it cannot claim the entire forward/raster-to-visibility gain again. [S6, S8, S11; Puck source above]
- **PSOs solve stutter:** AMD explains why explicit PSOs move compilation ahead of draws, while Khronos documents how real engines still suffer combinatorial prewarming and hitching. Both can be true: the API exposes scheduling responsibility but does not fulfil it for the engine. [S17, S13]
- **Pipeline-library synchronization:** AMD's broad no-lock recommendation has Microsoft's same-PSO exception. Follow the API contract. Puck already does. [S17, S14; `DirectXPipelineLibrary.cs:285`]
- **Fast linking versus fast GPU execution:** GPL's fast link can sacrifice device optimization; its own proposal recommends replacing it with an optimized pipeline. Shader objects are a separate design with different support assumptions. Neither is a universal upgrade for compute shading. [S13, S19, S25]
- **Quality caveat:** software VRS, stochastic sampling, precision reductions and approximated derivatives are not accepted here as automatic performance fixes. The cited results establish mechanisms; Puck's highest-quality/no-reduction mandate controls what it may adopt.

## What this means for Puck

**Recommendation to the research lead:** permit authored shading through a shared compilable program contract, and make built-in layers clients of it. Keep presentation float work isolated from simulation by compiler/resource-interface enforcement. A program's cost depends on its canonical meaning and actual execution, not source spelling. This is a proposed invariant and enforcement boundary; accepting arbitrary native bytecode without validation would not establish it.

The first architectural experiment should extend Puck's existing visibility/material-table design, not replace it wholesale with another engine's renderer. Separate program identity from parameter rows, specialize only demanded structural choices, preserve the spatial filter, and measure whether coherent bins pay. Retain one common authored implementation capable of producing baseline and specialized executables. Graphics-pipeline libraries and shader objects remain optional backend optimizations; compute dispatch plus ordinary cached pipelines is the portable baseline for this question.

Puck-native opportunities deserve equal consideration:

- **SDF source:** derive coordinate frames, gradients/footprints and immutable material inputs from the authored source or mesh derivation when semantics permit. Do not bake camera-, light-, time- or screen-dependent results into a supposedly static material. Validate animated/deformed cases and grazing-angle footprints.
- **Input-derived caches:** key canonical IR, compiler modules, specializations and portable bytecode from their true inputs. Key device binaries additionally by device/driver compatibility. Build/package prewarming is disposable derived output; cache misses compile again and are counted.
- **Change-proportional work:** distinguish a parameter-row edit from a program-structure edit and a shared-module edit. Rebuild only affected program families and leave unrelated pipelines ready. Reuse shader/module/pipeline identities across nested views where compatible; view transforms normally remain data.
- **`world.budget`:** attribute generated program count, compile CPU time, pending compiles, native code size where available, active material pixels, bins/tail waste, scratch traffic and GPU work to the authored program. Distinguish measured costs from compiler estimates; expose bottlenecks without requiring authors to hand-unroll or paste code.

These are proposals to evaluate, not measured wins. The evidence supports pursuing selective specialization and material-coherent execution, with a complete async preparation/cache design. It does not support promising that 4.3 ms becomes 2 ms, requiring hardware-only scheduling features, or claiming unrestricted new shader code can appear instantly with no compilation cost.

## Opened source register

All URLs below were opened and their relevant content read in the research round. “Undated” means no publication/update date was established from the opened content; it is not the crawler's date. No unverified search snippet is used as a measured result. Source summaries above are deliberately selective; these are independent vendor, API, engine, paper and implementation sources rather than one library treated as specification.

- S1: NVIDIA, [Turing Tuning Guide][S1], CUDA 13.5 online edition; undated.
- S2: NVIDIA, [NVIDIA Ada GPU Architecture Tuning Guide][S2], CUDA 13.5 online edition; undated.
- S3: François Guthmann / AMD, [Occupancy explained][S3]; 2023-12-20, updated 2024-06-26.
- S4: NVIDIA, [CUDA C++ Best Practices Guide][S4], control-flow sections, CUDA 13.5 online edition; undated.
- S5: AMD GPUOpen, [Unreal Engine Performance Guide][S5], RGP and UE example; undated.
- S6: Christopher A. Burns and Warren A. Hunt, [The Visibility Buffer: A Cache-Friendly Approach to Deferred Shading][S6], JCGT 2(2); 2013.
- S7: Christoph Schied and Carsten Dachsbacher, [Deferred Attribute Interpolation for Memory-Efficient Deferred Shading][S7]; 2015.
- S8: John Hable, [Visibility Buffer Rendering with Material Graphs][S8]; 2021-07-05.
- S9: Epic Games, [Unreal Engine 5.4 Release Notes][S9]; version 5.4 (2024), page undated. The bin-count and timing passages were also read directly from the page's HTML because the web tool's find operation did not locate them.
- S10: Dana Cowley / Epic Games, [Take a deep dive into Nanite GPU-driven materials][S10]; 2024-04-25.
- S11: Michal Drobot / Activision, [Geometry Rendering Pipeline Architecture][S11]; September 2021 publication path, abstract undated.
- S12: Khronos Vulkan Samples, [Pipeline Management][S12]; undated.
- S13: Khronos, [VK_EXT_graphics_pipeline_library feature proposal][S13]; undated.
- S14: Microsoft, [ID3D12Device1::CreatePipelineLibrary][S14]; online API reference, date not established.
- S15: Microsoft, [Direct3D 12 pipeline state cache sample][S15]; page dated 2026.
- S16: Khronos, [VkPipelineCacheHeaderVersionOne][S16]; undated.
- S17: AMD GPUOpen, [RDNA Performance Guide][S17]; page says originally posted 2023-03-22.
- S18: Khronos Vulkan Samples, [Utilizing Specialization Constants][S18]; undated.
- S19: Khronos, [VK_EXT_shader_object feature proposal][S19]; undated.
- S20: Microsoft DirectXShaderCompiler, [dxcapi.h / IDxcLinker][S20]; moving main source, undated.
- S21: Slang, [Link-time Specialization and Module Precompilation][S21]; undated.
- S22: DXVK, [README / Graphics Pipeline Library][S22]; moving master source, undated.
- S23: ValveSoftware, [Fossilize README][S23]; moving master source, undated.
- S24: Khronos, [VkPipelineCreateFlagBits][S24]; undated.
- S25: Khronos, [VkPhysicalDeviceGraphicsPipelineLibraryPropertiesEXT][S25]; undated.
- S26: Khronos, [vkGetShaderBinaryDataEXT][S26]; undated.
- S27: Valve, [Steam Client Update Released][S27], shader collection download entry; 2019-06-13.
- S28: Valve, [Steam Client Update Released][S28]; 2020-08-29, re-released the next day.
- S29: KhronosGroup, [SPIRV-Tools README / Linker][S29]; moving source, undated.

[S1]: https://docs.nvidia.com/cuda/turing-tuning-guide/ "NVIDIA, Turing Tuning Guide, CUDA 13.5 online edition; undated"
[S2]: https://docs.nvidia.com/cuda/ada-tuning-guide/ "NVIDIA, NVIDIA Ada GPU Architecture Tuning Guide, CUDA 13.5 online edition; undated"
[S3]: https://gpuopen.com/learn/occupancy-explained/ "François Guthmann / AMD, Occupancy explained; 2023-12-20, updated 2024-06-26"
[S4]: https://docs.nvidia.com/cuda/cuda-c-best-practices-guide/ "NVIDIA, CUDA C++ Best Practices Guide, control-flow sections, CUDA 13.5 online edition; undated"
[S5]: https://gpuopen.com/learn/unreal-engine-performance-guide/ "AMD GPUOpen, Unreal Engine Performance Guide, RGP and UE example; undated"
[S6]: https://jcgt.org/published/0002/02/04/paper.pdf "Christopher A. Burns and Warren A. Hunt, The Visibility Buffer: A Cache-Friendly Approach to Deferred Shading, JCGT 2(2); 2013"
[S7]: https://cg.ivd.kit.edu/publications/2015/dais/DAIS.pdf "Christoph Schied and Carsten Dachsbacher, Deferred Attribute Interpolation for Memory-Efficient Deferred Shading; 2015"
[S8]: https://filmicworlds.com/blog/visibility-buffer-rendering-with-material-graphs/ "John Hable, Visibility Buffer Rendering with Material Graphs; 2021-07-05"
[S9]: https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-engine-5.4-release-notes?application_version=5.4 "Epic Games, Unreal Engine 5.4 Release Notes; version 5.4 (2024), page undated"
[S10]: https://www.unrealengine.com/blog/take-a-deep-dive-into-nanite-gpu-driven-materials "Dana Cowley / Epic Games, Take a deep dive into Nanite GPU-driven materials; 2024-04-25"
[S11]: https://research.activision.com/publications/2021/09/geometry-rendering-pipeline-architecture "Michal Drobot / Activision, Geometry Rendering Pipeline Architecture; September 2021 publication path, abstract undated"
[S12]: https://docs.vulkan.org/samples/latest/samples/performance/pipeline_cache/README.html "Khronos Vulkan Samples, Pipeline Management; undated"
[S13]: https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_graphics_pipeline_library.html "Khronos, VK_EXT_graphics_pipeline_library feature proposal; undated"
[S14]: https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device1-createpipelinelibrary "Microsoft, ID3D12Device1::CreatePipelineLibrary method; online API reference, date not established"
[S15]: https://learn.microsoft.com/en-us/samples/microsoft/directx-graphics-samples/d3d12-pipeline-state-cache-sample-win32/ "Microsoft, Direct3D 12 pipeline state cache sample; page dated 2026"
[S16]: https://docs.vulkan.org/refpages/latest/refpages/source/VkPipelineCacheHeaderVersionOne.html "Khronos, VkPipelineCacheHeaderVersionOne reference; undated"
[S17]: https://gpuopen.com/learn/rdna-performance-guide/ "AMD GPUOpen, RDNA Performance Guide; page says originally posted 2023-03-22"
[S18]: https://docs.vulkan.org/samples/latest/samples/performance/specialization_constants/README.html "Khronos Vulkan Samples, Utilizing Specialization Constants; undated"
[S19]: https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_shader_object.html "Khronos, VK_EXT_shader_object feature proposal; undated"
[S20]: https://raw.githubusercontent.com/microsoft/DirectXShaderCompiler/main/include/dxc/dxcapi.h "Microsoft DirectXShaderCompiler, dxcapi.h / IDxcLinker; moving main source, undated"
[S21]: https://docs.shader-slang.org/en/latest/external/slang/docs/user-guide/10-link-time-specialization.html "Slang, Link-time Specialization and Module Precompilation; undated"
[S22]: https://github.com/doitsujin/dxvk/blob/master/README.md "DXVK, README / Graphics Pipeline Library; moving master source, undated"
[S23]: https://github.com/ValveSoftware/Fossilize/blob/master/README.md "ValveSoftware, Fossilize README; moving master source, undated"
[S24]: https://docs.vulkan.org/refpages/latest/refpages/source/VkPipelineCreateFlagBits.html "Khronos, VkPipelineCreateFlagBits reference; undated"
[S25]: https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceGraphicsPipelineLibraryPropertiesEXT.html "Khronos, VkPhysicalDeviceGraphicsPipelineLibraryPropertiesEXT reference; undated"
[S26]: https://docs.vulkan.org/refpages/latest/refpages/source/vkGetShaderBinaryDataEXT.html "Khronos, vkGetShaderBinaryDataEXT reference; undated"
[S27]: https://store.steampowered.com/news/posts/?enddate=1566860279&feed=steam_client "Valve, Steam Client Update Released, shader collection download entry; 2019-06-13"
[S28]: https://store.steampowered.com/oldnews/74990?l=english "Valve, Steam Client Update Released; 2020-08-29, re-released the next day"
[S29]: https://github.com/KhronosGroup/SPIRV-Tools "KhronosGroup, SPIRV-Tools README / Linker; moving main source, undated"

The 2024 Nanite talk's inaccessible slide URL is recorded solely as an access limitation, not supporting evidence: <https://media.gdcvault.com/gdc2024/Slides/GDC%2Bslide%2Bpresentations/Nanite%2BGPU%2BDriven%2BMaterials.pdf>. The official announcement [S10] and release notes [S9] were accessible. Physical Deck behaviour, exact three-device specialization gains, and Puck's supplied 4.3 ms/native-register readings remain unconfirmed.
