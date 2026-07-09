# Shading, Ambient Occlusion & Shadows

Techniques that run **after the march has a hit** — the shading epilogue that
turns a surface point, a normal, and a distance field into a lit pixel. Puck
ships **no ambient occlusion at all today**; its only occlusion term is the
soft-shadow march toward the sun. Everything on this page either reuses that
existing `map()`/normal/shadow infrastructure at the same seam or is a
sweep-only survey entry for later. The recurring foot-gun across every
sub-technique below: `map()` returns a distance pre-scaled by the baked
`stepScale = 1/L`, so any comparison against a world-space offset (a
normal-ladder rung, a cone radius, the shadow ray's `y`/`d`) must divide
`stepScale` back out first — the same one-line discipline the existing
soft-shadow march already applies to its `k*h/t` ratio.

## Ambient occlusion

### iq 5-tap normal-ladder AO (calcAO)
- **Source:** Inigo Quilez, *Raymarching Signed Distance Functions*,
  iquilezles.org, n.d. https://iquilezles.org/articles/raymarchingdf/ (exact
  `calcAO` form cross-confirmed via community mirror:
  https://github.com/nicoptere/raymarching-for-THREE `glsl/bits/draw/ao.glsl`).
- **Digest:** From the hit point, step five fixed rungs outward along the
  surface normal with geometric falloff; at each rung compare the expected
  travel `h` against what the field actually reports, and accumulate the
  deficit `(h - d)` as occlusion. Purely local — no cones, no hemisphere, no
  history — but it reads convincingly as contact shadowing in creases and
  under overhangs.
- **Determinism / cross-backend fit:** Yes — pure `map()` calls and float
  arithmetic, no RNG, no screen-space neighbor reads, no temporal history;
  same numeric class as the normals and soft shadows that already pass
  parity. The `(h - d)` term mixes a world-space `h` with a `stepScale`-scaled
  `d`; de-scale `d` by `L` before the subtract or occlusion strength silently
  tracks each program's Lipschitz bake instead of world geometry.
- **Puck verdict:** adopt-now, effort S. Per the deep review: "cheapest
  technique in this whole survey, largest perceptual gain, zero architectural
  disturbance" — 5 extra `map()` calls paid only on surface hits, no ISA/march
  change, wired into the shading epilogue beside the existing normal and
  soft-shadow helpers.
- **See also:** [gradients-and-normals.md](gradients-and-normals.md) (shares
  the tetrahedron-tap seam), [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md)
  (the `stepScale` divide-back), [verdict-index.md](verdict-index.md).

### Multiresolution AO — the ambient-only band-multiply rule
- **Source:** Inigo Quilez, *Multiresolution Ambient Occlusion*,
  iquilezles.org, n.d. https://iquilezles.org/articles/multiresaocc/
- **Digest:** A compositing philosophy, not a single kernel: split occlusion
  into high/medium/low frequency bands (baked-in self-shadowing, a
  screen-space SSAO pass, a blurred shadowmap) and **multiply the product into
  ambient light only, never into direct light** — iq's explicit warning is
  that multiplying occlusion into the whole lighting equation causes ghosting.
- **Determinism / cross-backend fit:** The combining *principle* is
  deterministic and free to adopt. The literal medium-frequency band (a
  dithered screen-space SSAO kernel) is a rasterizer's tool — view-/
  resolution-dependent, typically dither-seeded — and is covered separately
  below since it fights the no-RNG/no-screen-space-history posture.
- **Puck verdict:** already-effectively-have-it as a principle, effort S —
  take only the combining rule (`calcAO` × cone-AO, routed to ambient only)
  in the shading epilogue; do not build the screen-space SSAO or shadowmap
  bands. Literal SSAO+shadowmap pipeline is effort XL and rejected as a poor
  fit (no G-buffer/depth prepass exists to sample).
- **See also:** [verdict-index.md](verdict-index.md).

### UE Distance-Field AO — cone tracing from closest approach
- **Source:** Epic Games, *Distance Field Ambient Occlusion in Unreal
  Engine*, Unreal Engine documentation.
  https://dev.epicgames.com/documentation/en-us/unreal-engine/distance-field-ambient-occlusion-in-unreal-engine
- **Digest:** Traces a small bundle of cones from the shading point and, per
  cone, tracks the closest approach to any occluder (distance-to-field ÷
  distance-travelled — the same min-ratio a soft shadow uses), converting it
  to per-cone occlusion; also emits a bent normal (the direction of least
  occlusion) that redirects the diffuse sky-light term, and can intersect the
  occlusion cone with a roughness-sized reflection cone for approximate
  specular occlusion. UE's field is a cached low-res clipmap; Puck's live
  `map()` is the exact field, so the clipmap-maintenance cost UE pays does not
  apply.
- **Determinism / cross-backend fit:** Yes — fixed cone directions, fixed step
  counts, no RNG, no screen-space reads, same class as soft shadows. The
  closest-approach ratio must de-scale the returned distance by `L` exactly as
  the soft-shadow march already does.
- **Puck verdict:** adopt-when-GPU-bound, effort M. Per the deep review, the
  seam is to factor the soft-shadow inner loop into a shared
  `closestApproach(ro, rd, mint, maxt, k)` primitive that both the sun-shadow
  ray and the cone-AO cones call — "the correct quality tier and the natural
  home of bent normals," but the cheaper 5-tap AO above "captures most of the
  perceptual win at a fraction of the cost."
- **See also:** [march-loop-scheduling.md](march-loop-scheduling.md),
  [verdict-index.md](verdict-index.md).

### Bent normals from the AO cone bundle
- **Source:** Epic Games, *Distance Field Ambient Occlusion in Unreal
  Engine*, Unreal Engine documentation.
  https://dev.epicgames.com/documentation/en-us/unreal-engine/distance-field-ambient-occlusion-in-unreal-engine
  ; also R. Paszkowski, *Bent Normals*, pixelantgames.com (general treatment,
  cited in the broader sweep).
- **Digest:** The average unoccluded direction over the hemisphere — the
  visibility centroid. Sampling ambient/sky light along the bent normal
  instead of the geometric normal pushes the lookup away from nearby
  occluders (a wall, a floor); it is the directional companion to the scalar
  AO factor and UE's primary output feeding the diffuse sky term.
- **Determinism / cross-backend fit:** Yes, same deterministic class as the
  UE cone-AO entry above; the only caveat is cost, since it only exists once
  the cone bundle is paid for.
- **Puck verdict:** pursue as part of the cone-AO entry above, never
  standalone, effort S on top of it. Per the deep review: the 5-tap ladder
  samples only along a single direction and carries no azimuthal information,
  so a bent normal is a cone-bundle feature, not a 5-tap one — accumulate
  `occlusion_i * dir_i` during the same cone loop and normalize; consumer side
  it can also drive a cheap `saturate(dot(bentNormal, lightDir))`
  diffuse-occlusion factor for the engine's colored screen-glow lights,
  reusing one bent normal for all of them rather than marching separate AO
  cones per light.
- **See also:** [verdict-index.md](verdict-index.md).

### iq analytic sphere / box occluders
- **Source:** Inigo Quilez, *Sphere Functions*, iquilezles.org, n.d.
  https://iquilezles.org/articles/spherefunctions/ ; Inigo Quilez, *Box
  Functions*, iquilezles.org, n.d. https://iquilezles.org/articles/boxfunctions/
- **Digest:** Closed-form AO (and soft shadow) for a *known* primitive with no
  sampling: `sphOcclusion` returns the exact occlusion a single sphere casts,
  `boxOcclusion` sums an oriented box's 12 edges' solid-angle contributions
  normalized by 2π. Exact and noise-free, but each assumes the occluder is a
  single, fully-visible analytic shape.
- **Determinism / cross-backend fit:** Yes — pure analytic float math, no
  sampling, no RNG; the most deterministic option on this page.
- **Puck verdict:** pursue when GPU-bound, and only for designated
  hero-occluder niche, effort M–L. Per the deep review, Puck's world is an
  arbitrary min/smin-composited VM program, not a short list of analytic
  spheres/boxes — applying these per-instance would double-count overlaps and
  misses smooth-union blending entirely; they only pay off for a small,
  flagged set of non-overlapping hero instances (a ground sphere, one big
  planet, a dominant column), and the cost is mostly the param-extraction
  plumbing through the instance directory, not the math.
- **See also:** [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md)
  (instance-directory plumbing), [verdict-index.md](verdict-index.md).

### Cone / horizon multi-scale AO (XeGTAO)
- **Source:** GameTechDev, *XeGTAO*, github.com/GameTechDev/XeGTAO (screen-
  space analog of cone/horizon-based multi-scale AO, cf. GTAO horizon search
  ported to field queries). https://github.com/GameTechDev/XeGTAO
- **Digest:** Widens the sample cone/step schedule with distance instead of
  the fixed 5-tap ladder, so occlusion captures larger nearby occluders at the
  cost of more bounded `map()` evaluations per pixel; no scene-wide structure
  required.
- **Determinism / cross-backend fit:** Yes, per the sweep — same
  local-field-query character as the 5-tap ladder, no accumulation buffer.
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [verdict-index.md](verdict-index.md).

### iq screen-space SSAO band
- **Source:** Inigo Quilez, *Screen Space Ambient Occlusion*, iquilezles.org,
  ~2004–2005 (one of the first public SSAO write-ups).
  https://iquilezles.org/articles/ssao/ ; the band's role in the three-band
  composite: Inigo Quilez, *Multiresolution Ambient Occlusion*, iquilezles.org,
  n.d. https://iquilezles.org/articles/multiresaocc/
- **Digest:** Depth-buffer reconstruction plus N sample points in a sphere
  around the shading point, projected back to screen space, with
  distance-dependent quadratic attenuation, dithered sampling, and an
  edge-preserving blur. In iq's three-band composite this is the literal
  medium-frequency band — a dithered, ~16-sample screen-space AO kernel
  blended under the high-frequency baked term and the low-frequency
  blurred-shadowmap term.
- **Determinism / cross-backend fit:** Caveat — view-/resolution-dependent,
  typically dither-seeded; would need PCG3D-hashed offsets to stay
  reproducible under Puck's no-RNG, no-screen-space-history posture, and
  assumes a depth prepass/G-buffer Puck's per-pixel interpreter doesn't keep.
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [verdict-index.md](verdict-index.md).

## Soft shadows

### iq classic penumbra (2010) + Aaltonen 2017 closest-approach refinement
- **Source:** Inigo Quilez, *Soft Shadows in Raymarched SDFs*,
  iquilezles.org, 2010 (updated). https://iquilezles.org/articles/rmshadows/
  (classic `res = min(res, k*h/t)` form; improved closest-approach form
  attributed within the article to Sebastian Aaltonen, GDC 2017).
- **Digest:** The classic form takes one penumbra estimate per march step —
  the ratio of the closest miss `h` to distance travelled `t` — but only
  samples the miss *at* discrete step positions, so at a sharp occluder edge
  the true closest approach falls *between* steps and produces
  step-frequency banding/light-leaking. Aaltonen's refinement treats the
  previous and current SDF samples (`ph`, `h`) as a local parabola,
  recovering the perpendicular miss distance (`y = h*h/(2*ph)`,
  `d = sqrt(h*h - y*y)`, `res = min(res, d/(w*max(0,t-y)))`) at the estimated
  closest point between samples — removing the banding at effectively the
  same per-step cost (two extra ops plus one `sqrt`, no extra SDF
  evaluations).
- **Determinism / cross-backend fit:** Yes — `sqrt` and the extra divide are
  both IEEE-754 correctly-rounded on both backends, so the op set is
  backend-safe; the ratio is more numerically sensitive near `t≈y` (grazing
  rays at silhouettes) than the classic form, expect that penumbra ring to be
  the parity hotspot to watch under `PUCK_PARITY_STRICT=1` (it stays within
  relaxed thresholds). **The stepScale divide-back is the load-bearing
  requirement here**: `y` and `d` must be computed from the *true*, unscaled
  SDF value, then the existing `stepScale` divide-back applies to the final
  `d/(w*(t-y))` ratio — mixing scaled and unscaled quantities here reintroduces
  the ~30% darkening bug a prior fix already closed.
- **Puck verdict:** adopt-if-classic / confirm-and-bank-if-already-improved,
  effort S either way. Per the deep review, Puck's shipping shadow form was
  unverified at review time — the concrete first task is a read-and-classify
  of the shadow loop to pick the S-upgrade branch (add a `ph` register,
  replace the `res` update, re-tune the softness constant) vs. the S-confirm
  branch (three-point inspection: `ph` seeded large on the first step,
  `max(0,t-y)` guards the grazing divide, divide-back operates on unscaled
  `y`/`d`). The discriminating verification is a sharp-corner occluder scene
  (a cube edge casting onto a plane) — classic shows step-frequency banding,
  improved shows a smooth gradient — run via the demo, not a new Post gate.
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md)
  (the `stepScale` divide-back invariant), [verdict-index.md](verdict-index.md).

## Curvature & NPR

### Curvature shading + field-gradient outlines
- **Source:** shaderfun, *Signed Distance Fields Part 8: Gradients, bevels
  and noise*, shaderfun.com, 2018.
  https://shaderfun.com/2018/07/23/signed-distance-fields-part-8-gradients-bevels-and-noise/
- **Digest:** Builds on finite-difference field gradients — a first ring of
  taps gives the gradient (surface normal), used for bevels and edge-finding.
  A second finite-difference ring on the same tap infrastructure yields the
  field's discrete Laplacian, which for a metric SDF (`|∇d| ≈ 1`)
  approximates mean curvature of the level set: concave creases read
  negative, convex ridges/silhouettes read positive. That signal drives
  cavity darkening, rim/edge highlights, and ink-line outlines where
  `|curvature|` spikes or `|∇d|` collapses.
- **Determinism / cross-backend fit:** Yes — finite differences over `map()`
  are pure float arithmetic, no RNG, no screen-space reads, no history,
  identical class to the normal computation that already passes parity.
  Benign caveat: curvature of the `stepScale`-scaled field is `L`-scaled
  uniformly (`∇²(d·stepScale) = stepScale·∇²d`); an artistic curvature signal
  can leave this and just retune the gain constant, a world-unit signal
  divides by `stepScale` — either way it stays deterministic and
  cross-backend identical.
- **Puck verdict:** adopt-now (cavity + outlines), effort S for cavity+rim,
  S–M if outlines want their own tuned edge signal and per-object ink
  parameters. Per the deep review, the key economy is that Puck **already
  computes a 4-tap tetrahedron normal at every hit**, and those offsets are
  exactly a symmetric ring around the point — a discrete Laplacian reuses the
  same four taps plus the center hit distance already fetched, so curvature
  is "≈ free" (a few adds, no new `map()` calls); it rides the existing
  shading epilogue, no ISA or march-loop change, gated behind per-run shading
  toggle bits since it is stylized shading, not physically based.
- **See also:** [gradients-and-normals.md](gradients-and-normals.md) (the
  shared tetrahedron-tap seam), [verdict-index.md](verdict-index.md).

### NPR toon shading & field-gradient outlines
- **Source:** shaderfun, *Signed Distance Fields Part 8: Gradients, bevels
  and noise*, shaderfun.com, 2018.
  https://shaderfun.com/2018/07/23/signed-distance-fields-part-8-gradients-bevels-and-noise/
  ; Red Blob Games, *Signed Distance Field Fonts*, 2024 (applied conceptually
  to 3D silhouettes). https://www.redblobgames.com/x/2403-distance-field-fonts/
- **Digest:** Quantizes the lit result into discrete toon steps and/or
  thresholds `|gradient|`/normal-discontinuity near silhouettes to draw
  outlines directly from the field, costing almost nothing beyond values
  already computed for shading.
- **Determinism / cross-backend fit:** Yes, per the sweep — pure per-pixel
  function of existing field/gradient data, same character as the curvature
  entry above.
- **Puck verdict:** surveyed, not deep-reviewed (see the curvature entry
  above for the deep-reviewed ink-line outline design that rides the same
  tetrahedron taps).
- **See also:** [verdict-index.md](verdict-index.md).

## Subsurface & materials

### Interior-distance thickness for cheap SSS (Claybook-style)
- **Source:** Sebastian Aaltonen, *Claybook: GPU-Based Clay*, GDC 2018.
  https://ubm-twvideo01.s3.amazonaws.com/o1/vault/gdc2018/presentations/Aaltonen_Sebastian_GPU_Based_Clay.pdf
- **Digest:** Casts a short secondary ray into the surface (a negative-side
  SDF march) to measure local thickness, then uses that thickness to
  attenuate a transmittance term — a translucency approximation with no
  multi-bounce cost, purpose-built for a fully-SDF scene.
- **Determinism / cross-backend fit:** Yes, per the sweep — a bounded
  secondary march per shading point, no accumulation, same deterministic
  class as the soft-shadow march.
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [materials-and-primitives.md](materials-and-primitives.md),
  [verdict-index.md](verdict-index.md).

### Triplanar mapping on marched surfaces
- **Source:** General technique; e.g. Godot smooth-terrain documentation.
  https://voxel-tools.readthedocs.io/en/latest/smooth_terrain/
- **Digest:** Projects texture/material lookups along three axis planes and
  blends by normal-alignment weight, avoiding stretching since CSG-blended
  SDF surfaces have no native UVs; pure per-pixel texture-fetch cost.
- **Determinism / cross-backend fit:** Yes, per the sweep — deterministic
  function of hit position and normal.
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [materials-and-primitives.md](materials-and-primitives.md)
  (the material-id/blend-factor plumbing this would sit on top of),
  [verdict-index.md](verdict-index.md).

## Volumetrics & GI-ish

### Distance-field density volumetrics
- **Source:** Xor, *Volumetric Raymarching*, GM Shaders.
  https://mini.gmshaders.com/p/volumetric
- **Digest:** Replaces the binary surface SDF with a density field derived
  from the same distance function (a smoothstep around zero, or
  signed-distance-weighted falloff for fog hugging geometry), then
  accumulates transmittance/in-scattered light along the ray — generalizes
  Puck's per-screen glow-light fog term to SDF-shaped volumetric density.
- **Determinism / cross-backend fit:** Yes if driven by integer-hash noise
  for density breakup, per the sweep; explicitly avoid the jittered-start-
  per-frame trick common in volumetrics tutorials, which is temporal and
  incompatible with Puck's no-history posture.
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [verdict-index.md](verdict-index.md).

### Distance-field light culling for many emitters
- **Source:** Generalizes Epic Games' Distance Field structures toward
  light-side culling (no single dedicated paper in the corpus — a sweep-
  report generalization). Base structural reference: Epic Games, *Distance
  Field Ambient Occlusion in Unreal Engine*, Unreal Engine documentation.
  https://dev.epicgames.com/documentation/en-us/unreal-engine/distance-field-ambient-occlusion-in-unreal-engine
- **Digest:** Uses the SDF the renderer already queries to early-reject or
  falloff-scale emitters whose influence sphere doesn't reach a shading
  point — the most direct generalization of Puck's existing colored
  screen-glow-light system to a larger emitter count.
- **Determinism / cross-backend fit:** Yes, per the sweep — a deterministic
  bounds test against the existing field/light list, no new state.
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [verdict-index.md](verdict-index.md).

### SDF-based dynamic diffuse GI (probe-relit)
- **Source:** *Signed Distance Fields Dynamic Diffuse Global Illumination*,
  arXiv:2007.14394, 2020. https://arxiv.org/pdf/2007.14394
- **Digest:** Irradiance probes relit each frame by short SDF-traced
  visibility rays (an SDF-native DDGI/RTXGI analog), giving single-/
  multi-bounce diffuse GI far cheaper than path tracing.
- **Determinism / cross-backend fit:** Caveat, per the sweep — the DDGI
  family blends new probe samples into a running estimate across multiple
  frames (temporal history), which conflicts with Puck's no-history
  constraint; would need a single-frame-converged variant to be adoptable at
  all. See [negative-results-and-rejections.md](negative-results-and-rejections.md)
  for the corpus's broader temporal-history family rejection.
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [negative-results-and-rejections.md](negative-results-and-rejections.md)
  (the temporal-history family rejection this falls under), [verdict-index.md](verdict-index.md).

### Dreams screen-space AO/GI layer
- **Source:** Alex Evans (Media Molecule), SIGGRAPH 2015 Advances course /
  GDC 2016 recap. https://www.mediamolecule.com/blog/article/dreams_at_gdc_2016
- **Digest:** Layers a screen-space pass for small-scale AO, reflections, and
  single-bounce indirect on top of the primary SDF-derived point-cloud hit —
  "cheap SDF-local AO/GI" treated as a composited screen-space layer rather
  than baked into the primary shading loop.
- **Determinism / cross-backend fit:** Caveat, per the sweep — Dreams uses
  temporal accumulation for the indirect layer specifically; the single-
  bounce screen-space idea is fine per-frame with no history, but the
  production form as shipped relies on history and needs the same
  single-frame redesign as the DDGI entry above.
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [negative-results-and-rejections.md](negative-results-and-rejections.md)
  (the temporal-history family rejection this falls under), [verdict-index.md](verdict-index.md).
