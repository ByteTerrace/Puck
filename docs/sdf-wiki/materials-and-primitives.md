# Materials and Primitives

Puck's ISA and blend-table story so far has been distance-only: smooth-min
blends merge two candidates into one surface but the material id still snaps
at the geometric midpoint, and the primitive catalog covers only the 2D shapes
already lifted into 3D. This page tracks the material-channel seam that rides
on the existing smin `h` (the one deep-reviewed technique here), the wider
smin/domain-operator libraries it's drawn from, and the primitive-catalog and
text-rendering backlog around it.

## Material blending

### Material blend factor at seams (smooth-min h reuse)
- **Source:** Inigo Quilez, *Smooth Minimum* (2024 rewrite), iquilezles.org. https://iquilezles.org/articles/smin/
- **Digest:** iq's smin taxonomy splits into DD (direct-difference: exponential, root, sigmoid kernels) and CD (clamped-difference: quadratic, cubic, quartic, circular exact/polynomial) families. CD kernels are constrained so `g(-1)=g'(-1)=0` and `g(1)=g'(1)=1`, giving zero blend influence beyond `k` units apart — compact support. The CD quadratic/cubic forms return a two-output `vec2(distance, m)`, where `m` is a material blend weight in `[0,1]` built from the *same* already-clamped `h` that positions the distance blend — no extra transcendental, just a reshuffle of an existing value. Puck's deployed `lerp(a,b,1-h)` is the CD-quadratic form.
- **Determinism / cross-backend fit:** CD compact support gives `m` exactly 0 or 1 at the blend-band endpoints — the same far-exact guarantee the distance line already has, so mask-cull stays bit-exact. Inside the band, `m` is a smooth function of the LSB-scale difference `a-b` and stays within the relaxed mean≤0.35 parity threshold; near the band edge the clamp can flip which side of zero a backend lands on, a bounded ε-scale seam-ring disagreement that could trip `PUCK_PARITY_STRICT=1` but not the relaxed gate. The DD family is **rejected outright** for any Puck blend opcode — non-compact support means no bit-exact endpoint, which would break far-exact mask-cull (see negative-results-and-rejections.md).
- **Empirical status (Puck): Xor unmaskable-exemption CONFIRMED EXEMPT** (2026-07-08 in-demo correctness hunt, real-GPU slice comparison) — Xor's exterior field is bit-identical to union, so its mask-cull exemption holds; the one residual is a documentation note (its cull-bound *sizing* is union-like, needing an influence margin, not subtraction-like). Details on [tape-pruning-and-inclusion.md](tape-pruning-and-inclusion.md) and in [verdict-index.md](verdict-index.md#empirical-status-in-puck).
- **Puck verdict:** Adopt — shade-funnel option first (review-05): the march runs unchanged (single winner distance, far-exact); only at the confirmed hit do we re-evaluate the winning blend node's two operands, recover `h`/`m`, and lerp their palette albedos, transported through the existing `parityMaterialDelta` channel so the color path keeps its own relaxed tolerance rather than forcing a new threshold on the main albedo output. Effort **M** — new shade-stage decode of one blend node plus threshold routing, no hot-loop or ISA change. The carried-weight variant (widen `SdfResult` to carry `(distance, materialA, materialB, weight)` through the whole blend tree, correct through arbitrarily deep nesting) is deferred until nested-blend material bleeding is actually demanded — effort **L**, since it widens the hot per-step path and needs a weight-reduction rule for stacked blends. DD-family kernels are rejected outright, not deferred.
- **See also:** [negative-results-and-rejections.md](negative-results-and-rejections.md), [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

## Blend & domain operator libraries

### Smooth Minimum — full taxonomy (polynomial / exponential / root / circular)
- **Source:** Inigo Quilez, *Smooth Minimum* (invention 2011–2012, published 2013; Media Molecule optimization ~2015; 2024 rewrite), iquilezles.org. https://iquilezles.org/articles/smin/
- **Digest:** Beyond the CD-quadratic material-factor form deep-reviewed above, the same article catalogs exponential, root/power, and sigmoid/logistic smins (all DD, non-compact support) alongside circular-exact and circular-polynomial variants (CD, compact support). Each family trades the character/sharpness of the blend curve for evaluation cost; per-kernel `k` normalization factors differ (quadratic `k*=4`, cubic `k*=6`, quartic `k*=16/3`).
- **Determinism / cross-backend fit:** Sweep verdict: pure arithmetic, deterministic on both backends. Only the CD members retain the far-exact endpoint property the material-blend entry above depends on; DD members do not and are excluded from consideration for any Puck blend opcode.
- **Puck verdict:** Surveyed, not deep-reviewed — only the CD-quadratic material-factor form received a phase-3 deep review. The rest of the taxonomy (circular CD variants, alternate normalizations) is backlog for whenever a different blend "character" is wanted; the DD half is a standing rejection, not backlog.
- **See also:** [negative-results-and-rejections.md](negative-results-and-rejections.md), [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### hg_sdf blend/domain operator library
- **Source:** Johann Korndörfer ("cupe," Mercury Demogroup), hg_sdf library and "The Timeless Way of Building Geometry" talk, NVScene 2015. https://mercury.sexy/hg_sdf/
- **Digest:** A widely-reused GLSL/HLSL "profile at the seam" family distinct from Puck's smooth/chamfer-minimum family: `fOpUnionColumns` (columnar/fluted ridge merges), `fOpUnionStairs` (stepped-join blend), `fOpIntersectionChamfer`, `fOpPipe` (hollow tube along the intersection seam), `fOpEngrave` (carved recess), and `fOpGroove`/`fOpTongue` (channel/ridge protrusion variants). All are closed-form min/max/clamp compositions rather than smooth-min reformulations.
- **Determinism / cross-backend fit:** Sweep verdict "yes, caveat" — closed-form arithmetic should port cleanly, but each operator needs its own individual distance-bound/Lipschitz re-derivation before it can be trusted in the march; none of these ship with a published Lipschitz proof the way iq's smin/primitives do.
- **Puck verdict:** Surveyed, not deep-reviewed. Deferred until authoring catches up — each op needs its own halo/Lipschitz re-derivation before it can land as an ISA opcode, and none has had that derivation done yet.
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [march-loop-scheduling.md](march-loop-scheduling.md), [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

## Primitive catalog gaps

### 2D SDFs — extended primitive list
- **Source:** Inigo Quilez, *2D Distance Functions*, ongoing reference, iquilezles.org. https://iquilezles.org/articles/distfunctions2d/
- **Digest:** Exact closed-form 2D distance functions beyond Puck's currently lifted 2D family: heart, cross (+ rounded), pie, arc, ring, horseshoe, moon, cut disk, uneven capsule, hexagram/pentagram, quadratic Bézier, parabola (+ segment), hyperbola, "Cool S," stairs, and circle-wave. All are revolve/extrude-liftable through Puck's existing 2D→3D pipeline the same way the current 2D primitive family was.
- **Determinism / cross-backend fit:** Sweep verdict "yes" — all closed-form exact functions, pure arithmetic, no nondeterminism introduced by adding any of them.
- **Puck verdict:** Surveyed, not deep-reviewed. A mechanical backlog for primitive-catalog expansion — no blocking design question, just authoring/ISA-opcode time per shape.
- **See also:** [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Interior SDFs
- **Source:** Inigo Quilez, *Interior SDFs*, undated, iquilezles.org. https://iquilezles.org/articles/interiordistance/
- **Digest:** `min()`-based unions give correct distance *outside* a compound shape but wrong-sign or wrong-magnitude distance *inside* it — a defect invisible to surface rendering but load-bearing for anything that samples interior distance. Lays out five escalating fixes: ignore it (Puck's implicit current behavior); model negative space explicitly; maintain a dual internal/external field; a 2D-contour-based exact-interior method; hand-authored exact composite primitives.
- **Determinism / cross-backend fit:** Sweep verdict "yes" for the deterministic fixes (dual-field, contour-based); "ignore it" is a no-op describing current behavior rather than a new technique to adopt.
- **Puck verdict:** Surveyed, not deep-reviewed. Matters once subsurface scattering, volumetrics, or physics/collision start sampling interior distance — no current Puck consumer needs it yet, so it stays a watch item rather than a scheduled change.
- **See also:** [shading-ao-shadows.md](shading-ao-shadows.md), [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

## Text

### Improved Alpha-Tested Magnification for Vector Textures (MSDF lineage)
- **Source:** Chris Green (Valve), SIGGRAPH 2007 Advances in Real-Time Rendering course; shipped in Team Fortress 2. https://steamcdn-a.akamaihd.net/apps/valve/2007/SIGGRAPH2007_AlphaTestedMagnification.pdf
- **Digest:** Represents a glyph or vector-art shape as a low-resolution single-channel signed-distance coverage texture, alpha-tested or smoothstepped at arbitrary magnification without the blur/aliasing naive bilinear-filtered alpha textures show; cheap outline/glow/drop-shadow effects fall out as distance-threshold offsets on the same texture. This is the foundational reference the single-channel-SDF-to-multi-channel-SDF (MSDF) font-rendering lineage builds on.
- **Determinism / cross-backend fit:** Sweep verdict "yes, trivially" — a texture sample plus a smoothstep, deterministic given a deterministic distance-field texture and sampler state.
- **Puck verdict:** Surveyed, not deep-reviewed. MSDF UI text is on the roadmap and this is the classic reference to build from. Note: Viktor Chlumsky's msdfgen — the standard modern multi-channel SDF generator/tooling that extends this technique — did not surface in the corpus sweep; that is a coverage gap in the research, not a finding about the technique itself.
- **See also:** [antialiasing-and-filtering.md](antialiasing-and-filtering.md), [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

## See also

- [negative-results-and-rejections.md](negative-results-and-rejections.md) — the
  DD-family smin rejection recorded in full alongside the other rejected
  techniques.
- [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md) —
  the Lipschitz/halo re-derivation every hg_sdf operator needs before it can
  become an ISA opcode.
- [gradients-and-normals.md](gradients-and-normals.md) — the smooth-blend
  gradient (`mix(∇b, ∇a, h)`) reuses the same `h` this page's material-blend
  entry reuses.
- [verdict-index.md](verdict-index.md) — this page's deep-reviewed verdict in
  the all-techniques table.
- [../sdf-sota-survey.md](../sdf-sota-survey.md) — the ranked decision
  shortlist this wiki is a reference companion to.
