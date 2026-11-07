# Author shaders lane: SDF-native and user-content systems, safety, languages, and Dolphin

Research lane for the author-shaders round (`mandates.md`). Every source below
was opened in the research round unless marked **unverified**. Large
documents (the Dreams slides, the WGSL spec, the GLSL ES 1.00 spec, the Dolphin
posts, the Dolphin source) were downloaded and read as text; quotations come from
that text.

## Puck today, checked against the code

- Puck interprets SDF programs on the GPU: `src/Puck.SdfVm/Assets/Shaders/Sdf/isa/sdf-isa.hlsli`
  is generated from the C# `SdfOp` model (27 ops, `SDF_OP_RESET_POINT` to
  `SDF_OP_CELL_DISPLACE`, plus the shape types) and says "regenerate it, never
  edit it". The SDF side is already an interpreter over a closed instruction
  set, much like Dolphin's TEV ubershader (see section 5).
- Materials are data: `src/Puck.SignedDistance/SdfAppearance.cs` packs a material
  table, a paint table (`SDF_MAX_PAINTS_PER_MATERIAL`), flow control words and
  screen surfaces into a program's word stream. The shading includes under
  `shade/` (about 2,900 lines, e.g. `sdf-material.hlsli`, `shade-layers.hlsli`,
  `shade-weathering.hlsli`, `shade-flow.hlsli`, `sdf-toon.hlsli`) read those rows.
  That makes it an ubershader with a material table.
- Puck has just lived through a GPU hang caused by its own work size:
  the commit that cut relight jobs. One 384-probe relight batch carried about 4.4 million
  rays as a single dispatch, and DRED named the open ExecuteIndirect in four
  reproductions. The fix cuts relight jobs at `RelightRaysPerJob` (131072)
  *predicted* rays. The device-loss recovery commit records device-loss recovery holding on
  both backends: "Graphics device recovered; rendering resumed after 1
  attempt(s)" on Vulkan, and gpu.faults remove/lose recovered on D3D12. Puck
  therefore already has the two parts the safety section argues for: work cut by
  predicted cost, and recovery as the backstop.
- `docs/plans/open-items.md` § "Author shaders" asks for a design memo covering
  the plug-in points, the language, compilation on both backends, costing in
  `world.budget`, the read/write contract, and the engine's layers as the first
  authored shaders.

---

## 1. Media Molecule's Dreams

### Representation: an edit list, evaluated into point clouds

Source: Alex Evans, "Learning from Failure: a Survey of Promising, Unconventional
and Mostly Abandoned Renderers for 'Dreams PS4', a Geometrically Dense, Painterly
UGC Game", SIGGRAPH 2015 Advances in Real-Time Rendering,
https://advances.realtimerendering.com/s2015/AlexEvans_SIGGRAPH-2015-sml.pdf.
Announced on the Media Molecule blog 12/08/2015,
https://www.mediamolecule.com/blog/article/siggraph_2015. Every slide carries the
caveat "Pre-production/concept/prototype artwork. Not representative of final
game."

- **The source of truth is a list of SDF edits, never a stored field.** "the core
  idea of lists of simple SDF elements, is still how all sculptures are made in
  dreams". Their CSG trees "were entirely right leaning, meaning they were a
  simple list". Edits were limited "to exclude domain deformation or any
  non-local effects like blur (much to the chagrin of z-brush experienced
  artists)". The reason was distribution: "the goal of dreams is for UGC to be
  minimal size to download, so we can't store the SDF fields themselves anyway -
  we need a fast evaluator!" Puck's choice (the SDF program as the source, baked
  products as caches) is the same choice.
- **The evaluator ("CS of doom")**: "The full pipeline from edit list to
  renderable data is 40+ compute shaders ... a few 3000+ instruction shaders
  chained together". Dispatch counts for one model: eval 60, sweep 91, points 459,
  bricker 73. Brute force would be "100 billion evaluations, which is too many".
  Hierarchical culling with max-norm bounds and interval arithmetic reaches
  "culling rates compared to brute force ... well over 99%. we get 10m - 100m
  voxels evaluated per second on a ps4, from a model with tens of thousands of
  edits". Test models ranged from 600 to 53,000 edits (worst about 120k). On
  soft blend: "Soft blend breaks ALL THE CULLING". It must "revert to hard min/max
  once distance fields are sufficiently far apart".
- **The final renderer ("Engine 4: the splats")**: "EVERYTHING is going to be a
  point cloud. the SDF becomes an intermediate representation, we use it to spawn
  the points at evaluation time". A grid of about 900³ voxels gives about 2 million
  surface points, sorted in Hilbert order into clusters of about 256 points. Each
  point is "one dword big, storing bitpacked pos,normal,roughness, and colour",
  with "completely independent cluster sets for each LOD". Rendering is "edits
  ->eval-> point cloud clusters in BVH -> LOD cut selection -> clusters splatted
  via atomics to 64 bit z+id buffer -> gbuffer -> classic deferred lighting ->
  heavy TAA".
- **Performance number**: "this scene shows 28.2M point splats, which takes
  4.38ms, so thats about 640 million single pixel splats per second" (PS4,
  1080p). LOD targets "a 1 splat to 1 screen pixel rate", with Russian roulette
  reducing a cluster "from 256 smoothly down to 25%". Transparency is "pure
  stochastic transparency ... let TAA sort it out", and the talk admits TAA
  history "starts breaking down horribly with stochastic alpha".
- The compute splatter "beats the rasterizer due to not wasting time on all the
  alpha=0 pixels".
- Lighting was "pretty vanilla": deferred shading, imperfect shadow maps, "a
  budget of 64 small (128x128) shadowmaps", and z-buffer AO extended by 1-bit
  voxel cascades.

**Disagreement or uncertainty:** the slides are 2015 pre-production. I found no
primary source on the renderer Dreams shipped in 2020. Secondary forum accounts
(Linen/Future of Coding archive, Habr comments) say the shipped engine keeps
cached 8³ SDF volumes plus point splats, but that page returned HTTP 429 and was
not opened. **Unverified.**

### How users express looks without writing shader code

From the official Dreams manual (docs.indreams.me, Media Molecule's user docs):

- **Style mode** (https://docs.indreams.me/en/create/resources/edit-mode-guide/style/tools):
  *Apply Fleck* changes the fleck type, and "the change is gradual, so stopping
  partway produces a mix of two fleck types". *Looseness* sets how loose the
  flecks are. *Impasto* makes flecks swell and layer. *Comb* sets the direction
  flecks lie. *Ruffle* will "randomise the directions of the individual flecks".
  *Neatness* lines flecks up on gridlines. Every tool is applied with a volume
  brush at a chosen rate.
- **Effects mode** (https://docs.indreams.me/en/create/resources/edit-mode-guide/effects/tools):
  Boil, Flow, Wave, Evaporate and Throb, each with an intensity that builds while
  held and a rate dial. Effects "work better when your flecks are looser" and
  combine with Style and Coat (https://docs.indreams.me/en/create/resources/edit-mode-guide/effects).
- **Coat** changes surface properties ("shininess, waxiness, or metallic
  finish"), per the Dreams docs as summarised by search. The Coat page itself
  was not opened. **Partly unverified.**
- **Budget made visible**: the Thermometer page (https://docs.indreams.me/en/help/thermometer)
  names three thermometers, gameplay, graphics ("graphics memory") and audio,
  and asks creators to keep levels low-cost. It does not say how the
  thermometers are computed.

**What Dreams shows:** a large creative community built a wide range of looks
(realistic forests and carpets were reported by the press) on a fixed renderer
whose user controls are *parameters painted spatially onto the surface
primitives*: fleck type, looseness, direction, animation. No user shader code
was involved. Cost is bounded because users can only change *data* that a fixed,
engine-costed pipeline consumes, and the thermometer makes the capacity visible.

---

## 2. Other user-content platforms: how each bounds shading cost and safety

| Platform | Authored shading allowed? | How cost and safety are bounded | Source |
|---|---|---|---|
| **Roblox** | No shader code is documented. Creators get PBR texture maps through `MaterialVariant` (tileable) and `SurfaceAppearance` (UV-mapped), plus studs-per-tile and pattern. "base material shaders differ from the MaterialVariant shader, so you can't create custom materials that look exactly like base materials." | Fixed engine shaders; users supply data only. | https://create.roblox.com/docs/parts/materials (©2026); a DevForum shader request (https://devforum.roblox.com/t/material-graph-based-custom-shaders/236718/5) shows it is a long-standing ask. No official "never" statement found. |
| **Core (Manticore, UE4)** | `CustomMaterial` is a base material plus named properties (`SetProperty`, `GetPropertyNames`, `GetBaseMaterialId`). The example is `rotate_material`. No shader authoring is documented. | Parameterised engine materials. | https://docs.coregames.com/api/custommaterial/ (no date). The absence of shader authoring is inferred from the API, not stated. |
| **Rec Room Studio (Unity)** | Only the "Rec Room Studio/*" shaders. "Material ... uses an unsupported shader ... Edit this material to use one of the Rec Room Studio shaders." Linear fog only, no baked GI. | A shader whitelist enforced at validation. | https://rrstudio.pages.dev/docs/Troubleshooting/validation, a *community* manual, not official. The official forum (forum.rec.net) did not resolve. **Treat as secondary.** |
| **VRChat (Unity)** | PC: arbitrary Unity shaders on avatars and worlds. Android/Quest avatars: "VRChat on Android or Quest only permits the shaders provided with the latest SDK on avatars", stated to be "in the interest of performance, user safety, and discouraging malicious behavior." | (a) The **Safety system** per trust rank lets a viewer block another user's shaders: "When disabled, all shaders on a user's avatar are reverted to Standard", and it exists to protect users from "nuisance users using things like screen-space shaders". (b) The **fallback system**: authors tag a `VRCFallback` (Unlit, Toon, Hidden ...); "Hidden ... useful for things like raymarching effects." (c) The **Performance Ranking** counts triangles, material slots, texture memory, particles, PhysBones and lights. **Shader cost is not a ranked stat**; shaders appear only in optimisation tips ("Use lower-intensive shaders"). | https://docs.vrchat.com/docs/vrchat-safety-and-trust-system (updated 2025-11-13); https://creators.vrchat.com/avatars/shader-fallback-system/; https://creators.vrchat.com/platforms/android/quest-content-limitations/; https://wiki.vrchat.com/wiki/Avatar_Performance_Ranking |
| **Shadertoy / WebGL** | Arbitrary fragment shaders. | No cost bound beyond the browser's process isolation. On an ANGLE issue about a shader that "takes too long" and crashes the GPU process on M1, Chrome's Ken Russell replied (2021-03-03): "Chrome's GPU process *is* the process isolation which insulates the browser from badly behaving shaders ... This is working as intended." WebGL 1 did impose static loop shapes (section 3). | https://groups.google.com/g/angleproject-review/c/1alWXxELtZw |
| **Godot (engine, as a contrast)** | Its own GLSL ES 3.0-like language. | It does not bound cost: "in modern GPUs, an infinite loop can exist and can freeze your application (including editor)... Godot can't protect you from this." Out-of-range indexing: "the shader will crash and break rendering." | https://docs.godotengine.org/en/stable/tutorials/shaders/shader_reference/shading_language.html |

**VRChat's experience in its users' words** (anecdotal, r/VRchat Jan 2025 and
Steam discussions found by search; the Reddit mirror was not opened, so
**unverified**): malicious "crasher" shaders keep the GPU busy until the client
stalls, and can be timed to activate after upload review. The standard advice is
to block shaders for non-friends, which also breaks legitimate looks. The
official docs above confirm the mechanism (per-user blocking with fallback) and
the platform split (a whitelist on mobile), without naming crashers.

**Pattern across platforms:** every large UGC platform except PC VRChat (and
Shadertoy, which has no budget at all) stops users from shipping raw shader
code to other players. Instead they expose parameterised engine materials. The
one that allows raw code has had to add per-viewer blocking, an author-supplied
fallback, and a mobile whitelist, and still does not count shader cost in its
performance rank. Dreams sits apart: a fixed SDF-native renderer with rich
*spatial* parameters and a visible budget.

---

## 3. Safety of author code on the GPU

### Timeouts and what a hang costs

- **Windows (WDDM TDR)**, from "WDDM Support for Timeout Detection and Recovery",
  https://learn.microsoft.com/en-us/windows-hardware/drivers/display/timeout-detection-and-recovery
  (ms.date 2025-11-05):
  - "The default timeout period in Windows is two seconds. If the GPU can't
    complete or preempt the current task within the TDR timeout period, the OS
    diagnoses that the GPU is frozen."
  - Recovery "purges all allocations from video memory". Applications "must
    release and then re-create its Microsoft Direct3D device and all of the
    device's objects."
  - Escalation: "the OS bug-checks the computer on the sixth (or more) GPU hang
    when it detects that five (5) or more GPU hangs (0x117) and subsequent
    recoveries occur within one (1) minute."
  - Engine timeouts (0x141) block GPU access for the offending process (0x142),
    "In this way, the malfunctioning process doesn't bug-check the system."
  - Registry keys (https://learn.microsoft.com/en-us/windows-hardware/drivers/display/tdr-registry-keys):
    TdrDelay default 2 s, TdrDdiDelay 5 s, TdrLimitTime 60 s, TdrLimitCount 5.
    "End users shouldn't manipulate these registry keys. Applications
    shouldn't..." Raising them is not a fix, and Puck must not ask players to do
    it.
  - **A repeatedly hanging authored shader can blue-screen the machine.** That
    makes it a host-citizenship problem (the mandate "plays nice with its host"),
    not only a crash of Puck.
- **Linux (amdgpu, which the Steam Deck runs)**, from
  https://docs.kernel.org/gpu/amdgpu/module-parameters.html: `lockup_timeout`:
  "By default(with no lockup_timeout settings), the timeout for all queues is
  2000" ms. `gpu_recovery`: "The default is -1 (auto, disabled except SRIOV)".
  **Disagreement or evolution:** an amd-gfx patch from June/July 2025 ("make
  compute timeouts consistent",
  https://lists.freedesktop.org/archives/amd-gfx/2025-July/127452.html, seen
  via search snippet, not opened) describes the default as 10000 ms and moves
  compute from 60 s to 10 s. The current kernel doc says 2000 ms. The value
  depends on the kernel version, and the `gpu_recovery` text may be stale.
  Puck should treat "about 2 s, maybe 10 s, recovery not guaranteed" as the
  Linux envelope.
- **Linux DRM's contract** (https://docs.kernel.org/gpu/drm-uapi.html, "Device
  reset"): "Device resets can not be disabled without tainting the kernel".
  After a reset, the "KMD should reject new command submissions for affected
  contexts", and "Apps using Vulkan should check for VK_ERROR_DEVICE_LOST ...
  it needs to recreate the contexts to keep going."
- **Diagnostics**: D3D12 DRED (https://learn.microsoft.com/en-us/windows/win32/direct3d12/use-dred,
  updated 2025-03-11) puts auto-breadcrumbs after every Draw, Dispatch and Copy,
  and reports page faults. "Empirical measurements show 2-5% performance loss on
  a typical AAA Direct3D 12 graphics game engine. For this reason,
  auto-breadcrumbs are off by default." Page-fault causes listed include "A
  shader indexes beyond the end of a root binding", and on some GPUs memory
  faults are "bit-bucket writes; reading simulated data ... or ... simply
  hanging." Puck's relight hang was found this way.

### Validation and bounding loops and work

- **WGSL** (W3C CRD, 2026, https://www.w3.org/TR/WGSL/):
  - "functions must not be recursive, either directly or indirectly", and "It
    is a shader-creation error if any module scope declaration is recursive."
  - Loops are not statically bounded: "A dynamic error occurs if the loop would
    execute an unbounded number of iterations. This may result in early
    termination of the loop, other non-local effects, or even device loss."
    Only "obviously infinite loops" (an empty behaviour set) are rejected at
    compile time.
  - An out-of-bounds access "generates a dynamic error". Allowed outcomes include
    a Trap ("The shader invocation immediately terminates, and shader stage
    outputs are set to zero values") or loads from other locations in the *same
    bound buffer*.
  - Uniformity analysis rejects derivatives, barriers and subgroup operations it
    cannot prove are in uniform control flow (diagnostics `derivative_uniformity`
    and `subgroup_uniformity`). This is about correctness, not cost.
- **WebGPU** (W3C CRD, 2026, https://www.w3.org/TR/webgpu/ § 2.1): a
  "watchdog" so an application does not cause "GPU unresponsiveness for more
  than a few seconds". Memory accesses are confined by "robust buffer access"
  or inserted bounds checks, and all resources start zeroed. **The web's answer
  to cost is the watchdog and device loss, not static bounds.**
- **GLSL ES 1.00 Appendix A** (Rev. 17, 2009-05-12,
  https://registry.khronos.org/OpenGL/specs/es/2.0/GLSL_ES_Specification_1.00.pdf):
  "control flow is limited to forward branching and to loops where the maximum
  number of iterations can easily be determined at compile time." A `for` loop
  needs a constant init, the condition `loop_index relop constant_expression`,
  a constant step, and an index not assigned in the body. **WebGL 1 made this
  mandatory** (https://registry.khronos.org/webgl/specs/latest/1.0/): "for loops
  must conform to the structural constraints in Appendix A. while and do-while
  loops are disallowed". It is the one shipped precedent for *statically bounded*
  author shaders. The lesson (my inference) is that a constant bound proves
  termination, not affordability: `for (i = 0; i < 1000000; i++)` is legal.
  Termination is necessary, but the budget needs a *cost* bound as well.
- **Vulkan robustness** (`VK_EXT_robustness2`, now `VK_KHR_robustness2`,
  https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_robustness2.html):
  "out of bounds writes must be discarded, out of bound reads must return zero",
  and "Accesses to null descriptors have well-defined behavior". The page says
  nothing about timeouts. Robustness bounds *memory*, not *time*.

### What this means for an authored shader in Puck

1. **Memory safety** comes from the language and the binding model. The authored
   code never sees a raw descriptor, a buffer address or a root binding. It
   reads a fixed interface (the shading inputs) and writes a fixed output. A
   language that has no pointers and no indexing into engine buffers cannot
   fault.
2. **Termination** comes from the language: no recursion (as in WGSL) and only
   statically bounded loops (as in GLSL ES Appendix A).
3. **Cost** must be bounded statically, because no platform's runtime protects
   the frame. TDR and amdgpu are measured in *seconds*, and the 60 FPS bar is
   16.7 ms. A static cost (instructions times trip counts, as the SDF ISA
   already allows) charged per covered pixel in `world.budget` is something none
   of the surveyed platforms has. VRChat's ranking explicitly lacks it.
4. **Work size** must be cut by predicted cost, as the relight fix
   (`RelightRaysPerJob`) now does. An authored shading pass that multiplies
   lights, samples or steps must have its dispatches cut the same way, so that
   no single submission approaches the 2 s window even under a pathological
   author.
5. **Recovery stays the backstop**, not the plan: Puck's device-loss recovery
   holds on both backends (the device-loss recovery commit), and DRED names the culprit. A
   repeated loss within a minute escalates to a bug check on Windows, so a
   shader that hangs once must be quarantined (falling back to the neutral
   default material) before it can hang again. VRChat's fallback tag is the
   precedent, applied automatically.

---

## 4. Shading languages and toolchains: one source, two backends

- **HLSL through DXC** (https://github.com/microsoft/DirectXShaderCompiler/blob/main/docs/SPIR-V.rst):
  "DirectXShaderCompiler is the reference compiler for HLSL. Adding SPIR-V codegen
  ... will enable the usage of HLSL as a frontend language for Vulkan ...
  developers will also have a unified compiler toolchain for targeting both
  DirectX and Vulkan." The document also notes "semantic gaps": "certain HLSL
  features do not have corresponding mappings in Vulkan". This is Puck's current
  path (DXIL and SPIR-V at build time).
- **Direction of travel**: "DirectX Adopting SPIR-V as the Interchange Format of
  the Future", 2024-09-19, Bieneman and Hoef,
  https://devblogs.microsoft.com/directx/directx-adopting-spir-v/: "Shader Model
  7.0 will adopt SPIR-V as its interchange format", "This transition will take
  several years", with translators between SPIR-V and DXIL in both directions.
  For Puck, one SPIR-V artifact for both backends becomes possible later, so a
  design that keys caches on the source and the target is not wasted.
- **Slang**:
  - Paper: He, Foley, Fatahalian, "Slang: Language Mechanisms for Extensible
    Real-time Shading Systems", SIGGRAPH 2018-08-12,
    https://research.nvidia.com/publication/2018-08_slang-language-mechanisms-extensible-real-time-shading-systems.
    It claims that generics with interface constraints, associated types and
    extensions support extensible shading systems "without domain-specific
    tools", and that the rearchitected renderer was "simpler, easier to extend,
    and renders faster than the original HLSL-based implementation".
  - Governance: Khronos-hosted since November 2024, Apache 2.0
    (https://www.khronos.org/news/permalink/khronos-group-launches-slang-initiative-hosting-open-source-compiler-contributed-by-nvidia,
    seen via search; the GitHub org page states it).
  - Targets (https://shader-slang.org/slang/user-guide/targets.html): D3D12 DXIL,
    Vulkan SPIR-V, Metal (work in progress), WebGPU WGSL (work in progress), CUDA
    and CPU. The DXIL route goes through DXC as a "downstream compiler"; the
    slangc docs (per search) list `-Xdxc` pass-through and loading
    dxcompiler/dxil. The slangc page itself returned 404 when opened, so this is
    **partly unverified**.
  - Interfaces and generics (https://shader-slang.org/slang/user-guide/interfaces-generics.html):
    the compiler "always attempts to determine the actual type of an
    interface-typed value at compile time". If it cannot, it "will generate code
    that performs a dynamic dispatch based on the concrete type of the value at
    runtime", which "requires the concrete type to have no opaque fields such as
    resources". Generic code is type-checked against its constraints before
    specialization, unlike C++ templates.
  - Link-time specialization (https://shader-slang.org/slang/user-guide/link-time-specialization.html):
    `extern static const` and `extern struct Sampler : ISampler`, resolved by an
    `export` in another module. Modules are precompiled once into
    `.slang-module`, and linking specializes. Preprocessor specialization "redo[es]
    almost all work from scratch with every specialized variant", while link-time
    specialization reuses parsing, checking and IR. Slang's freshness check
    (`isBinaryModuleUpToDate`) is the same "identity from inputs" idea as Puck's
    caches.
  - **Relevance:** Slang's static and dynamic dispatch over one interface is
    Dolphin's specialized/ubershader pair expressed in the language. A material
    `IMaterial` either specializes into a per-material kernel or compiles to one
    dynamic-dispatch kernel (an ubershader), from one source.
- **MaterialX** (https://materialx.org/, ASWF-hosted since 2021-07-14): an open
  standard for node-graph materials with shader generation to GLSL/ESSL, MSL,
  OSL, MDL, WGSL (v1.39.4, 2025-09-15) and Slang (v1.39.5, 2026-05-22). It shows
  that the industry's portable *authoring* format is a restricted graph lowered
  to many languages, not raw shader source.
- **WGSL** as an authoring language: memory-safe and recursion-free, but loops
  are only dynamically bounded (section 3). It has a single portable spec, yet
  Puck would still lower it to DXIL and SPIR-V (through Tint or Naga), adding a
  third front end. I found no source showing a WGSL-to-DXIL path that beats
  HLSL-through-DXC; **not researched further**.

### Raw HLSL versus a restricted authoring language

| Concern | Raw HLSL from authors | Restricted Puck language (IR lowered to HLSL) |
|---|---|---|
| Costing in `world.budget` | Needs a DXIL/SPIR-V analysis per backend after compilation. Loops and texture fetch counts are not knowable in general. | Static: ops times bounded trip counts, as the SDF ISA already allows. The cost is attributed to the author's rows. |
| Termination / TDR | Not guaranteed (Godot: "can't protect you"). | No recursion, statically bounded loops (GLSL ES Appendix A precedent), plus a cost cap. |
| Memory safety | Depends on robustness modes and binding discipline. Root-binding overreads fault (DRED). | No raw resource access by construction. |
| No simulation access (determinism) | Must be enforced by binding policy. | The interface stamp contains no simulation state. |
| Determinism of the compile and cache identity | The DXC version, flags and backend all enter the identity. | IR to HLSL is Puck's own deterministic lowering. The identity is the IR plus the compiler version, as for every derived product. |
| Both backends | The DXC SPIR-V "semantic gaps" leak to authors. | Puck's lowering owns the gaps once. |
| "Ergonomic is optimal" | Spelling affects cost (the compiler sees text). | Many spellings lower to one canonical IR, matching the mandate. |
| Expressiveness | Unlimited, including everything that breaks the bar. | Bounded by the op set. Grows by adding ops (the engine's own layers become ops and authored programs). |
| Hot reload under 30 s | Requires a DXC compile per edit (seconds) plus a PSO. | An interpreted (ubershader) path shows an edit as *data* immediately; a specialized compile follows in the background (section 5). |

---

## 5. Dolphin's ubershaders (added at the lead's request)

### The problem

From "Ubershaders: A Ridiculous Solution to an Impossible Problem", JMC47 and
MayImilae, 2017-07-30 (last updated 2023-07-06),
https://dolphin-emu.org/blog/2017/07/30/ubershaders/. The live site blocks
automated fetches, so it was read from the Wayback copy at
https://web.archive.org/web/2024/https://dolphin-emu.org/blog/2017/07/30/ubershaders/.

- The GameCube/Wii TEV is configured by the game and used instantly: "There is
  no preloading of the TEV configurations whatsoever". Dolphin must turn each
  configuration into a specialized host shader, and when a compile takes longer
  than a frame, "the game will visibly stop".
- "There are roughly 5.64 × 10^511 potential configurations of TEV unit alone",
  so precompiling every one is impossible. Prediction is infeasible, and shared
  UID caches fail because of little overlap between games (Wind Waker and
  Twilight Princess share about 15%), because "Dolphin is still improving", and
  because cached shaders are invalidated by "a GPU change, GPU driver update, or
  even going to a new Dolphin version".
- Tino's asynchronous fork "simply skips rendering the object". It was rejected
  for master because some effects render once (Mii heads drawn once to the EFB
  "will not show up for the remainder of the game").

### The solution and its modes

- "emulate the rendering pipeline itself with an interpreter that runs directly
  on the GPU as a set of monsterous flexible shaders". phire's 2015 prototype
  surprised the team: "GPUs shouldn't really be able to run these at playable
  speeds, but they do". Stenzek finished it in 2017.
- The modes in current Dolphin source
  (`Source/Core/VideoCommon/VideoConfig.h`, `enum class ShaderCompilationMode {
  Synchronous, SynchronousUberShaders, AsynchronousUberShaders,
  AsynchronousSkipRendering }`; GUI labels and help text in
  `Source/Core/DolphinQt/Config/Graphics/GeneralWidget.cpp`, both fetched from
  github.com/dolphin-emu/dolphin master in the research round):
  - **Specialized (Default)**: "Ubershaders are never used. Stuttering will occur
    during shader compilation, but GPU demands are low. Recommended for low-end
    hardware. If unsure, select this mode."
  - **Exclusive Ubershaders**: "Ubershaders will always be used. Provides a near
    stutter-free experience at the cost of very high GPU performance
    requirements." A comment notes the "very powerful GPU" wording is "by 2021 PC
    GPU standards".
  - **Hybrid Ubershaders**: "Ubershaders will be used to prevent stuttering
    during shader compilation, but specialized shaders will be used when they will
    not cause stuttering ... results depend on video driver behavior." The blog
    describes it as: "whenever a new pipeline configuration appears, Dolphin will
    use the already compiled Ubershaders to immediately render the effect without
    stuttering while still compiling the specialized shader in the background.
    Once the specialized shader is done, Dolphin will then hand the objects
    rendering through the Ubershader over".
  - **Skip Drawing**: "Prevents shader compilation stuttering by not rendering
    waiting objects ... at the cost of introducing visual glitches and broken
    effects. Not recommended". This is Tino's idea, eventually merged as the last
    resort.
  - Plus **Compile Shaders Before Starting** (`bWaitForShadersBeforeStarting`),
    which waits for queued compiles before boot.

### Performance across GPU classes (2017 post)

- "At 1x Internal Resolution (480p) most dedicated GPUs should be able to get the
  job done, with higher end cards still able to push 1080p or higher even
  exclusively using Ubershaders."
- Integrated: "onboard solutions at best could get roughly 50% speed with
  Ubershaders at 1x IR in a typical 3D game!" Intel: "Exclusive Mode does work,
  but Intel iGPUs are currently not fast enough to run it at fullspeed even at 1x
  native."
- Hybrid cost: the July/August 2017 progress report (per search snippet,
  https://alwaysdata.dolphin-emu.org/blog/2017/09/02/dolphin-progress-report-july-and-august/,
  not opened, behind the same bot wall) says "a GTX 760 may only get 2x IR on
  Exclusive Mode Ubershaders, but, with Hybrid Mode Ubershaders, performance is
  identical to that of Ubershaders being off". **Snippet only.**
- Android: NVIDIA Shield "Exclusive can actually get full speed in very basic
  games". PowerVR: "graphical corruption from shader compilation errors ... Too
  slow". Adreno: "Hybrid Mode will crash and Exclusive Mode show severe graphical
  corruption". Mali: "Not tested. We can safely say it wouldn't end well".
- macOS (OpenGL 4.1 at the time): "Exclusive is too slow to be useful", and there
  was no shader cache.

### Driver problems the large shaders exposed (the post's "Hall of Shame")

- Pipeline variants: "When we have to generate a new pipeline for a different
  blend or depth states, some drivers aren't smart enough to share the shaders
  between pipelines". Mesa was the exception.
- NVIDIA: OpenGL/Vulkan "Compiled Shaders ... are Much Slower than D3D". On a GTX
  760, 1x IR on GL/Vulkan versus "double or even triple" on D3D for the same
  shader, and there was no disassembly to debug it ("they only provide shader
  disassembly tools for Direct3D 12 (under NDA)").
- AMD Vulkan lacked a shader cache (fixed during writing), which meant
  recompiling the ubershaders on every run. Intel i965 GL did not share shaders
  between threads, so Hybrid still stuttered.
- Changing settings that affect the ubershader (per-pixel lighting, AA) "will
  require different ubershaders to be compiled and may cause a sizable pause".

### Later developments

- **Dolphin Progress Report July/August 2022**
  (https://web.archive.org/web/2023/https://dolphin-emu.org/blog/2022/09/13/dolphin-progress-report-july-and-august-2022/):
  the newer APIs "have pipelines ... that ... only use what is necessary to
  simplify shaders", so pipeline compiles came back. "Dolphin actually had too
  many configurations to precompile every pipeline configuration." The fix
  "sacrific[ed] performance and simplif[ied] the Ubershaders to only use a single
  method for blending": FrameBuffer Fetch "for almost all blend unit
  configurations". The same report notes the new Metal backend (TellowKrinkle).
- **Dolphin Progress Report Sept–Nov 2022**
  (https://web.archive.org/web/2023/https://dolphin-emu.org/blog/6J):
  "Dynamic Vertex Loaders for Ubershaders" (5.0-17403). Some drivers fold
  "hardware vertex loader settings into the actual shader, which essentially
  makes our Ubershaders not so Uber anymore". The fix reduced the pipeline count
  "so much that now nearly every pipeline is able to be precompiled at boot".
  Results: Metal "no known drivers" stutter; D3D12 on NVIDIA/AMD matches D3D11's
  stutter-free experience; Vulkan improved, but "NVIDIA still tends to have a few
  noticeable compiles", and `VK_EXT_rasterization_order_attachment_access` would
  help. On the **Steam Deck**: "you can even throw on Hybrid Ubershaders and get
  a pretty smooth experience on par with a mid-range desktop!"

**Disagreement between Dolphin's own sources:** the 2017 post expected Hybrid
"to be the most commonly used Ubershader mode". Current source keeps
**Specialized as the default**, "Recommended for low-end hardware", with Hybrid
qualified as "results depend on video driver behavior". The 2022 reports show
the remaining problem was never the interpreter itself. It was *pipeline state
outside the shader* (blend, vertex format) that drivers specialize on behind the
interpreter's back.

### Weighing Dolphin's hybrid pattern against Puck

What transfers:

1. **Puck already has Dolphin's ubershader.** The SDF ISA interpreter (27 ops
   over a word stream) and the material-table shading kernels are interpreters
   over closed configuration spaces. A new shape or material is *data*, so there
   is no compile stutter today, as Dolphin's exclusive mode promised.
2. **Hybrid is the right shape for authored shading under the 30 s edit bar.**
   An authored shading program first runs *interpreted*: an op stream in the
   material table, read by a generic shading interpreter, giving an immediate
   view with no compile. A specialized kernel (IR lowered to HLSL, then DXC to
   DXIL and SPIR-V) is derived in the background on a low-priority thread,
   identified from its inputs (IR, compiler version, target) as a cache entry,
   counted on a miss, and swapped in when ready. That meets "derived products are
   caches" and "a runtime compile must not stall the frame" together.
3. **The swap must be invisible.** Dolphin made "rendering identical to the
   specialized shaders" an explicit work item, and AGENTS.md demands that a
   second path give "the same result within the still tolerance". Since both
   paths lower from one IR, the interpreter and the specialized kernel must be
   generated from the *same* op definitions, the way `sdf-isa.hlsli` is
   generated from the C# `SdfOp` model, never hand-written twice.
4. **Fix pipeline state.** Dolphin's 2022 lesson: anything the driver
   specializes on outside the shader (blend modes, vertex formats, render-target
   formats) breaks the scheme. Puck's authored shading must not create PSO
   variants. Blend, depth and formats stay engine-owned and few, and authored
   programs vary only shader *data* (or, once specialized, the shader alone,
   with every PSO precompilable at boot as Dolphin achieved).

Where Puck differs:

5. **Open-ended programs versus a finite configuration.** TEV is huge (5.64 ×
   10^511) but has a *fixed shape* (16 stages of a fixed function), so one
   interpreter covers it at bounded cost per pixel. Authored programs are
   open-ended only if the language is. If Puck defines a bounded op set with
   static trip counts, the interpreter's per-pixel cost is bounded by the
   program's static cost, which `world.budget` can charge. The open-endedness
   then moves into *program length*, which is costed, rather than into *kinds of
   operation*, which grow only by engine (or authored-op) extension.
6. **The interpreter's cost lands on the frame bar, not just on iGPUs.**
   Dolphin's exclusive mode ran at about 50% speed on integrated GPUs at 480p.
   Puck's bar includes the Steam Deck (an APU) at 45 FPS at the highest quality,
   and the `views` pass is already 4.3 ms at the worst stops, with high register
   allocation reported by PIX. An interpreter's register allocation is the
   *maximum over all ops it could execute*. Every op added to a single shading
   ubershader raises occupancy pressure for every pixel, even pixels that never
   use the op. So the interpreter cannot be the steady state for heavy authored
   materials. Hybrid (specialize, then swap) is required, not optional. A
   Puck-native refinement (my inference, not from a source) is to classify pixels
   by material program and dispatch each class with its own kernel. The
   interpreter then only serves the classes still compiling, the same "a fraction
   of the objects ... only for frames at a time" that made Dolphin's Hybrid
   cheap.
7. **Puck knows its programs ahead of time.** Dolphin cannot see the future;
   Puck can. A world's authored shading programs are in its documents at load,
   so a player build can ship the specialized kernels prewarmed (allowed by the
   mandate), and a dev build can specialize at load. The interpreter then covers
   only *edits* (hot reload) and cache misses, which is far less exposure than
   Dolphin's. Dolphin's cache-invalidation pain (GPU, driver and version
   changes) maps onto Puck's input-identified cache: the driver's own
   PSO/pipeline cache is the only part keyed on things Puck does not control.
8. **Skip Drawing is not acceptable for Puck** (a quality reduction, and Dolphin
   itself shows its correctness failures). The neutral default material is the
   fallback only for a *quarantined* program (after a device loss or a
   validation failure), never for "still compiling".

---

## What this means for Puck

Borrowed techniques, weighed:

- **Do not accept raw HLSL from authors as the authoring surface.** Every UGC
  platform surveyed either forbids it (Roblox, Rec Room, Core, VRChat on mobile)
  or ships it with per-viewer blocking and still cannot count its cost (VRChat
  PC). Raw HLSL cannot be costed in `world.budget`, cannot be bounded against a
  2 s TDR (Godot: "can't protect you"), leaks DXC's DXIL/SPIR-V semantic gaps to
  authors, and lets spelling affect cost, which contradicts "ergonomic is
  optimal". If an escape hatch is wanted for engine developers, it is an
  *engine-op extension* (a new op with a declared cost, generated into both the
  interpreter and the specializer), not an author document feature.
- **Take Dreams' lesson about expression.** Users made a huge range of looks with
  zero shader code, because looks were *spatially painted parameters* (fleck
  type, looseness, comb direction, ruffle, effects like boil, flow and wave) on
  an SDF-derived surface, under a visible budget. Puck's paints, weathering,
  flow and layers are already this kind of system. Authored shading should first
  extend it with *authored programs over the surface interface* (inputs: position,
  normal, material lanes, paint coverage, time, light terms; output: the shading
  response), rather than with a second shading system.
- **Take GLSL ES Appendix A's static loop shape and WGSL's no-recursion rule**,
  and add what neither has: a static cost bound charged per covered pixel. The
  SDF ISA shows Puck can do this. It is the one capability no surveyed platform
  has.
- **Take Dolphin's hybrid**, modified as section 5 sets out: interpret
  immediately (edit to running in seconds), specialize in the background
  (identified from inputs, counted, prewarmed in player builds), swap
  invisibly, keep PSO state engine-owned so the driver cannot re-specialize
  behind Puck's back, and never skip drawing.
- **Consider Slang as the specialization target, or as a model.** One `IMaterial`
  interface compiled either to dynamic dispatch (an ubershader) or to static
  specialization, plus link-time specialization of precompiled modules, is the
  hybrid expressed in a language. It also targets DXIL (via DXC), SPIR-V and
  WGSL. The alternative, which fits "Puck code over engine code" better, is
  Puck's own IR lowering to HLSL through the existing DXC path, with the
  interpreter generated from the same op table (`puck shaders generate`
  already does this for the ISA). The SM7 move to SPIR-V makes either choice
  safe long term.

Puck-native combinations that no surveyed system has:

1. **Shading programs as SDF-VM programs.** The op-stream interpreter,
   generated-HLSL and costing machinery that runs SDF fields can run shading
   programs. An authored material is a program in the palette's word stream,
   interpreted at once and specialized as a derived product. The engine's own
   layers (weathering, flow, toon, paint) become the first such programs,
   satisfying "never a second path".
2. **Cost in `world.budget` before the GPU sees it.** Static program cost times
   predicted coverage (from the mesh pass's classification) gives each
   authored material a priced line. Dispatches are cut by predicted work (the
   `RelightRaysPerJob` pattern generalised), so no author can build a 2-second
   submission.
3. **Change-proportional specialization.** Only changed programs are
   re-specialized (identity from inputs). Unchanged ones hit the cache, and the
   interpreter covers only the edited material for the seconds its compile
   takes.
4. **Quarantine on device loss.** DRED (when enabled) or the in-flight job line
   names the program, which falls back to the neutral default until edited,
   preventing the five-hangs-in-60-s bug check.
5. **Determinism is untouched.** Shading programs read the presentation
   interface only. The interface stamp contains no simulation lanes, which
   satisfies the determinism mandate by construction rather than by policy.

### Not verified or open

- The shipped (2020) Dreams renderer: primary source not found; secondary
  accounts not opened.
- How the Dreams thermometer is computed.
- The current amdgpu default timeout across kernel versions (2000 ms per
  current docs versus 10000 ms per a 2025 patch, not opened) and the
  `gpu_recovery` default on bare metal.
- Official VRChat statements about crasher shaders (only community reports
  found).
- Whether Slang emits DXIL only through DXC (the slangc page returned 404).
- A Rec Room primary source (the official forum did not resolve).
- The Dolphin 2017 progress report's Hybrid performance numbers (snippet only).
