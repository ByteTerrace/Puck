# Citation check: gpu.md

Checked by opening each URL (WebFetch, curl and pdftotext for the PDFs) and each Puck file at the cited line. Verdicts are not softened. "Not opened" means this check did not reach the source.

## External claims

| # | Claim in gpu.md | Verdict | Evidence |
|---|---|---|---|
| S1 | Turing: 32 warps/SM, 64K regs/SM, 255 regs/thread, 16 blocks/SM; 4 warps with 4-way ILP or 16 without hide FMA latency | CONFIRMED | Turing guide: "32 on Turing", "64k 32-bit registers per SM", "maximum registers per thread is 255", "16"; latency "as few as 4 warps per SM" with 4-way ILP "or by 16 warps per SM without any instruction-level parallelism". |
| S2 | Ada: 48 warps/SM, same 64K regs, 255/thread, 24 blocks/SM | CONFIRMED | Ada guide quotes all four values. |
| S3 | RDNA2: 32-wide SIMD, 16 wave slots/SIMD vs 20 on RDNA1 | CONFIRMED | "In RDNA1, each SIMD has 20 slots... RDNA 2 and RDNA 3 have 16 slots per SIMD." |
| S3 | A VGPR spike in one branch reserves registers for the whole shader even if the branch is never taken | PARTLY | Source: "a single spike in one branch of the shader can cause the entire shader to require a lot of VGPRs." The "even if never taken" part is the fetch tool's gloss, not a verified quote. The mechanism is correct. |
| S3 | Higher occupancy can hurt cache; spilling hurts | CONFIRMED | "increasing the occupancy will result in more cache trashing and will not help hide the latency"; spill text matches. |
| S3 | Dates 2023-12-20, updated 2024-06-26 | CONFIRMED | |
| S3 | "Do not use RDNA3 capacity as a Deck spec" | CONFIRMED, and the source shows RDNA2 and RDNA3 are both 16 slots. The VGPR file size was not checked. | |
| S4 | Divergent paths in a warp execute separately; predication suppresses false lanes' writes, address evaluation and operand reads | CONFIRMED | Best Practices 13.1/13.2: "the different execution paths must be executed separately"; "Instructions with a false predicate do not write results, and they also do not evaluate addresses or read operands." Predication is for short blocks. |
| S4 | "Independent Thread Scheduling does not make arbitrary material paths free" | PARTLY | The guide mentions ITS only as allowing a warp "to remain diverged outside of the data-dependent conditional block". It says nothing about material paths, so this is the lane's inference. The "wave-uniform branch can be a good trade" point is also inference. The guide never uses the term "warp-uniform", only a threadIdx/WSIZE example. |
| S5 | AMD UE guide: 7900 XTX at 4K, SupportImportanceSampleBRDF 0.8 ms at 7 waves, 0.55 ms after StochasticInterpolation; credited to VGPR and fewer reads | CONFIRMED | Matches. The page also states it reduces frame time by 0.3 ms. |
| S6 | Burns/Hunt: little/no net benefit at ~2M visibility samples; test GPUs HD 5750, GTX 560 Ti, HD 4000, Iris Pro 5200 | CONFIRMED | pdftotext: "little to no net benefit with only two million visibility samples"; the four GPUs are listed. |
| S7 | DAIS: 30% of a 20-byte/sample G-buffer at 3840x2160, 8x MSAA | CONFIRMED | Fig. 1 caption: "30% of the deferred shading G-Buffer using 20 bytes per sample" at 3840x2160 with 8x MSAA. |
| S8 | Hable: RTX 3070, 1920x1088; 2.569 vs 3.01 ms low density full frame | CONFIRMED | Both are full-frame totals. The article calls the GPU "RTX 3070" (the min-spec section has a "GTX 3070" typo). The width is only implied by 1080p. |
| S8 | High density 6.43 vs 4.34 ms, "relevant prepass/material/lighting/visibility-utility sum" | CONFIRMED | They are subset sums excluding "Other". The draft says the denominators differ. Full-frame high-density totals are 11.161 ms (deferred) and 8.853 ms (visibility), which gpu.md omits. |
| S9 | UE 5.4 notes: binding-hash fix; shading bins exploded 2-3x with static lighting | CONFIRMED | Raw HTML: "would explode the # of shading bins (2-3x) when features like static lighting were enabled". |
| S9 | CS material sort saves ~0.16 ms, 4.79 -> 4.63 ms in CitySample Nanite BasePass without bundles; sort order as described | CONFIRMED | Raw HTML matches. Hardware is not stated, as the draft says. |
| S10 | Epic announcement (Cowley, 2024-04-25) scope; Box slides inaccessible, GDC PDF 403 | UNREACHABLE | The announcement URL returned 403 to both WebFetch and curl here. The draft says it opened it. Scope and date are unchecked. Nothing load-bearing rests on it. |
| S11 | Activision/Drobot: mixes Forward+ with visibility-buffer-style rendering | CONFIRMED | Abstract: "Main lit rendering effectively mixes Forward+ with Visibility Buffer style rendering approaches". |
| S12 | Pipeline-cache sample: 24.4 ms with cache vs 50.4 ms without, 2 pipelines, Mali G76 | CONFIRMED | The page says "a phone with a Mali G76". "Cached case exceeds a 16.67 ms frame" is the draft's own arithmetic. |
| S13 | GPL: 50% penalty estimate for unoptimized linking, outliers worse; specialization constants stay with shader stages; engines' enormous state combinations | CONFIRMED | "could be as bad as a 50% penalty in the general case, with outliers performing even worse" (vendors' estimate). "enormous sets of state and shader combinations". Spec constants cannot be given at link time. Fast-linking text is confirmed. |
| S14 | CreatePipelineLibrary: shared-data savings, blob must stay valid, driver/adapter mismatch errors, same-PSO load needs app synchronization | CONFIRMED | Thread Safety: "multiple threads loading the same PSO... should synchronize themselves". |
| S15 | MS PSO cache sample implements an ubershader while a specialized PSO compiles | CONFIRMED | The page date matches. The page describes it as slightly lower GPU performance in exchange for smoother frames. |
| S17 | AMD RDNA guide: "no need for locks"; uber shader first, specialize later; minimize PSOs | CONFIRMED | "There is no need for locks when using a pipeline library... The DirectX runtime will handle synchronization." "Consider using an uber-shader first and then compile specializations later." Posted 2023-03-22. The conflict with Microsoft's same-PSO exception is real. |
| S18 | Specialization sample: 4.4% on Mali G76; 0.5 -> 0.3 shortest-path cycles | CONFIRMED | "4.4% performance increase even when used in an extremely simple sample". |
| S19 | Shader objects: support compute; compilation at vkCreateShadersEXT; binary export via vkGetShaderBinaryDataEXT | CONFIRMED | The proposal says all three. It does not name driver support. See the RADV note below. |
| S21 | Slang: precompile modules to binary IR, specialize at link time, avoiding re-doing front-end work per variant | CONFIRMED | "precompile all three modules to binary IR"; with preprocessor defines the compiler "must redo almost all work from scratch". |
| S22 | DXVK: GPL compiles at D3D shader load; draw-time loaders still stutter; high CPU | CONFIRMED | Quote matches ("most Unreal Engine games" still stutter). |
| S23 | Fossilize records Vulkan objects and replays to build caches without running the game | CONFIRMED | "replay on N devices to build up VkPipelineCache objects without having to run application." |
| S24 | FAIL_ON_PIPELINE_COMPILE_REQUIRED is core 1.3 / EXT cache_control; not a deadline | CONFIRMED | Flag text and Vulkan 1.3 alias confirmed. "Not a deadline" is the draft's inference and is correct. |
| S25 | graphicsPipelineLibraryFastLinking: linking costs about a command-buffer record | CONFIRMED | |
| S27 | 2019-06-13 Valve notes: download and pre-compile the Vulkan pipeline collection | CONFIRMED | "enable downloading and pre-compiling of the whole collection of Vulkan pipelines". The same entry says pre-compiling "will be enabled in a future update", so the 2019 note alone does not show pre-compile was live. |
| S28 | 2020-08-29 notes: process the local pipeline DB on driver update | CONFIRMED | "Now processes local source pipeline database on driver update." |
| S29 | SPIRV-Tools linker is still under development | CONFIRMED | "The linker is still under development." |
| S16, S20, S26 | `VkPipelineCacheHeaderVersionOne`, dxcapi.h `IDxcLinker`, `vkGetShaderBinaryDataEXT` | NOT OPENED in this check | The shader-object proposal confirms the binary-export claim indirectly. |

## Misdescription and support checks

- **The Deck's driver (RADV, which may be how Steam pre-caching ships) is never mentioned.** gpu.md says GPL and shader objects are "optional backend optimizations" and that Deck behaviour is unverified, so it does not overstate. It also cites no source that GPL or `VK_EXT_shader_object` works on the Deck's RADV. S19, S13 and S25 do not name any driver. Only the DXVK README ("drivers supporting VK_EXT_graphics_pipeline_library") hints at driver dependence. Support on RADV is an uncited gap, but the conclusion treats both as optional.
- **Section "Steam and the Deck" ties Fossilize and the Valve notes to "Steam's Vulkan shader pre-caching on Deck".** The sources are 2019 and 2020 desktop Steam client notes plus the Fossilize README. None mentions the Steam Deck or SteamOS. The draft admits it did not verify the Deck's distribution policy. That is honest, but the Deck link is inference.
- **The AMD pages (S3, S5, S17) are vendor blogs and guides (GPUOpen), not API specification.** gpu.md labels them "AMD". Hable (S8) is a personal blog. Both are named accurately.
- **S5 is RDNA3 and changes the algorithm** (stochastic interpolation, fewer reads). The draft says so and does not transfer it.
- **Uncited but load-bearing:** the `65536 / (32 * 128) = 16` warps arithmetic (marked as the lane's own calculation; correct). The savings table (B = 0.20 ms) is declared hypothetical and the arithmetic is correct (for example, 4.3 x (0.5 + 0.5/1.5) + 0.2 = 3.783).

## Claims about Puck's code

| Claim | Verdict | Evidence |
|---|---|---|
| `views` is one invocation per pixel in an 8x8 workgroup (`sdf-pixel-entry.hlsli:1`; `sdf-views-stage.hlsli:124`) | CONFIRMED | Header comment: "one invocation a pixel of the render extent in an 8x8 workgroup"; `SDF_VISIBILITY_GROUP_EDGE 8u` is defined in `isa/sdf-render.hlsli:104`. Line 124 loads `sdfLoadSurfaceSample(worldVisibilityRecord(...))`. |
| Gathers a tile with an apron, group barrier, 4x4 filter (`:12`, `:24`, `:65`, `:118`, `:135`) | CONFIRMED | Tile edge is group+3, origin is -2; `GroupMemoryBarrierWithGroupSync()` at `:54`; the filter loops are 4x4 (`for y<4u`, `x<4u`); gather called at `:118`, filter at `:135`. |
| Material decode rows 0-31 plus paint, weathering, flow, toon, glass (`sdf-material.hlsli:9`, `:106`) | CONFIRMED | `sdfMaterialLoad` at `:106` reads `materialBase + 31u` for glass. I did not check each row. |
| Paint is evaluated before the full material load, to shorten live state; flow, emissive, lighting and indirect/specular follow (`sdf-light-stage.hlsli:132..232`; `sdf-material.hlsli:172`) | CONFIRMED | Comment at `:129-130` says "none of it is live across their shape dispatch"; the paint call is `:132`, `sdfMaterialLoad` at `:136`, flow at `:142`, the `meshIndirect` branch at `:209`, the specular/metal test at `:232`. |
| Debug selection and atmospheric transmittance follow light evaluation (`:140`, `:141`, `:153`) | CONFIRMED | |
| DXIL SM 6.6 and SPIR-V Vulkan 1.3; identity (`ShaderCompiler.Identity.cs:8`, `:36`, `:82`) | CONFIRMED | `ShaderModel = "6.6"`, `VulkanVersion = "1.3"`; line 82 is `EntryPoint: stage.EntryPoint`. The "closing source and options" identity was not checked line by line. |
| DirectX same-name loads under a lock; Vulkan validates identity (`DirectXPipelineLibrary.cs:285`; `VulkanPipelineCache.cs:69`) | CONFIRMED | `TryLoad` takes `lock (m_gate)` around every load, which is stronger than "same-name" only. `Refuse(data, identity)` validates the header. |
| Background reload builds (`SdfWorldResidency.ShaderReload.cs:33`, `:91`, `:95`, `:258`) | CONFIRMED | `BackgroundBuild<SdfWorldPipelineReload>` at `:33`, `m_reloadBuild.Start(...)` at `:258`. |
| `gpu-cost.md:63` and `:100` distinguish driver-native register counts from DXIL/SPIR-V and PIX's allocation percentage | PARTLY | `:63` says DXIL/SPIR-V carry no register count (correct). `:100` is about idle clocks under Remote Desktop. The "ComputeRegisterAllocation is a percentage, not registers per thread" statement is at line 106. The citation is off by 6 lines. |
| 4.3 ms and the "earlier high-register report" come from a research-mandates file | PARTLY | That path does not exist. The text is at the round's mandates file (`mandates.md` beside this check, its "Puck today" section) ("about 4.3 ms at the worst stops on an RTX 4070 ... high register allocation reported earlier by PIX"). The draft states this is an unmeasured brief figure. |
| "`sdfViewsGatherFilter` ... requires every group lane at its barrier"; therefore packed pixel bins would break it (`:24-58`) | CONFIRMED | The comment at `:22-23` says every lane calls it because it ends at the group barrier. `:110-112` says the group-uniform levers guarantee all lanes arrive. It derives the origin from `p.pixel % 8`. The constraint reasoning is sound. |

## Claims whose verdict changes a conclusion

1. **S10 (Epic Nanite announcement) is UNREACHABLE.** It returns 403 here, though the draft says it was opened. Only its role as scope context is lost. The Nanite binning evidence rests on S9, which is confirmed.
2. **Steam "pre-caching on Deck" rests on 2019 and 2020 Steam-client notes and the Fossilize README. None mentions the Deck or SteamOS.** The 2019 note says pre-compiling was still to be enabled. Deck-specific caching behaviour is therefore not evidenced. The draft hedges, but the section heading and framing imply more than the sources show.
3. **Nothing cited shows `VK_EXT_graphics_pipeline_library` or `VK_EXT_shader_object` is available on the Deck's RADV driver.** The conclusion keeps both optional, so it survives. Any move to depend on them for the Deck path would be unsupported.
4. **Hable's 6.43 vs 4.34 ms is a subset sum.** Full-frame high-density totals are 11.161 vs 8.853 ms. The draft says the denominators differ but does not give the totals. This strengthens the visibility-wins case at high density, but not as a "views" substitute.
5. **The research-mandates path in section 5 is wrong** (`research-shaders/mandates.md`). The 4.3 ms baseline that the savings table scales from is, as the draft says, unmeasured and uncaptured.
6. **gpu-cost.md line cites:** `:100` should be `:106` for the PIX allocation-percentage caveat.
7. **ITS and "wave-uniform branch is a good trade" are the lane's inference, not NVIDIA text.** The Best Practices guide only shows ITS permitting a warp to stay diverged. It does not support the claim that ITS leaves material paths non-free.

No other figure or quotation contradicted its source.
