# Antialiasing and Filtering

Puck ships only ordered dither today — no silhouette antialiasing and no
texture-footprint filtering — so hard edges crawl frame to frame and screen
slabs viewed at a glance or a distance shimmer. This page covers two
independent, deterministic fixes that both reuse machinery the SDF VM already
computes per pixel: coverage-based silhouette AA landing in the march
epilogue and the composite pass, and ray-differential texture filtering
landing at the textured-hit sampling seam (the CRT/screen slabs).

### Coverage-via-pixel-footprint AA ("antialias, sort of")
- **Source:** Inigo Quilez, *Raymarching Distance Fields* (the "antialias,
  sort of" section), iquilezles.org, 2014 (updated); companion Shadertoy
  reference. https://iquilezles.org/articles/raymarchingdf/ ,
  https://www.shadertoy.com/view/llXGR4.
- **Digest:** At the closest approach of a ray that misses or grazes a
  silhouette, `map(p)` returns a small nonzero distance; compared against the
  pixel-cone footprint at that depth (`pixelRadius * traveled` — exactly the
  quantity Puck already forms for its adaptive epsilon), that proximity is
  partial coverage of the pixel's solid angle. iq's framing composites these
  per-near-intersection coverage values **front-to-back** for a smooth edge
  without supersampling; the "sort of" hedge is honest — it's an
  approximation of the silhouette boundary, not shading or sub-pixel
  topology.
- **Determinism / cross-backend fit:** `coverage` is a pure function of `d`,
  `traveled`, `pixelRadius`, and `stepScale` (divided back out — `d` is
  Lipschitz-scaled, the footprint is world-space) plus `smoothstep`; per the
  engine's ±1-LSB cross-backend guarantee this perturbs the result by a few
  LSB, well inside the relaxed parity threshold (mean ≤ 0.35). Under
  `PUCK_PARITY_STRICT=1` a few-LSB delta at a high-contrast edge can flip a
  final 8-bit channel, so STRICT mode quantizes coverage to N discrete levels
  before the blend (mirroring the engine's existing dither/fuzz-threshold
  quantization discipline) rather than shipping raw float coverage.
- **Puck verdict:** Track A, adopt-now, effort **S** — "a min-ratio
  accumulator plus a smoothstep — essentially free, no extra marching, no
  hardware derivatives" (review-09). Track a running `minRatio = min(d/
  (pixelRadius*traveled))` across the march (one `min` + one `div` per step,
  no extra `map()` calls), then `coverage = clamp(minRatio, 0, 1)` at loop
  exit; blend against sky (Tier 0, free) is the recommended first landing,
  gating a one-hit continuation to blend against farther geometry (Tier 1,
  edge-pixels only, ~1.2–1.5× march cost on that subset) as a follow-up. Full
  front-to-back accumulation across many grazes (Tier 2) is **not
  recommended** — out of proportion to opaque silhouette AA. Dither must run
  *after* the coverage blend, at final 8-bit quantization, or it smears along
  the reconstructed edge instead of smoothing the coverage ramp.
- **Empirical status (Puck): premise corrected, rebuilt 2026-07-08** (commit
  `37f8b65`, in-demo correctness hunt). "Free coverage from the min-ratio
  accumulator" does not hold under Puck's footprint-adaptive marcher — the
  min-ratio saturates to ~1 on every solid hit (the termination criterion
  guarantees it), and a naive along-ray forward probe used to compensate
  misfired on grazing-but-solid surfaces (footprint-quantized scaliness across
  lit flanks, the floor washed to sky). The shipped Tier-0 form instead derives
  coverage from the terminal-step residual in the march's own clamped/
  termination-consistent units, gated by a normal-facing clamp and a relative
  (not absolute) rising-field forward tap; the per-step min-ratio tracking was
  removed from the hot march loop. Deliberately subtle — corner-pixel
  softening only. Full record: [../sdf-sota-survey.md](../sdf-sota-survey.md)
  (R9) and [verdict-index.md](verdict-index.md#empirical-status-in-puck).
- **See also:** [shading-ao-shadows.md](shading-ao-shadows.md),
  [marching-acceleration.md](marching-acceleration.md),
  [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md),
  [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Analytical Anti-Aliasing (exact 2D coverage)
- **Source:** frost.kiwi, *Analytical Anti-Aliasing*, blog.frost.kiwi, 2024.
  https://blog.frost.kiwi/analytical-anti-aliasing/.
- **Digest:** For an exact 2D shape, coverage is a one-pixel-wide smoothstep
  band centered on the zero isocontour —
  `coverage = smoothstep(-pixelRadius, +pixelRadius, d)` — which analytically
  integrates the shape's occupancy of the pixel with no sampling and no
  MSAA/SSAA multiplication. The article is explicit that this is *exact in
  2D* and frays in 3D/raymarching: at silhouettes the derivative is
  unreliable, occlusion isn't analytically integrable, and depth makes the
  pixel-size estimate depth-dependent — plus it doesn't capture thin/
  sub-pixel or interior detail.
- **Determinism / cross-backend fit:** Same profile as the coverage-via-
  footprint technique above — pure arithmetic (smoothstep of a signed
  distance), a few-LSB cross-backend delta absorbed by relaxed parity;
  STRICT mode needs coverage quantized to N levels before the blend.
- **Puck verdict:** adopt-now as part of Track A, effort **S** — review-09's
  synthesis is that this and iq's coverage idea "are the same underlying
  identity — coverage = a smoothstep of signed distance against the pixel
  footprint — approached from raymarching and from 2D SDF text/shape
  rendering," and that Puck's `pixelRadius * traveled` **already is** the 3D
  pixel footprint this article identifies as the hard part of extending the
  idea past 2D. No separate implementation track from the entry above; this
  source supplies the exact 2D form and the honest scope limits.
- **See also:** [marching-acceleration.md](marching-acceleration.md),
  [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md),
  [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Supporting coverage-AA writeups
- **Source:** reindernijhoff, *Raymarching Distance Fields*, 2017.
  https://reindernijhoff.net/2017/07/raymarching-distance-fields/ ; Musing
  Mortoray, *Antialiasing with a signed distance field*.
  https://mortoray.com/antialiasing-with-a-signed-distance-field/ ;
  shadergif.com, *Anti-Aliasing Basics for Procedural Shapes (GLSL)*.
  https://shadergif.com/guides/anti-aliasing-basics/.
- **Digest:** Three independent restatements of the same coverage identity:
  reindernijhoff uses ray differentials/pixel footprint at the near-
  intersection to estimate fractional silhouette occupancy composited
  front-to-back ("essentially free since the SDF value at the hit encodes
  sub-pixel proximity"); Mortoray and shadergif work the 2D
  `fwidth`-driven edge-smoothing case for SDF/procedural shapes. None
  introduce a mechanism beyond what iq's raymarching article and frost.kiwi's
  AAA already contribute — they corroborate the same footprint-smoothstep
  identity from different angles.
- **Determinism / cross-backend fit:** Same character as the primary
  sources — a deterministic function of hit distance and screen-space
  footprint, no temporal history or accumulation buffer.
- **Puck verdict:** surveyed, not deep-reviewed — cited in review-09 as
  corroborating evidence for Track A rather than reviewed as an independent
  design; no separate effort estimate.
- **See also:** [marching-acceleration.md](marching-acceleration.md),
  [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Ray Differentials and Textures (raymarch texture filtering)
- **Source:** Inigo Quilez, *Ray Differentials and Textures*, iquilezles.org.
  https://iquilezles.org/articles/filteringrm/.
- **Digest:** Raymarching breaks the GPU's screen-space locality assumption —
  adjacent pixels can hit different objects, so hardware `dFdx`/`dFdy` on the
  hit position produce garbage mip selection at object boundaries (Puck has
  no hardware derivatives at all, so this is doubly relevant). The fix:
  shoot the neighbor rays for pixel+1x/pixel+1y and intersect each with the
  **tangent plane** at the primary hit (`(p - pos)·nor = 0`); the closed-form
  perspective formulas `dposdx = t*(rdx*dot(rd,nor)/dot(rdx,nor) - rd)` (and
  the `y` analogue) give the world-space pixel footprint on the surface,
  chain-ruled through the UV mapping into `duvdx`/`duvdy` for
  `textureGrad`/`SampleGrad` — analytic, no hardware derivatives required.
- **Determinism / cross-backend fit:** `dposdx`/`dposdy` are pure functions
  of `rd`, `rdx`, `rdy`, `nor`, `t` — all ±1-LSB cross-backend already. They
  feed mip LOD selection, which is inherently quantized (integer mip +
  fractional blend), so ±1-LSB derivative noise almost never changes the
  selected mip pair; relaxed parity absorbs it. The one hazard is a footprint
  landing exactly on a mip boundary (`log2` = integer), which STRICT mode
  handles by snapping/rounding the computed LOD before `SampleLevel`, same
  discipline as coverage quantization. Low risk overall.
- **Puck verdict:** Track B, adopt-second, effort **M** — "the highest-
  *certainty* win in the whole review" (review-09): Puck already computes
  the per-pixel camera ray setup and hit normal, so the neighbor rays are a
  trivial re-evaluation of the existing ray generator (no marching), and both
  SPIR-V and DXIL expose explicit-gradient sampling. Fixes the CRT/screen-
  slab moire and shimmer on angled or distant screens — a concrete,
  screenshot-visible win on the demo's hero asset. The prerequisite that gates
  the benefit: the sampled screen-source textures (emulator framebuffer,
  SDF-baked art) need generated mip chains, or `textureGrad` has nothing to
  select and falls back to bilinear regardless — a pipeline/upload change,
  not a kernel change, but on the critical path.
- **See also:** [materials-and-primitives.md](materials-and-primitives.md),
  [gradients-and-normals.md](gradients-and-normals.md),
  [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).
