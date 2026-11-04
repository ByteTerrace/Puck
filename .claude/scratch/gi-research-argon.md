# Far-Field Interior Irradiance: Preventing Coarse Probe Leaks Across Thin Walls and Storeys

## 1. Exact Anatomy of the Far-Wall Failure in Puck

### 1.1 Clipmap Geometry vs. Architectural Scale

Puck's mesh global illumination stores three nested toroidal probe levels (`SdfMeshIrradianceProbe.Levels = 3` in `src/Puck.SdfVm.Model/SdfMeshIrradianceProbe.cs:20`), each packing `235` `uint` words (`940` bytes) per probe (`SdfMeshIrradianceProbe.ProbeWords` in `src/Puck.SdfVm.Model/SdfMeshIrradianceProbe.cs:11`):

| Level | High Tier Spacing & Grid | Medium Tier Spacing & Grid | World Window (`X × Y × Z`) | Surface Reach (`SurfaceReachOf`) |
|---|---|---|---|---|
| **Level 0** | `1.0 m`, `48 × 16 × 48` (`36,864` probes) | `2.0 m`, `32 × 12 × 32` (`12,288` probes) | High: `48 m × 16 m × 48 m`<br>Medium: `64 m × 24 m × 64 m` | High: `2.65 m`<br>Medium: `5.25 m` |
| **Level 1** | `8.0 m`, `32 × 12 × 32` (`12,288` probes) | `8.0 m`, `32 × 12 × 32` (`12,288` probes) | `256 m × 96 m × 256 m` | `20.83 m` |
| **Level 2** | `32.0 m`, `32 × 12 × 32` (`12,288` probes) | `32.0 m`, `32 × 12 × 32` (`12,288` probes) | `1024 m × 384 m × 1024 m` | `83.19 m` |

Key structural parameters in `src/Puck.SdfVm/SdfMeshIrradiance.cs` and `src/Puck.SdfVm.Model/SdfMeshIrradianceProbe.cs`:
- Coarser spacings are fixed at `LevelSpacing => [8f, 32f]` (`src/Puck.SdfVm/SdfMeshIrradiance.cs:110`), with grid dimensions `MediumDims = (32, 12, 32)` (`src/Puck.SdfVm/SdfMeshIrradiance.cs:171, 2477`). While a canonical clipmap doubles spacing at each level (`1 m → 2 m → 4 m → 8 m`), High tier currently jumps **8×** in spacing directly from Level 0 (`1 m`) to Level 1 (`8 m`). Even with dyadic `2 m` and `4 m` intermediate levels, any receiver beyond the `4 m` window falls onto the `8 m` level.
- In `worlds/debug/hall.puck:1-193`, the four-storey hall spans `x = 10 .. 70 m` (`60 m` long) and `z = 12 .. 40 m` (`28 m` wide), with storey height `4.5 m` (`y = 4.5k + 0.05` for `k = 0 .. 3`, roof at `18.05 m`), `0.3 m` exterior walls, and `0.3 m` floor slabs (`0.02 m` ceiling + `0.28 m` floor).
- In the recorded reproduction (`docs/plans/open-items.md:1778-1782`), the player stands near the east end (`body.pose 64 4.57 28.52 273`, camera around `(68, 6.8, 28.52)`) looking west toward `(10, 6.0, 28.52)`. Level 0's `48 m` X-window centres near the camera and extends `24 m` plus `SurfaceReachOf(1f) = 2.65 m` (`src/Puck.SdfVm.Model/SdfMeshIrradianceProbe.cs:40`, `src/Puck.SdfVm/SdfMeshIrradiance.cs:2106`), terminating near `x ≈ 37 m`. The far western third of the hall (`x = 10 .. 37 m`, including the far west wall at `x = 10.6, y = 6.5, z = 28.5`) lies outside Level 0's reach and is shaded by **Level 1 (`8 m` spacing)**.

### 1.2 Four Interacting Mechanisms Behind Pale Triangles and Dark Wedges

Inspection of `src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-mesh-irradiance.hlsli` and `src/Puck.SdfVm/SdfMeshIrradiance.cs` identifies four distinct mechanisms that combine at `8 m` spacing to produce the pale triangles (light leaks) and dark wedges (over-occlusion):

1. **Topological Cell Straddling (`8 m` cell vs. `4.5 m` storey and `0.3 m` wall/slab)**
   An `8 m × 8 m × 8 m` cell spans `1.78` storeys vertically (`8.0 / 4.5`) and `26.7×` the wall/slab thickness (`8.0 / 0.3`). For a receiver on the second-storey west wall (`x = 10.6, y = 6.5, z = 28.5`), the enclosing 8-corner trilinear cage (`low = floor((position - origin) / 8 - 0.5)`) straddles:
   - **Across the exterior wall in X**: probes at `x = 4 m` (outdoors in full `100,000 lx` direct sun and open sky) and probes at `x = 12 m` (indoors).
   - **Across floor slabs in Y**: lower corners at `y = 4 m` (first storey, below the `y = 4.55 m` slab) and upper corners at `y = 12 m` (third storey, above the `y = 9.05 m` slab). Neither Y layer of the `8 m` cage sits inside the second storey (`y = 4.55 .. 9.05 m`) where the receiver lives.

2. **Local Relocation Cannot Fix Free-Space Topological Straddling (`SdfMeshIrradiance.Place`)**
   Probe relocation in `SdfMeshIrradiance.Place` (`src/Puck.SdfVm/SdfMeshIrradiance.cs:1725-1756`) only relocates a probe when its cell centre is inside geometry or within `Clearance = 0.1 m` (`src/Puck.SdfVm.Model/SdfMeshIrradianceProbe.cs:26`). It searches a `0.25 * spacing` (`2 m` at Level 1) grid and `TryNearestSurface` exits within `MaxOffset = 0.45f * spacing` (`3.6 m` at Level 1).
   - A coarse probe at `x = 4 m` outdoors or at `y = 4 m` / `y = 12 m` on an adjacent storey is already in open air (`> 0.1 m` from any surface), so `Place` leaves it at `offset = (0, 0, 0)`.
   - Even when a probe centre lands inside a `0.3 m` slab or wall, `Place` pushes it to the nearest surface exit (`1.25 * Clearance = 0.125 m`), which has a 50% chance of being on the wrong side of the wall or slab relative to a given receiver.

3. **Angular Under-Sampling of the `12 × 12` Surface Map at `8 m` Range (Pale Triangles)**
   Every probe stores a `12 × 12` octahedral first-hit surface map (`ProbeSurfaceSide = 12`, `144` rays per probe in `src/Puck.SdfVm.Model/SdfMeshIrradianceProbe.cs:18`, traced by `SdfMeshProbeDepth.FillSurfaceMaps` in `src/Puck.SdfVm/SdfMeshProbeDepth.cs:186-298`) with first-hit distance, octahedral normal, and finite-difference curvature.
   - Each octahedral texel covers roughly `0.295 rad` (`~17°`). At Level 0 (`d ≈ 0.5 .. 1.5 m`), one texel projects to a `0.15 .. 0.44 m` patch—comparable to the `0.3 m` wall/slab thickness.
   - At Level 1 (`d ≈ 5 .. 14 m`, up to `SurfaceReachOf(8) = 20.83 m`), one octahedral texel projects to a **`1.5 m .. 4.2 m` footprint** on the receiver wall.
   - In `sdfMeshProbeDepthVisibility` (`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-mesh-irradiance.hlsli:71-165`), the shader evaluates `pushed = position + normal * min(0.05 * spacing, 0.05)` (`0.05 m` push) and bilinearly inspects the **4 octahedral texels** around `toward = pushed - probePoint`.
   - When an outdoor or adjacent-storey probe traces its `144` rays, rays that pass through a window opening, graze past a `0.3 m` slab edge, or hit two different surfaces across adjacent texels (failing the `DepthSurfaceJoinGap = 0.04 m` coplanarity check at `sdf-mesh-irradiance.hlsli:109`) cause `blocked` to fall below `DepthCoverageStart = 0.5` (`sdf-mesh-irradiance.hlsli:65, 164`).
   - Because octahedral mapping unfolds the sphere's 8 octants into triangles inside the `12 × 12` square (`sdfMeshProbeDepthOctUv` at `sdf-mesh-irradiance.hlsli:16-30`), the bilinear transitions between blocked and unblocked texels project onto the far wall as **sharp pale triangles** where outdoor or other-storey irradiance leaks through.

4. **Zero Back-Support (`BehindShare = 0`) + Binary Level-Selection Cliff (Dark Wedges)**
   - At `level > 0`, `BehindShare` is set to `0.0` (`sdf-mesh-irradiance.hlsli:262-265, 318`). Any coarse probe with `dot(probePoint - position, normal) <= 0` receives `facing = 0`. For the west wall at `x = 10.6` with `normal = (+1, 0, 0)`, all 4 coarse corners at `x = 4 m` have `facing = 0`, leaving only the 4 indoor corners at `x = 12 m` (`y = 4 m` on storey 1 and `y = 12 m` on storey 3; none on storey 2 at `y = 6.5 m`).
   - Those remaining 4 corners sit on the **wrong storeys** (`y = 4 m` below the floor slab and `y = 12 m` above the ceiling slab). Their rays toward `(10.6, 6.5, 28.5)` must cross the `y = 4.55 m` or `y = 9.05 m` floor slabs! Where the `12 × 12` surface map catches the floor slab, the curved/planar tangent reconstructor (`sdfMeshProbeDepthPatch` at `sdf-mesh-irradiance.hlsli:57-63`) projects a wide octahedral wedge of shadow onto the second-storey wall.
   - Worse, in `sdfMeshProbesAt` (`sdf-mesh-irradiance.hlsli:298-323`, mirrored in `SdfMeshIrradiance.Select` at `src/Puck.SdfVm/SdfMeshIrradiance.cs:2092-2122`), the level loop stops as soon as `total > 0` (`if (attempt == 0)` at `level = 1` succeeds if **even one** corner has weight `w = 1e-4`). It never blends with Level 0's boundary extension (`attempt = chosen + 2`), and instead divides by `total`, amplifying a single barely-unblocked or heavily-attenuated coarse corner to 100% of the receiver's irradiance.

---

## 2. Survey of Literature and Shipped Engine Techniques

> **Citation policy note**: All external sources below were retrieved via search summaries (`search_web`) and are marked **`summary-only`**. No direct quotes are taken from external sources. In-repo files were read directly via `read_file` and are cited by path and line range.

### 2.1 DDGI and Its Follow-Ups

#### A. Original DDGI: Chebyshev Depth Moments, Normal/View Bias, and Weight Crushing
- **Source**: *Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Fields*, Zander Majercik, Jean-Philippe Guertin, Derek Nowrouzezahrai, Morgan McGuire (NVIDIA / McGill / Université de Montréal), Journal of Computer Graphics Techniques (JCGT) Vol. 8, No. 2, 2019, `https://jcgt.org/published/0008/02/01/` (**`summary-only`**).
- **What it does**:
  1. Stores an octahedral irradiance texture (`8 × 8` interior texels) and an octahedral radial depth-moment texture (`16 × 16` interior texels storing radial distance mean $\mu$ and squared mean $\mu_2$) per probe on a regular grid.
  2. Shifts the shaded surface point toward the surface normal and camera view vector (`surfaceBias = (normal * 0.2 + viewVec * 0.8) * 0.75 * minAxialDistance`) before evaluating visibility.
  3. Weights each of the 8 enclosing probes by trilinear factor × wrap-shading backface weight $\max(0.0001, (\hat{d}\cdot\hat{n} + 1)/2)^2 + 0.2$ × Chebyshev upper-bound visibility $\left(\sigma^2 / (\sigma^2 + (r - \mu)^2)\right)^3$ when $r > \mu$.
  4. Applies a **weight-crushing threshold** (`crushThreshold = 0.2`): when a probe's combined visibility and backface weight falls below `crushThreshold`, its weight is multiplied by $w^2 / \text{crushThreshold}^2$ so a nearly occluded probe with tiny residual weight does not normalize to 100% when the other 7 probes are zero.
- **The leak it stops**: Softens smooth shadow transitions when a probe sits behind a thick blocker; weight crushing prevents single-probe normalization blowups when 7 of 8 corners are occluded.
- **Why it fails on thin walls / coarse levels (and what Puck already found)**:
  1. **Radial distance vs. plane distance**: As documented in `.claude/skills/rendering/references/mesh-gi.md:152-154` and `docs/plans/illumination.md:402`, interpolating scalar radial distances across an octahedral texel creates a spherical arc rather than a flat wall plane. At grazing angles along a coplanar wall or floor, radial distance underestimates the wall distance and gives ~48% self-occlusion (`chebyshev = 0.48`). Puck already replaced Chebyshev moments with its `12 × 12` first-hit surface map (distance + normal + curvature).
  2. **Spacing-proportional bias tunnels through thin walls**: At `8 m` spacing, aDDGI bias of `0.2 .. 0.3 × spacing` is `1.6 .. 2.4 m`, which pushes a receiver straight through a `0.3 m` wall or floor slab into the adjacent room (`mesh-gi.md:182-184`). Puck already clamps `SurfacePushMax = 0.05 m`.
  3. **Variance over-smoothing**: When a coarse probe's octahedral texel straddles a window frame or slab edge, radial depth variance $\sigma^2$ becomes huge, forcing the Chebyshev fraction close to `1.0` and leaking full outdoor sunlight through the wall.
- **Cost**: Low (`16 × 16` RG16F depth moments = `1 KiB` per probe; ALU-only gather).
- **What it would need from Puck**: Puck should **not** revert to Chebyshev moments, nor increase receiver push past `0.05 m`. However, DDGI's **weight-crushing threshold** coupled with a **confidence sum check** directly addresses Puck's single-probe normalization cliff in `sdfMeshProbesAt`.

#### B. Production DDGI / RTXGI: Relocation, Classification, Perceptual Depth, and Cascades
- **Source**: *Scaling Probe-Based Real-Time Dynamic Global Illumination for Production*, Zander Majercik, Adam Marrs, Josef Spjut, Morgan McGuire (NVIDIA), JCGT Vol. 10, No. 2, 2021, `http://jcgt.org/published/0010/02/01/` (**`summary-only`**).
- **What it does**:
  1. **Signed-distance probe relocation**: Uses ray hit distances and back-face hits from the probe trace to compute a spring-like displacement vector that moves probes out of geometry and away from close walls, clamped to `< 0.5 × spacing` per axis so the trilinear cage topology does not invert.
  2. **Probe state classification**: Classifies probes as *Inactive* (completely inside geometry or surrounded by backfaces, skipped during trace and given zero gather weight), *Sleeping* (unchanged static environment), or *Active*.
  3. **Self-shadow bias & perceptual depth encoding**: Replaces linear radial depth with a perceptual exponential curve to allocate precision near the probe, and separates the receiver bias into normal and view terms.
  4. **Scrolling cascades**: Introduces nested toroidal grids around the camera, blending between fine and coarse cascades across a transition band near the fine cascade boundary.
- **The leak it stops**: Stops probes trapped *inside* thick walls from injecting garbage or dark pockets, and smooths the seam where the fine cascade hands off to the coarse cascade.
- **Why it still leaks at `8 m` spacing**: Relocation is strictly bounded to `< 0.5 × spacing` (`< 4 m` at Level 1). When a `4.5 m` storey is narrower than the `8 m` vertical cell spacing, no bounded relocation can place all 8 corners of a cell inside a single `4.5 m` storey—at least 4 corners must remain on the storey above or below.
- **Cost**: Negligible runtime overhead (relocation and classification already exist in Puck's `SdfMeshIrradiance.Place` and `InvalidFlag`).
- **What it would need from Puck**: Puck already implements relocation (`Place`), inside-backface rejection (`Inside > InsideLimit` at `src/Puck.SdfVm/SdfMeshIrradiance.cs:1456`), and toroidal cascades. What Puck lacks from this paper is **smooth cross-cascade transition blending** when fine-cascade support ends or when coarse-cascade support is under-sampled.

#### C. Ubisoft Anvil (Assassin's Creed Shadows): High-Resolution `20 × 20` Octahedral G-Buffer Probes
- **Source**: *Raytracing the world of Assassin's Creed Shadows*, Mickaël Leblanc and Danielle Conte (Ubisoft), SIGGRAPH 2025 Advances in Real-Time Rendering in Games, 2025, `https://advances.realtimerendering.com/s2025/content/Advances%202025%20-%20Raytracing%20the%20world%20of%20Assassin's%20Creed%20Shadows.pdf` (**`summary-only`**).
- **What it does**:
  1. Stores a **`20 × 20` octahedral G-buffer** (`400` ray hits per probe: hit distance, normal, albedo) instead of `12 × 12` (`144` hits), decoupling geometric ray tracing (16% of GI frame budget) from probe relighting (84% of budget).
  2. Designed specifically around feudal Japanese interiors with thin shoji/paper walls and multi-room interiors where traditional DDGI depth moments leak severely.
  3. Uses hit distance and surface normal per octahedral texel for visibility rejection during probe-to-probe and probe-to-receiver relighting.
- **The leak it stops**: Narrows each octahedral texel's angular width from `~17°` (`12 × 12`) to `~10°` (`20 × 20`, or `~6.3°` at `32 × 32`), shrinking the world-space size of triangular octahedral texel footprints on distant walls by `2.8×` in area (`1.67×` linearly).
- **Cost**: Increasing surface-map resolution from `12 × 12` (`144` texels) to `24 × 24` (`576` texels) on **only the coarse levels** (`Level 1` and `Level 2`, `24,576` probes total) would add `432 × 1.5` words = `2,592` bytes per coarse probe (`~60.7 MiB` across Levels 1 and 2 if curvature is kept, or `~40.5 MiB` without curvature on coarse probes).
- **What it would need from Puck**: Coarse levels (`level >= 1`) need higher angular resolution than Level 0—the exact opposite of uniform `12 × 12` across all levels (see §2.4 on Radiance Cascades).

---

### 2.2 Cascaded and Clipmapped Probe Volumes in Shipped Engines

#### A. Unreal Engine 5 Lumen: Screen Probes + World-Space Radiance Cache (WSRC) Ray Handoff
- **Source**: *Lumen: Real-Time Global Illumination in Unreal Engine 5*, Daniel Wright, Krzysztof Narkowicz, Patrick Kelly (Epic Games), SIGGRAPH 2022 Advances in Real-Time Rendering in Games, 2022, `https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Lumen-Wright%20et%20al.pdf` (**`summary-only`**).
- **What it does**:
  1. Maintain a clipmapped **World-Space Radiance Cache (WSRC)** around the camera, where coarse clipmaps have multi-metre probe spacing.
  2. **Crucial architectural rule**: **Receivers never directly trilinearly interpolate the World-Space Radiance Cache.** Instead, Lumen places adaptive **Screen Probes** directly on visible G-buffer/mesh surfaces (so a screen probe on the far hall wall at `x = 10.6 m` sits *inside* the second storey on the correct side of the `0.3 m` wall, even `60 m` from the camera).
  3. Each receiver/screen-probe traces **short local rays** (`0 .. 2 m`) through the local Signed Distance Field (Per-Mesh SDFs or Global SDF clipmap) and only interpolates the coarse WSRC probes **at the end of the local ray** out in open room space, after stepping back from any hit to avoid punching through thin walls.
- **The leak it stops**: Completely eliminates both **wall straddling** and **storey straddling** for distant visible surfaces! Because local visibility over the first `2 .. 4 m` is resolved *from the receiver* using the SDF before handing off to the coarse world-space probe grid, a receiver on storey 2 at `x = 10.6 m` only queries WSRC probes along directions that were proven unoccluded by the local SDF.
- **Cost**: Short local SDF cone/sphere traces at downsampled screen-probe resolution (`1/16` screen resolution) or per-vertex/per-cluster on distant meshes, or a short SDF segment check from the receiver toward each candidate coarse probe.
- **What it would need from Puck**: For distant receivers falling outside Level 0 (`level >= 1`), instead of trusting the coarse probe's sparse `12 × 12` surface map to resolve a `0.3 m` wall `8 m` away, Puck can verify the **segment from the receiver toward the coarse probe** (or over the first `2 .. 4 m` from the receiver) against an SDF or retained BVH, or spawn sparse surface-hugging probe bricks where geometry exists.

#### B. Unity Adaptive Probe Volumes (APV): Geometry-Driven Bricks, Virtual Offset, Validity Masks, Dilation, and Rendering Layers
- **Sources**:
  - *Adaptive Probe Volumes*, Unity HDRP 17.0 Manual, Unity Technologies, 2024, `https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/Override-Probe-Volume.html` (**`summary-only`**).
  - *Fix light leaking with Adaptive Probe Volumes (Leak Reduction)*, Unity 6 URP Manual, Unity Technologies, 2024, `https://docs.unity3d.com/6000.0/Documentation/Manual/urp/probevolumes-leak-reduction.html` (**`summary-only`**).
- **What it does**:
  1. **Geometry-Driven `4 × 4 × 4` Bricks**: Subdivides space into an octree of `4 × 4 × 4` probe bricks (`64` probes per brick) at `1 m`, `3 m`, `9 m`, and `27 m` spacing. Crucially, subdivision is driven by **surface proximity, not camera distance**: any brick touching a wall or floor slab uses `1 m` spacing (even `100 m` away from the camera), while `9 m` and `27 m` bricks are placed only in empty outdoor air or high above roofs.
  2. **Virtual Offset**: Pushes a probe's capture origin out of close geometry along hit normals without moving its logical grid coordinate.
  3. **Per-Probe 8-Bit Corner Validity Mask & Dilation**: Each probe stores a validity mask encoding whether the 8 neighbouring probes in the trilinear stencil are mutually visible or inside solid geometry. During a post-bake **dilation pass**, invalid probes copy spherical-harmonic coefficients from valid neighbours on the same side of the wall, and runtime trilinear interpolation zeroes the weight of any corner marked invalid in the cell's validity mask.
  4. **Rendering Layer Masks (Room / Interior-Exterior Bitmasks)**: Assigns a compact 4-bit or 8-bit layer mask (e.g. *Outdoor*, *Storey 0*, *Storey 1*, *Storey 2*) to each probe and mesh renderer. At gather time, a receiver only weights probes whose layer mask overlaps the receiver's layer mask (`(probeMask & receiverMask) != 0`).
  5. **Runtime Leak Reduction (Anti-Leaking Ray/Offset)**: When a receiver's normal-biased sample point lands in a cell where some corners are invalid/occluded, APV warps the sampling position away from the invalid corners toward valid interior probes.
- **The leak it stops**:
  - Geometry-driven `1 m` bricks prevent coarse `8 m / 9 m` cells from ever touching a `0.3 m` wall or `4.5 m` storey slab.
  - Layer/room masks strictly prevent an outdoor probe (`x = 4 m`) or a first-storey probe (`y = 4 m`) from ever contributing to a second-storey indoor wall (`x = 10.6 m, y = 6.5 m`), regardless of angular texel aliasing.
- **Cost**:
  - Layer/storey mask: **zero extra bytes per probe** in Puck (fits in unused bits of `word 0` in `SdfMeshIrradianceProbe`) and 3 bitwise instructions in HLSL.
  - Adaptive bricks: requires a 3D indirection texture (`1 uint` per `4 m` or `12 m` brick cell) pointing into a flat brick pool, replacing fixed toroidal modulo indexing.
- **What it would need from Puck**:
  1. An **SDF-derived enclosure / storey / outdoor tag** packed into existing probe header bits (`word 0`) and tested during `sdfMeshProbeGatherLevel`.
  2. Optionally, an indirection-backed brick pool or vertical spacing adjustment so indoor walls never fall onto `8 m` vertical cells.

#### C. Ubisoft Massive (Far Cry 3 & Tom Clancy's The Division): Deferred Radiance Transfer Volumes & Indoor/Outdoor Sector Grids
- **Sources**:
  - *Deferred Radiance Transfer Volumes: Global Illumination in Far Cry 3*, Nikolay Stefanov and Mickaël Gilabert (Ubisoft Massive / Ubisoft Montreal), GDC 2012, `https://gdcvault.com/play/1015326/Deferred-Radiance-Transfer-Volumes-Global` (**`summary-only`**).
  - *Global Illumination in Tom Clancy's The Division*, Nikolay Stefanov (Ubisoft Massive), GDC 2016, `https://gdcvault.com/play/1023275/Global-Illumination-in-Tom-Clancy` (**`summary-only`**).
- **What it does**:
  1. *Far Cry 3* places probes on an adaptive hierarchy (`4 m`, `8 m`, `16 m` spacing) with precomputed sky/bounce transfer matrices, pushing probes near terrain/roofs along surface gradients.
  2. *The Division* streams brick-based probe grids (`2 m` indoors/alleys, coarser outdoors) and associates probes with **explicit indoor/outdoor building sector volumes**. When shading an indoor receiver, outdoor probes outside the building sector are masked out before trilinear normalization.
- **The leak it stops**: Stops `100,000 lx` outdoor street/sky probes from bleeding through `0.3 m` exterior building facades when coarse grids straddle the facade.
- **Cost**: 1 bit (indoor vs. outdoor) or a small room/sector ID per probe.
- **What it would need from Puck**: Puck already computes per-probe `Sky` shares (`words 1..3`, 6 cube faces in `src/Puck.SdfVm.Model/SdfMeshIrradianceProbe.cs:11`) and `Inside` back-face shares. Furthermore, Puck can classify indoor vs. outdoor and storey/room IDs automatically from the authored SDF without manual level-designer sector boxes.

#### D. EA Frostbite & SEED (GIBS): Surface-Spawned Irradiance Caches
- **Sources**:
  - *Precomputed Global Illumination in Frostbite*, Yuriy O'Donnell (EA Frostbite), GDC 2018, `https://www.ea.com/seed/news/gdc-2018-precomputed-gi-frostbite` (**`summary-only`**).
  - *Shipping Dynamic Global Illumination in Frostbite (GIBS)*, Laurent Apers (EA SEED / Frostbite), SIGGRAPH 2024 Advances in Real-Time Rendering in Games, 2024, `https://advances.realtimerendering.com/s2024/content/EA-GIBS2/Apers_Advances-s2024_Shipping-Dynamic-GI.pdf` (**`summary-only`**).
- **What it does**:
  1. Frostbite's probe volumes combine adaptive volume placement with boundary validity weights.
  2. EA's dynamic GIBS replaces uniform 3D spatial grids with **surface-spawned surfels/probes** allocated dynamically on visible G-buffer surfaces and stored in a spatial hash / page table keyed by `(quantizedPosition, normalBucket)`.
- **The leak it stops**: Because probes only exist *on surfaces* and are bucketed by surface normal direction, a probe on the outside of a `0.3 m` wall (`normal = (-1, 0, 0)`) and a probe on the inside (`normal = (+1, 0, 0)`) never share a bucket or interpolate across the wall.
- **Cost**: View-dependent allocation requires temporal warmup when disoccluding surfaces (`docs/decisions/rendering.md` notes Puck prefers view-independent world-space data for portals and camera cuts).
- **What it would need from Puck**: Rather than view-dependent G-buffer spawning (which violates Puck's portal/camera-cut readiness goals), Puck can use **static SDF surface ranges (`SdfInstanceRange`)** to drive sparse world-space probe bricks independent of camera view.

---

### 2.3 Adaptive, Sparse, and Tetrahedral Probe Placement

#### A. Tetrahedral Tessellations and Visibility-Constrained Interpolation (Cupisz 2012; Call of Duty 2017/2020)
- **Sources**:
  - *Light Probe Interpolation using Tetrahedral Tessellations*, Robert Cupisz (Unity Technologies), GDC 2012, `https://gdcvault.com/play/1015312/Light-Probe-Interpolation-Using-Tetrahedral` (**`summary-only`**).
  - *Precomputed Lighting in Call of Duty: Infinite Warfare*, Michał Iwanicki and Peter-Pike Sloan (Activision), SIGGRAPH 2017 Advances in Real-Time Rendering, 2017, `https://advances.realtimerendering.com/s2017/` (**`summary-only`**).
  - *Precomputed Lighting Advances in Call of Duty: Modern Warfare*, Peter-Pike Sloan and Ari Silvennoinen (Activision), SIGGRAPH 2020 Advances in Real-Time Rendering, 2020, `https://advances.realtimerendering.com/s2020/` (**`summary-only`**).
- **What it does**:
  1. Instead of a regular Cartesian grid, places probes on both sides of walls/floors and links them into a Delaunay tetrahedral mesh (Cupisz 2012) or an adaptive octree with per-cell plane/visibility constraints (Iwanicki & Sloan 2017; Sloan & Silvennoinen 2020).
  2. In *Call of Duty*, cells that straddle a wall store a **separating plane or per-corner visibility bitmask** so receivers on one side of the wall plane reject or re-project probe corners on the opposite side.
- **The leak it stops**: Stops interpolation edges from crossing solid walls or floor slabs when probe spacing is larger than wall thickness.
- **Cost**: Full tetrahedral walking on GPU is expensive and poorly suited to rolling toroidal clipmaps, **but** storing a **per-cell separating slab/wall plane or 8-bit corner-to-corner visibility mask** in a regular grid is cheap (`1 uint` per cell) and O(1) at gather time.
- **What it would need from Puck**: At probe placement time (`SdfMeshIrradiance.Place`), Puck can test whether the 8 corners of a cell straddle an SDF zero-crossing (`SdfSampledLattice` / `SdfMeshIrradianceScene.Unblocked`) and record a compact **cell-partition or room-ID signature**.

#### B. Surface-Normal-Keyed Spatial Hash Radiance Cache (NVIDIA SHaRC)
- **Source**: *RTXGI 2.0: Spatial Hash Radiance Cache (SHaRC)*, Pascal Gautron, Zander Majercik et al. (NVIDIA), 2023–2024, `https://developer.nvidia.com/rtx/ray-tracing/rtxgi` (**`summary-only`**).
- **What it does**: Hashes `(floor(position / cellSize), normalDirectionIndex)` into a fixed-size GPU hash table of irradiance vouchers, using camera-distance-scaled LOD cell sizes.
- **The leak it stops**: Normal bucketing separates opposing sides of thin walls (`+X` vs `-X`, floor `+Y` vs ceiling `-Y` of the storey below). However, two parallel walls facing the same direction (`+Y` floor on storey 1 and `+Y` floor on storey 2) inside the same `8 m` cell still collide unless the vertical hash key is finer than the storey height (`4.5 m`).
- **Cost**: Hash-table probe lookup with linear probing (`4–8` atomic/read ops per query).
- **What it would need from Puck**: Replaces direct 3D clipmap indexing; less predictable for Puck's deterministic/fixed-budget architecture than keeping a clipmap or brick indirection with vertical anisotropy.

---

### 2.4 Radiance Cascades (Sannikov) and Hierarchical Radiance Methods

#### A. Radiance Cascades and the Angular-Spatial Scaling Theorem (Penumbra Condition)
- **Sources**:
  - *Radiance Cascades: A Novel Approach to Calculating Global Illumination*, Alexander Sannikov (Grinding Gear Games), 2023–2024, `https://github.com/Raikiri/RadianceCascadesPaper` (**`summary-only`**).
  - *Split Radiance Cascades: Real-Time Global Illumination via Sparse Radiance Probes*, Osborne Freeman, Alexander Sannikov, Joe Pitt-Francis (University of Oxford / Grinding Gear Games), arXiv:2607.20384, 2026, `https://arxiv.org/abs/2607.20384` (**`summary-only`**).
- **What it does**:
  1. **The Penumbra / Angular-Spatial Scaling Law**: To resolve an occluder or opening of size $w$ at distance $t_i$ without aliasing or leaking, a cascade $i$ whose spatial probe spacing doubles ($\Delta x_i = 2^i \Delta x_0$) must simultaneously:
     - **Start its ray interval at $t_i \propto 2^i$** (coarse cascade $i$ only integrates the ray interval $[t_i, t_{i+1}]$, **never** $[0, t_i]$ near the receiver!), and
     - **Double its linear angular resolution** ($\Delta \theta_i \propto 2^{-i}$, i.e. $4^i$ rays in 3D) so the world-space transverse spacing between adjacent rays $t_i \cdot \Delta \theta_i$ remains bounded by the spatial grid spacing $\Delta x_i$.
  2. **The Bilinear Fix (Depth-Aware Cascade Merge)**: When merging cascade $i+1$ (coarse) into cascade $i$ (fine), interpolating the 8 coarse probes along a ray direction leaks through thin walls if the ray hits a wall inside $[t_i, t_{i+1}]$. Sannikov's **bilinear fix** shifts or forks the fine ray toward each of the 8 coarse probe positions over $[t_i, t_{i+1}]$ (or tests visibility between the fine probe and each coarse probe) before accepting that coarse probe's radiance.
  3. **Split Radiance Cascades (2026)**: Allocates 3D cascade probes **sparsely around geometry surfaces** (using a 2-level spatial brick index) instead of filling empty air, and applies a depth-aware bilateral/trilinear filter during cascade merging to stop cross-wall bleeding.
- **Why this explains Puck's exact failure mode**:
  Puck's current clipmap **violates both halves of the Radiance Cascades theorem** when shading a far wall at `x = 10.6 m`:
  1. **Interval violation**: Because Level 0 is a camera-centred `48 m` window (`24 m` radius), a visible wall `58 m` from the camera has **no Level 0 probe covering the near interval $[0, 2.65\text{ m}]$**. Level 1 (`8 m` spacing) is forced to integrate all the way from $t = 0$ (the receiver surface) using probes up to `14 m` away!
  2. **Angular scaling violation**: Instead of *increasing* angular resolution at `8 m` spacing to keep $t \cdot \Delta \theta \le 0.3\text{ m}$, Puck keeps the surface map at a fixed **`12 × 12` (`144` rays)** across all levels. At $d = 10\text{ m}$, $d \cdot \Delta \theta \approx 10 \times 0.295 = 2.95\text{ m}$—nearly **10× thicker than the `0.3 m` wall and floor slabs**, guaranteeing octahedral triangle leaks!
- **The leak it stops**: Satisfying the local interval condition $[0, t_1]$ near the receiver (or increasing angular resolution / applying the visibility-aware merge fix) eliminates both the `2.95 m` octahedral triangle footprint and cross-wall trilinear bleeding.
- **Cost & what it would need from Puck**:
  - Either keep near-field resolution ($1\text{–}2\text{ m}$) around static geometry inside the view frustum / hall (like Split Radiance Cascades or Unity APV),
  - Or resolve the short near-receiver interval $[0, t_1]$ directly at the receiver (via SDF or Level-0 boundary / anisotropic grid) before querying the `8 m` cascade for far radiance.

---

### 2.5 Approaches Using a Signed Distance Field (SDF)

#### A. Unreal Engine Distance Field Ambient Occlusion (DFAO) & Cone Tracing (Wright 2015)
- **Source**: *Dynamic Occlusion with Signed Distance Fields*, Daniel Wright (Epic Games), SIGGRAPH 2015 Advances in Real-Time Rendering in Games, 2015, `https://advances.realtimerendering.com/s2015/DynamicOcclusionWithSignedDistanceFields.pdf` (**`summary-only`**).
- **What it does**: Evaluates soft cone visibility and point-to-point segment occlusion by sphere-marching a clipmapped Global SDF (composited from per-mesh SDF bricks). Along a ray from receiver $\mathbf{x}$ toward sample $\mathbf{p}$ of length $L$, at each step $t$ the SDF returns $s = \phi(\mathbf{x} + t\hat{d})$, which both advances the ray by $\max(s, \epsilon)$ and bounds the minimum cone aperture $\min_t(s / t)$.
- **The leak it stops**: An exact or fine-brick SDF segment trace between receiver $\mathbf{x}$ and coarse probe $\mathbf{p}$ is **100% immune to the `12 × 12` octahedral angular aliasing** of the probe's surface map: if a `0.3 m` floor slab or exterior wall lies anywhere along $\mathbf{x} \to \mathbf{p}$, $\phi \le 0$ on that segment and the probe weight is zeroed cleanly without triangular texel seams.

#### B. Godot 4 SDFGI: Cascaded SDF + Probe-to-Receiver Occlusion
- **Source**: *Using SDFGI (Signed Distance Field Global Illumination)*, Juan Linietsky and Godot Engine Contributors, Godot 4.x Documentation, 2023–2024, `https://docs.godotengine.org/en/stable/tutorials/3d/global_illumination/using_sdfgi.html` (**`summary-only`**).
- **What it does**: Maintains 4–8 nested cascades storing both a 3D SDF texture and an octahedral irradiance probe grid (`8 × 8` or `16 × 16`). Probes trace rays directly through the cascaded SDF, and receivers bias along the SDF gradient and use SDF occlusion to reject probes across walls.
- **Why naive cascaded SDFs still leak on coarse cascades**: When the SDF voxel size in cascade $k$ exceeds wall thickness ($> 0.3\text{ m}$), thin walls vanish from the coarse SDF voxel grid (*thin-wall erasure*), causing coarse probes to trace and gather straight through `0.3 m` walls.
- **Why Puck does NOT have Godot's thin-wall erasure problem**:
  Puck's SDF is **authored as an exact analytical program** (`Puck.SignedDistance`, `SdfFieldEvaluator`), and Puck already builds two exact representations that preserve `0.3 m` walls at any distance:
  1. **The retained static mesh BVH (`SdfMeshIrradianceScene`)**, which already has exact segment occlusion `scene.Unblocked(origin, dir, maxDistance)` (`src/Puck.SdfVm/SdfMeshIrradianceScene.cs:112-136`) and exact nearest-surface queries `scene.TryNearestSurface` (`src/Puck.SdfVm/SdfMeshIrradianceScene.Nearest.cs:12-65`). In fact, `world.probe` (`src/Puck.SdfVm/SdfMeshIrradiance.cs:2037-2043`) **already calls `scene.Unblocked` as the ground-truth reference (`reference-clear`)** when reporting why a probe leaked!
  2. **The bake lattices (`SdfSampledLattice`)**, which already detect thin sheets and slabs (`SheetsBetween` / `CloseSheets` in `.claude/skills/rendering/references/mesh-bake.md:72-95`) when baking static creations.

---

## 3. Summary Comparison Matrix

| Technique | Primary Leak Stopped | Stops `0.3 m` Wall Leak at `8 m`? | Stops `4.5 m` Storey Straddling? | Runtime GPU Cost | Memory Cost in Puck |
|---|---|---|---|---|---|
| **DDGI Chebyshev Moments** (Majercik 2019) | Smooth thick-wall shadow transitions | **No** (variance blowup & grazing arc error) | **No** | Very low | `-648 B/probe` (if replacing surface map, but worse quality) |
| **DDGI Weight Crushing + Cross-Cascade Confidence Blend** | Single-probe 100% normalization cliff & hard cascade cutoff | **Mitigates** (blends to Level 0 boundary & crushes tiny leaks) | **Mitigates** | ~10 ALU ops in gather shader | **`0 bytes`** |
| **Vertical Anisotropy + Closer Level-1 Spacing** (`4 m × 2 m × 4 m` or `8 m × 2.25 m × 8 m`) | Multi-storey vertical cage straddling | **Partially** (halves or quarters ray distance) | **Yes** ($\Delta y \le 2.25\text{ m}$ gives $\ge 2$ probe layers per `4.5 m` storey) | **`0` extra trace/gather cost** (same `12,288` probe count) | **`0 bytes`** |
| **SDF / BVH Topological Room + Storey + Outdoor Tag** (packed in `word 0`) | Cross-wall (indoor/outdoor) and cross-slab (storey-to-storey) bleeding | **Yes** (indoor receivers reject outdoor & wrong-storey probes) | **Yes** (storey tag / slab-crossing check) | 3 bitwise ops in HLSL gather + CPU/bake classification on dirty probes | **`0 bytes`** (uses 8 free bits in `word 0` of `SdfMeshIrradianceProbe`) |
| **Coarse-Level Higher Angular Surface Map** (`24 × 24` on Levels 1–2, AC Shadows / RC style) | Octahedral `17°` triangle footprints at `8–20 m` range | **Substantially** ($4\times$ solid-angle density) | **Partially** (still needs storey/slab support) | $4\times$ depth rays on dirty coarse probes only (rarely dirty) | `+40–60 MiB` if dense, or `0 B` if curvature is dropped on coarse levels |
| **Lumen / RC Receiver-Side Local SDF Visibility Handoff** | All near-receiver thin-wall & slab leaks at any distance | **Yes** (exact local visibility from receiver) | **Yes** | 4–8 SDF/brick taps per gather or per screen-probe | `0–8 MiB` for coarse world SDF/visibility brick |
| **Unity APV / Split RC Sparse Geometry-Driven Probe Bricks** (`1–2 m` near surfaces, `8–32 m` in open air) | Both angular aliasing and topological straddling everywhere | **Yes** (far indoor walls still get `1–2 m` probes) | **Yes** | 1 indirection texture fetch per level gather | **Lower or equal** to current `55.08 MiB` |

---

## 4. Ranked Options for Puck (Including SDF-Native Combinations)

The options below are ranked by **dividend-to-complexity ratio** for Puck: starting with immediate, zero-memory changes that fix the `worlds/debug/hall.puck` defect at its mathematical cause, followed by SDF-native structural upgrades that only an SDF-authored engine with `SdfMeshIrradianceScene` can execute cleanly.

---

### Rank 1 (Immediate High-Dividend Fix, `0 Bytes` Added): Vertical Anisotropy + Weight Crushing + Confidence-Weighted Cross-Level Blend

This combination fixes three concrete defects in `SdfMeshIrradiance.cs` and `sdf-mesh-irradiance.hlsli` without adding a single byte to the `55.08 MiB` probe buffer or a single ray to the frame budget.

#### 1A. Match Coarse Vertical Spacing (`Y`) to Architectural Storeys (`Anisotropic Spacing` or `4 m` Level 1)
- **Diagnosis**: Currently `SdfMeshIrradiance` uses isotropic cubic spacing (`1 m`, `8 m`, `32 m`) on an anisotropic grid (`32 × 12 × 32`). At Level 1 (`8 m`), the vertical extent is $12 \times 8\text{ m} = 96\text{ m}$—mostly wasted in empty sky above the `18 m` hall roof and deep underground—while the `8 m` vertical step is **too coarse to fit inside a `4.5 m` storey**!
- **Fix**:
  - Either make Level 1 spacing **`4 m`** (giving a `128 m × 48 m × 128 m` window that easily covers the entire `60 m` hall and guarantees at least one probe layer inside every `4.5 m` storey, with Level 2 at `16 m` or `24 m` covering `512 m` / `768 m`),
  - Or support **anisotropic per-axis spacing `float3 LevelSpacing`** on Level 1 (`(6.0f, 2.25f, 6.0f)` or `(8.0f, 2.25f, 8.0f)`), so `32 × 12 × 32` covers `256 m × 27 m × 256 m` with **two probe layers per `4.5 m` storey** (`2.25 m` vertical step).
- **Leak stopped**: Every `4.5 m` storey in `hall.puck` gets its own dedicated horizontal sheets of Level 1 probes *inside* the storey (`y ≈ 5.6 m` and `y ≈ 7.9 m`), eliminating the forced cross-slab gather from `y = 4 m` and `y = 12 m`.

#### 1B. Crush Sub-Threshold Probe Weights and Blend Across Levels by Support Confidence
- **Diagnosis**: In `sdfMeshProbesAt` (`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-mesh-irradiance.hlsli:298-323`) and `SdfMeshIrradiance.Select` (`src/Puck.SdfVm/SdfMeshIrradiance.cs:2092-2122`), two things go wrong when Level 1 is queried on the far wall:
  1. If 7 of the 8 coarse probes are blocked (`w = 0`) and 1 outdoor or upper-storey probe has a tiny residual leak (`w = 0.02` because `blocked = 0.48 < DepthCoverageStart = 0.5`), `total = 0.02 > 0` **terminates the `attempt` loop at `attempt = 0`** and normalizes that single leaky probe by `1.0 / 0.02 = 50×` to 100% of the pixel's lighting!
  2. Even when all indoor coarse probes are partially occluded (`total = 0.08`), the shader never consults Level 0's boundary probes (`attempt = chosen + 2`), creating a hard visual cliff where Level 0's `SurfaceReach` ends.
- **Fix**:
  1. **Per-probe weight crushing (DDGI 2019)**: After computing `depthWeight` in `sdfMeshProbeGatherLevel` (`sdf-mesh-irradiance.hlsli:287`), apply a smooth crush ramp when `depthWeight < 0.25` (`depthWeight *= sqr(depthWeight / 0.25)`), and zero any probe weight where `depthWeight < 0.05`.
  2. **Support confidence metric**: In `sdfMeshProbeGatherLevel`, alongside `total = sum(w_i)`, accumulate the **unoccluded geometric support** `idealTotal = sum(trilinear_i * facing_i)`. The ratio `confidence = saturate(total / max(idealTotal * 0.35, 1e-4))` measures what fraction of the front-facing trilinear cage actually had an unblocked line of sight to the receiver.
  3. **Confidence-weighted cross-level blend**: When `confidence < 1.0` on the primary level (`attempt == 0`), continue to the fallback attempts (`attempt = 1 .. 3`, including Level 0's clamped boundary extension `attempt = chosen + 2`) to fill the remaining `1.0 - confidence` weight! Additionally, widen the spatial feather band at the outer `2 × spacing` (`2 m`) rim of Level 0 so Level 0 hands off smoothly before its window edge.
- **Touchpoints**: `src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-mesh-irradiance.hlsli:231-323` and `src/Puck.SdfVm/SdfMeshIrradiance.cs:2063-2122`.

---

### Rank 2 (SDF-Native Combination A, `0 Bytes` Added): SDF/BVH Topological Enclosure & Slab-Crossing Mask in `word 0`

This is a **Puck-native synthesis** combining Unity APV's *Rendering Layer / Validity Masks* and *The Division*'s *Indoor/Outdoor Sectors*, automated completely from Puck's exact geometry (`SdfMeshIrradianceScene` and the authored SDF) so no author ever places a portal or room volume by hand.

#### How It Works
1. **Available Header Bits (`0 bytes` extra per probe)**:
   In `src/Puck.SdfVm.Model/SdfMeshIrradianceProbe.cs:13-16`, `word 0` of every probe stores:
   - bits `0..19` (or `0..28`): `KeyMask = 0x3FFFFFFFu` (the toroidal cell key uses at most `6 + 5 + 6 = 17` bits for `48 × 16 × 48`),
   - bit `30`: `InvalidFlag`,
   - bit `31`: `WrittenFlag`.
   This leaves **at least 12 unused bits in `word 0`** of every probe record!
2. **What to Pack into `word 0` (or the high bits of `word 18` alongside `Inside`) during `Place` / `FillSurfaceMaps`**:
   - **8-bit Neighbour/Octant Segment-Clear Mask (`octantClearBits`, bits `20..27`)**:
     For each coarse probe (`level >= 1`), every cell has 8 dual octants (or 6 axial neighbours: $\pm X, \pm Y, \pm Z$). During CPU/GPU probe placement (`SdfMeshIrradiance.Place`), test `scene.Unblocked` (or the SDF) from the relocated `probePoint` to the 8 cell sub-centres $\mathbf{c}_q = \text{cellCenter} + (\pm 0.25, \pm 0.25, \pm 0.25) \cdot \text{spacing}$.
     If a `0.3 m` wall or floor slab separates `probePoint` from sub-octant $q$, clear bit $q$!
   - **2-bit Enclosure Class (`enclosure`, bits `28..29`)**:
     From the probe's already-computed 6 sky shares (`words 1..3`) and zenith surface hit distance (`surface[zenith]`):
     - `0 = Outdoor / Open Sky` (zenith ray misses or total sky share $> 0.35$),
     - `1 = Threshold / Portal` (zenith blocked by ceiling/roof, lateral sky share $> 0.08$),
     - `2 = Deep Interior` (zenith blocked, lateral sky share $\le 0.08$).
3. **Runtime Gather Check in `sdfMeshProbeGatherLevel`**:
   - Given the receiver's `pushed` position inside the coarse cell, its relative sign from the cell centre determines which sub-octant $q \in [0..7]$ the receiver occupies (`q = (rel.x >= 0) | ((rel.y >= 0) << 1) | ((rel.z >= 0) << 2)`).
   - If bit $q$ is `0` in a corner probe's `octantClearBits` (meaning a wall or floor slab separates that corner probe from the receiver's octant of the cell), **multiply that probe's weight by `0` (or `0.05` if `enclosure == 1`)**!
- **The leak it stops**:
  - **Stops outdoor-to-indoor leaks across the `0.3 m` exterior wall at `x = 10 m`**: The probe at `x = 4 m` (outdoors) is blocked from the `x > 8 m` indoor sub-octants by the `x = 10 m` wall, so its octant bit for the receiver at `x = 10.6 m` is `0`.
  - **Stops storey-to-storey leaks across the `0.3 m` floor slabs at `y = 4.55 m` and `y = 9.05 m`**: A probe at `y = 4 m` (storey 1) is blocked from the `y > 4.55 m` sub-octant by the floor slab, so it cannot leak onto a storey-2 receiver at `y = 6.5 m`!
  - **Eliminates pale octahedral triangles**: Because the octant mask is evaluated in **spatial cell coordinates** (where the receiver sits relative to the cell's internal walls/slabs) rather than the `12 × 12` angular octahedral map, it never produces triangular octahedral texel seams on the wall.
- **Cost**:
  - **Memory**: `0 bytes` (uses 8–10 free bits in `word 0`).
  - **Placement cost**: 8 short `scene.Unblocked` ray checks per dirty coarse probe during `Place` (`< 0.02 ms` on CPU for the few coarse probes dirtied on camera scroll).
  - **Shader gather cost**: 4 integer bitwise instructions per corner probe in `sdfMeshProbeGatherLevel`.

---

### Rank 3 (SDF-Native Combination B): Asymmetric Cascade Resolution (Radiance-Cascade Scaling on Coarse Levels) or Curvature Reallocation

#### Diagnosis from Sannikov's Radiance Cascades
Puck spends the exact same per-probe memory budget (`235` words = `940` bytes) on all three levels:
- `144` words (`576` bytes) for the `12 × 12` distance+normal surface map, plus
- `72` words (`288` bytes, **30.6% of the entire probe record**) for `144` FP16 normal-gradient curvatures (`words 163..234`).

At Level 0 (`1 m` spacing), curvature (`words 163..234`) is valuable because a `1 m` cell hugs curved columns and spheres closely. At Level 1 (`8 m`) and Level 2 (`32 m`), however, **curvature over a `3 m` octahedral texel footprint is meaningless** when the texel itself straddles multiple architectural planes (`sdf-mesh-irradiance.hlsli:109` already zeroes the curvature wherever adjacent texels have a `> 4 cm` step gap!). Meanwhile, Level 1 is starved of **angular resolution** (`12 × 12` texels over `8–20 m`).

#### Fix Options
- **Option 3A (Zero-Byte Reallocation inside the 235-Word Record)**:
  For `level >= 1` (`8 m` and `32 m`), repurpose the `72` curvature words (`words 163..234`)!
  - Either store **216 words (`144 + 72`) of packed surface-map texels** (`16 × 14` or `15 × 15` octahedral distance+normal texels without curvature, or `24 × 24` 12-bit distance + 4-bit normal-octant texels in `216` words, **quadrupling** angular ray density from `144` to `576` rays per coarse probe at **zero extra memory**),
  - Or store a **min/max hierarchical depth-wedge range `(d_min, d_max)` per texel** so any texel that straddles a slab edge or window opening knows both the near blocker distance (`d_min`, the `0.3 m` wall/slab) and the far distance (`d_max`), using `d_min` conservatively whenever the receiver's normal faces away from the opening.
- **Option 3B (Dedicated Coarse Angular Upgrade)**:
  Increase `ProbeSurfaceSide` from `12` to `20` (`400` rays, matching *Assassin's Creed Shadows*, Leblanc & Conte 2025) on Level 1 while dropping curvature on Level 1–2.

---

### Rank 4 (Medium-Term Architectural Dividend): SDF-Driven Sparse Probe Bricks or Receiver-Side SDF Handoff (Lumen / Split RC / APV Synthesis)

If Puck later redesigns the clipmap storage during the illumination campaign (`docs/plans/illumination.md`), the clean long-term architecture combines **Split Radiance Cascades / Unity APV sparse geometry bricks** with **Puck's static `SdfInstanceRange` metadata**:

1. **Why Dense Clipmaps Waste 85% of Probes**:
   In `worlds/debug/hall.puck`, static geometry occupies a thin shell of walls, floor slabs, columns, and terrain. Yet Level 0 (`36,864` probes) and Level 1 (`12,288` probes) allocate full 3D grids across empty outdoor air and underground rock, while starving the far end of the `60 m` hall (`x = 10 .. 37 m`) because the `48 m` dense Level-0 window runs out of reach.
2. **SDF-Driven `4 × 4 × 4` Probe Bricks**:
   - Puck already knows the exact world AABB and surface detail of every static SDF instance (`SdfInstanceRange.Box`, `SdfInstanceRange.Detail` in `src/Puck.SdfVm/SdfMeshIrradianceScene.cs`).
   - Replace the dense `(48 × 16 × 48) + 2 × (32 × 12 × 32)` grids (`61,440` probes) with a **single pool of `1,024`bricks of `4 × 4 × 4` probes (`65,536` probes total)** indexed by a lightweight 3D clipmap indirection table (`1 uint16` brick index per `4 m × 4 m × 4 m` cell across a `256 m × 64 m × 256 m` domain = `512 KiB`).
   - Any `4 m` cell whose AABB intersects static geometry in `SdfMeshIrradianceScene` (including the far west wall of the hall at `x = 10 m`, `60 m` from the camera!) gets a **`1 m` or `2 m` brick**, while empty sky and subterranean cells point to a single shared sky/null brick or a coarse `8 m` brick.
   - **Result**: Every wall and storey in the entire `60 m` hall is permanently covered by `1–2 m` probes with zero `8 m` cross-storey or cross-wall straddling, within the exact same `~55 MiB` memory footprint and the exact same per-frame dirty probe budget.

---

### Concrete Execution Recommendation for Puck

1. **Do Rank 1 + Rank 2 together first** (small, surgical change in `SdfMeshIrradiance.cs`, `SdfMeshIrradianceProbe.cs`, and `sdf-mesh-irradiance.hlsli`, `0 bytes` added to GPU memory):
   - Pack the **8-bit cell sub-octant segment visibility mask** (`scene.Unblocked` from each coarse probe to its 8 dual cell sub-centres) into the unused bits `20..27` of `word 0` during `SdfMeshIrradiance.Place`, and reject occluded sub-octants in `sdfMeshProbeGatherLevel` and `SdfMeshIrradiance.SampleLevel`.
   - Add **weight crushing** (`depthWeight < 0.25`) + **confidence-weighted cross-level blending** (`confidence = total / idealTotal`) in `sdfMeshProbesAt` and `SdfMeshIrradiance.Select` so under-supported Level 1 cells smoothly blend with Level 0's clamped boundary extension instead of normalizing a single leaky corner to 100%.
   - Adjust Level 1's vertical spacing (or Level 1's isotropic spacing from `8 m` to `4 m` / anisotropic `(8, 2.25, 8)`) so `4.5 m` storeys never fall entirely between coarse Y layers.
2. **Evaluate Rank 3A next** if any angular aliasing remains on coarse levels inside tall open atriums (`> 9 m` ceiling height where octant masks are all clear): repurpose the `72` curvature words on `level >= 1` for higher angular resolution or `(d_min, d_max)` depth-wedge bounds.
3. **Feed Rank 4** into `docs/plans/illumination.md` when moving from dense toroidal grids to SDF-driven sparse probe bricks.

---

## 5. Bibliography and Source Ledger

### 5.1 External Sources (All Marked `summary-only`)

1. **Majercik et al. 2019 (DDGI)** — *Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Fields*, Zander Majercik, Jean-Philippe Guertin, Derek Nowrouzezahrai, Morgan McGuire (NVIDIA / McGill University / Université de Montréal), Journal of Computer Graphics Techniques (JCGT), Vol. 8, No. 2, pp. 1–30, 2019, `https://jcgt.org/published/0008/02/01/` — **`summary-only`**.
2. **Majercik et al. 2021 (Scaling DDGI / RTXGI)** — *Scaling Probe-Based Real-Time Dynamic Global Illumination for Production*, Zander Majercik, Adam Marrs, Josef Spjut, Morgan McGuire (NVIDIA), Journal of Computer Graphics Techniques (JCGT), Vol. 10, No. 2, pp. 1–29, 2021, `http://jcgt.org/published/0010/02/01/` — **`summary-only`**.
3. **Leblanc and Conte 2025 (Assassin's Creed Shadows GI)** — *Raytracing the world of Assassin's Creed Shadows*, Mickaël Leblanc and Danielle Conte (Ubisoft), SIGGRAPH 2025 Advances in Real-Time Rendering in Games, 2025, `https://advances.realtimerendering.com/s2025/content/Advances%202025%20-%20Raytracing%20the%20world%20of%20Assassin's%20Creed%20Shadows.pdf` — **`summary-only`**.
4. **Wright, Narkowicz, and Kelly 2022 (Unreal Engine 5 Lumen)** — *Lumen: Real-Time Global Illumination in Unreal Engine 5*, Daniel Wright, Krzysztof Narkowicz, Patrick Kelly (Epic Games), SIGGRAPH 2022 Advances in Real-Time Rendering in Games, 2022, `https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Lumen-Wright%20et%20al.pdf` — **`summary-only`**.
5. **Wright 2015 (Unreal Engine DFAO)** — *Dynamic Occlusion with Signed Distance Fields*, Daniel Wright (Epic Games), SIGGRAPH 2015 Advances in Real-Time Rendering in Games, 2015, `https://advances.realtimerendering.com/s2015/DynamicOcclusionWithSignedDistanceFields.pdf` — **`summary-only`**.
6. **Unity Technologies 2024a (Adaptive Probe Volumes HDRP)** — *Adaptive Probe Volumes*, Unity High Definition Render Pipeline (HDRP) 17.0 Manual, Unity Technologies, 2024, `https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/Override-Probe-Volume.html` — **`summary-only`**.
7. **Unity Technologies 2024b (APV Leak Reduction URP)** — *Fix light leaking with Adaptive Probe Volumes*, Unity 6 Universal Render Pipeline (URP) Manual, Unity Technologies, 2024, `https://docs.unity3d.com/6000.0/Documentation/Manual/urp/probevolumes-leak-reduction.html` — **`summary-only`**.
8. **Stefanov and Gilabert 2012 (Far Cry 3 DRTV)** — *Deferred Radiance Transfer Volumes: Global Illumination in Far Cry 3*, Nikolay Stefanov and Mickaël Gilabert (Ubisoft Massive / Ubisoft Montreal), Game Developers Conference (GDC), 2012, `https://gdcvault.com/play/1015326/Deferred-Radiance-Transfer-Volumes-Global` — **`summary-only`**.
9. **Stefanov 2016 (Tom Clancy's The Division GI)** — *Global Illumination in Tom Clancy's The Division*, Nikolay Stefanov (Ubisoft Massive), Game Developers Conference (GDC), 2016, `https://gdcvault.com/play/1023275/Global-Illumination-in-Tom-Clancy` — **`summary-only`**.
10. **O'Donnell 2018 (Frostbite Precomputed GI)** — *Precomputed Global Illumination in Frostbite*, Yuriy O'Donnell (EA Frostbite), Game Developers Conference (GDC), 2018, `https://www.ea.com/seed/news/gdc-2018-precomputed-gi-frostbite` — **`summary-only`**.
11. **Apers 2024 (Frostbite / EA SEED Dynamic GI — GIBS)** — *Shipping Dynamic Global Illumination in Frostbite*, Laurent Apers (EA SEED / Frostbite), SIGGRAPH 2024 Advances in Real-Time Rendering in Games, 2024, `https://advances.realtimerendering.com/s2024/content/EA-GIBS2/Apers_Advances-s2024_Shipping-Dynamic-GI.pdf` — **`summary-only`**.
12. **Cupisz 2012 (Tetrahedral Light Probe Interpolation)** — *Light Probe Interpolation using Tetrahedral Tessellations*, Robert Cupisz (Unity Technologies), Game Developers Conference (GDC), 2012, `https://gdcvault.com/play/1015312/Light-Probe-Interpolation-Using-Tetrahedral` — **`summary-only`**.
13. **Iwanicki and Sloan 2017 (Call of Duty: Infinite Warfare GI)** — *Precomputed Lighting in Call of Duty: Infinite Warfare*, Michał Iwanicki and Peter-Pike Sloan (Activision), SIGGRAPH 2017 Advances in Real-Time Rendering in Games, 2017, `https://advances.realtimerendering.com/s2017/` — **`summary-only`**.
14. **Sloan and Silvennoinen 2020 (Call of Duty: Modern Warfare GI)** — *Precomputed Lighting Advances in Call of Duty: Modern Warfare*, Peter-Pike Sloan and Ari Silvennoinen (Activision), SIGGRAPH 2020 Advances in Real-Time Rendering in Games, 2020, `https://advances.realtimerendering.com/s2020/` — **`summary-only`**.
15. **Gautron, Majercik et al. 2023 (NVIDIA RTXGI 2.0 / SHaRC)** — *RTX Global Illumination (RTXGI) 2.0 and Spatial Hash Radiance Cache (SHaRC)*, NVIDIA Corporation, 2023–2024, `https://developer.nvidia.com/rtx/ray-tracing/rtxgi` — **`summary-only`**.
16. **Sannikov 2023 (Radiance Cascades)** — *Radiance Cascades: A Novel Approach to Calculating Global Illumination*, Alexander Sannikov (Grinding Gear Games), 2023–2024, `https://github.com/Raikiri/RadianceCascadesPaper` — **`summary-only`**.
17. **Freeman, Sannikov, and Pitt-Francis 2026 (Split Radiance Cascades)** — *Split Radiance Cascades: Real-Time Global Illumination via Sparse Radiance Probes*, Osborne Freeman, Alexander Sannikov, Joe Pitt-Francis (University of Oxford / Grinding Gear Games), arXiv:2607.20384, 2026, `https://arxiv.org/abs/2607.20384` — **`summary-only`**.
18. **Linietsky et al. 2023 (Godot 4 SDFGI)** — *Using SDFGI (Signed Distance Field Global Illumination)*, Juan Linietsky and Godot Engine Contributors, Godot Engine 4.x Official Documentation, 2023–2024, `https://docs.godotengine.org/en/stable/tutorials/3d/global_illumination/using_sdfgi.html` — **`summary-only`**.

### 5.2 In-Repository Files Read and Cited

- `.claude/skills/rendering/references/mesh-gi.md` (lines 1–204)
- `.claude/skills/rendering/references/mesh-bake.md` (lines 1–120)
- `src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-mesh-irradiance.hlsli` (lines 1–408)
- `src/Puck.SdfVm.Model/SdfMeshIrradianceProbe.cs` (lines 1–50)
- `src/Puck.SdfVm/SdfMeshIrradiance.cs` (lines 1–2601)
- `src/Puck.SdfVm/SdfMeshProbeDepth.cs` (lines 1–300)
- `src/Puck.SdfVm/SdfMeshIrradianceScene.cs` (lines 1–889)
- `src/Puck.SdfVm/SdfMeshIrradianceScene.Nearest.cs` (lines 1–78)
- `docs/plans/open-items.md` (lines 1772–1788)
- `docs/plans/illumination.md` (lines 1–518)
- `docs/decisions/rendering.md` (lines 1–1207)
- `worlds/debug/hall.puck` (lines 1–193)
