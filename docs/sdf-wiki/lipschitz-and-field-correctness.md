# Lipschitz & Field Correctness

The whole march loop rests on one guarantee: `map()` returns a distance that is
*never larger* than the true distance to the surface, scaled by a known
Lipschitz bound `L` (`stepScale = 1/L`), so stepping by `stepScale * d` can
never skip a surface. `AnalyzeLipschitz` is the compiler seam that proves this
per-op — a closed-form norm attached to every ISA opcode so the baked program
carries a provably-safe `stepScale` rather than a hand-tuned fudge factor. This
page catalogs the theory of what keeps a combinator, a fold, or a synthesized
detail layer inside that conservative-bound contract, and where the published
literature runs out.

### hg_sdf exactness taxonomy

- **Source:** Mercury (demogroup), *hg_sdf: A GLSL Library for Building Signed Distance Functions*, 2016. https://mercury.sexy/hg_sdf/.
- **Digest:** A practitioner-level classification of SDF combinators into exact-distance vs. conservative-*bound* buckets — the library's own framing is blunt: "underestimating distances will happen — that's why calling it a distance bound is more correct." States the working invariant used to judge whether a combinator stays safe: gradient magnitude must not exceed 1.
- **Determinism / cross-backend fit:** A classification, not an algorithm — no backend implications on its own. The `|grad f| <= 1` invariant it states is exactly the property `AnalyzeLipschitz` proves per-op in Puck's compiler.
- **Puck verdict:** surveyed, not deep-reviewed — a checklist of exact-vs-bound buckets and primitive-level edge cases worth turning into regression tests.
- **See also:** [README.md](README.md), [materials-and-primitives.md](materials-and-primitives.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### iq "Signed Distance Functions" exactness catalog

- **Source:** Inigo Quilez, *Signed Distance Functions* (distfunctions / distfunctions2d), ongoing since ~2015, updated through 2023. https://iquilezles.org/articles/distfunctions/, https://iquilezles.org/articles/distfunctions2d/.
- **Digest:** The canonical exact-distance primitive catalog, annotated with the cases where elongation or rounding leave a small non-exact "flat core" inside a shape (still a conservative *underestimate* — just not exact anymore). States the 2D→3D lifting inheritance rule: an exact 2D SDF revolved or extruded into 3D stays exact.
- **Determinism / cross-backend fit:** Pure closed-form math with no backend-specific implications.
- **Puck verdict:** surveyed, not deep-reviewed — validates the lifted-2D-primitive family Puck already ships; the elongation flat-core caveat is a candidate regression test for any primitive combined with an Elongate op.
- **See also:** [README.md](README.md), [materials-and-primitives.md](materials-and-primitives.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Operations on Signed Distance Function Estimates

- **Source:** Bálint, Valasek, Gergő, *Operations on Signed Distance Function Estimates*, Computer-Aided Design & Applications 20(6), 2023. https://www.cad-journal.net/.
- **Digest:** A formal treatment of why CSG min/max and Lipschitz-division do not preserve exactness. Introduces "set-contact smoothness" — a generalized intersection-angle metric — to quantify precision loss per-operation and per-region, and proves the conditions under which sphere-tracing convergence stays closed under CSG composition.
- **Determinism / cross-backend fit:** A compile-time error-analysis framework; no runtime float-order implications of its own.
- **Puck verdict:** surveyed, not deep-reviewed — a candidate for sharpening the compiler's per-op norm tracking with a principled per-node error metric instead of worst-case constants; the compile-time analysis fits `AnalyzeLipschitz`'s existing architecture.
- **See also:** [README.md](README.md), [../sdf-sota-survey.md](../sdf-sota-survey.md), [verdict-index.md](verdict-index.md).

### Exact SDF Representation of Polygons

- **Source:** Bálint, *Exact Signed Distance Function Representation of Polygons*, Computer-Aided Design & Applications 20(5), 2023. https://www.cad-journal.net/.
- **Digest:** Derives a genuinely exact SDF for arbitrary polygons, correcting the naive polygon SDF construction that is typically only conservative (not exact) near concave vertices.
- **Determinism / cross-backend fit:** Closed-form math; no backend-specific implications noted in the corpus.
- **Puck verdict:** surveyed, not deep-reviewed — relevant only if a general 2D polygon primitive is added to the ISA; not applicable to the current lifted-2D-primitive set.
- **See also:** [README.md](README.md), [materials-and-primitives.md](materials-and-primitives.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Higher Order Algebraic Signed Distance Fields

- **Source:** Valasek, Bán, *Higher Order Algebraic Signed Distance Fields*, Computer-Aided Design & Applications 20(5), 2023. https://www.cad-journal.net/.
- **Digest:** Builds higher-order (beyond first-derivative/gradient) local Taylor approximations of a field with a closed-form error bound, closed under convex barycentric combination.
- **Determinism / cross-backend fit:** A field-representation technique, not a runtime marcher change; no float-order analysis in the corpus.
- **Puck verdict:** surveyed, not deep-reviewed — replaces the field representation itself, so it is more relevant to a future baked-field tier than to Puck's expression-tree compiler; Puck currently rejects baked volumes in favor of pure-analytic program interpretation.
- **See also:** [README.md](README.md), [lod-and-bounds.md](lod-and-bounds.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Generalized Lipschitz Tracing of Implicit Surfaces

- **Source:** Bán, Valasek, *Generalized Lipschitz Tracing of Implicit Surfaces*, Computer Graphics Forum 44, 2025. https://onlinelibrary.wiley.com/doi/10.1111/cgf.15202.
- **Digest:** Extends segment/Lipschitz tracing to general implicit functions by precomputing local polynomial proxies of the field in a preprocessing pass, then deriving directional-derivative bounds from those proxies at trace time — removing the requirement that every op have a hand-derived exact norm.
- **Determinism / cross-backend fit:** Not analyzed in depth by the corpus; the preprocessing pass would need to be verified bit-identical across backends before adoption.
- **Puck verdict:** surveyed, not deep-reviewed — trades the hand-classified-op requirement for a numerical preprocessing pass; interesting as a fallback for ops without a hand-derived closed-form norm, but adds a bake-time dependency the current closed-form `AnalyzeLipschitz` design avoids. Alongside Segment Tracing (Galin et al. 2020, on [marching-acceleration.md](marching-acceleration.md)), this is the closest existing literature gets to a general anisotropic/directional step correction — see the closing note below.
- **See also:** [README.md](README.md), [marching-acceleration.md](marching-acceleration.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Domain Repetition (the wrong-neighbor bug)

- **Source:** Inigo Quilez, *Domain Repetition*, 2023. https://iquilezles.org/articles/sdfrepetition/.
- **Digest:** Names and dates the "wrong neighbor" bug of `round()`-based domain repetition: whenever instance size varies, the true nearest instance can live in a neighboring cell rather than the cell the query point rounds into, producing a discontinuity in the field. The fix is explicit neighbor-cell checking (evaluate the candidate cell plus its relevant neighbors), or clamped/limited repetition for finite instance counts — at a cost of roughly 4x (2D) or 8x (3D) field evaluations per query.
- **Determinism / cross-backend fit:** Pure integer/float arithmetic (cell index + modular position); deterministic and backend-safe by construction once the neighbor set is fixed.
- **Puck verdict:** surveyed, not deep-reviewed — a named, dated primary source for the exact discontinuity Puck's current round-based repeat op is exposed to; directly actionable if a fix is scheduled.
- **Empirical status (Puck): CONFIRMED REAL, bounded to the authoring constraint** (2026-07-08 in-demo correctness hunt). Slice captures show Repeat with an off-center/oversized prototype creasing at cell walls — an *over*estimate, the hole-inducing class neither `stepScale` nor the omega step-back can fix — while a prototype centered within half the spacing stays exact; CellJitter seams at cell boundaries even with the in-cell containment rule satisfied (containment ≠ nearest-copy). The 3^k neighbor-check this entry describes is NOT warranted at current idle usage: the strengthened authoring rule suffices, plus builder-guard growth (Repeat has no extent guard today; CellJitter's guard omits the prototype radius). Status: actionable-if-authored, not urgent. Full record: [verdict-index.md](verdict-index.md#empirical-status-in-puck).
- **See also:** [README.md](README.md), [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### KIFS / Mandelbox scalar-DE fudge factors

- **Source:** Mikael Hvidtfeldt Christensen, *Distance Estimated 3D Fractals (VI): The Mandelbox*, Syntopia blog, 2011 (documenting the Tom Lowe / Rrrola / Tglad et al. community derivation). https://blog.hvidtfeldts.net/; fractalforums.com DE threads.
- **Digest:** For KIFS/Mandelbox-style folds, tracking a single scalar running product of the scaling-Jacobian magnitudes gives a usable distance estimate at a fraction of the cost of a full Jacobian — but the result is not a true metric, since the scalar product misses discontinuities the real Jacobian norm has, and practitioners compensate with an empirical fudge factor.
- **Determinism / cross-backend fit:** A running float product accumulated per-fold-iteration — deterministic within a backend under fixed float-op order, same class of order-dependence Puck's other accumulating loops already carry.
- **Puck verdict:** surveyed, not deep-reviewed — the closest existing treatment of the running-Lipschitz-product situation Puck has for nested Scale/fold operations, but it is an informal community source, not a rigorous derivation. Honest state of the art: no fully rigorous fix exists in the literature, only conservative fudge factors.
- **See also:** [README.md](README.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### iq "Smooth Minimum" taxonomy

- **Source:** Inigo Quilez, *Smooth Minimum* (2024 rewrite). https://iquilezles.org/articles/smin/.
- **Digest:** Organizes smooth-min kernels into two families with different correctness properties: **DD** (Direct Difference — exponential/root/sigmoid) kernels, whose blend influence extends unboundedly and never reaches a bit-exact endpoint; and **CD** (Clamped Differences — quadratic/cubic/quartic/circular) kernels, constrained so the blend has *zero* effect beyond `k` units apart, giving compact support and an exact endpoint. Separately catalogs polynomial/exponential/root/circular smin families with their halo/band-width formulas.
- **Determinism / cross-backend fit:** CD-family kernels preserve far-exact blend endpoints (bit-identical at `h=0`/`h=1`), which is what keeps instance mask-cull bit-exact; DD-family kernels do not, since their non-compact support means there is no region where both backends are guaranteed to agree on "no blend."
- **Puck verdict:** deep-reviewed in part — **DD-family smins: reject** (per `review-05-materials-shadows`): non-compact support breaks Puck's far-exact mask-cull invariant, a hard disqualifier, not a tuning tradeoff. **CD-family smins: the correctness-safe family Puck already deploys** (confirm the specific kernel in use is CD, not DD). The general polynomial/exponential/root/circular band-width taxonomy beyond that correctness split is surveyed, not deep-reviewed — a candidate to cross-check against Puck's existing `1.70711k` chamfer-halo constant, though a literature-derived closed form for that specific constant was not found in the surveyed material; treat any such cross-check as a sanity comparison, not a verified derivation.
- **See also:** [README.md](README.md), [materials-and-primitives.md](materials-and-primitives.md), [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Bound-preserving fBm detail

- **Source:** Inigo Quilez, *fBM Detail in SDFs*. https://iquilezles.org/articles/fbmsdf/. Moinet, Neyret, *Fast Sphere Tracing of Procedural Volumetric Noise…*, Computer Graphics Forum 2025. https://inria.hal.science/hal-05046040v1, https://doi.org/10.1111/cgf.70072.
- **Digest:** Arithmetic addition of fBm noise to an SDF is *not* a valid SDF — it violates the `|grad f| = 1` bound and creates disconnected "flyover" surfaces the marcher can overshoot. iq's fix builds detail one octave at a time: each octave's noise layer `n` is `smax`-clipped into a thin shell (`~0.1*s` half-width) around the running host field `d`, then `smin`-blended in (`~0.3*s` smoothness), so every partial cascade stays a valid conservative bound as amplitude `s` halves and frequency doubles per octave. Moinet & Neyret's complementary idea: every partial sum of the octave cascade is itself a conservative bound on the full field, so octave count can be driven as a footprint/distance-based level of detail — cheap low-octave bounds far away, full detail only where the ray footprint can resolve it.
- **Determinism / cross-backend fit:** Excellent. The whole construction is a pure function of position built from `smin`/`smax` (already parity-clean in Puck's ISA) over the deterministic PCG3D integer-hash noise basis — integer hashes are exact and bit-identical across backends by construction, with no per-ray history or accumulated float state at all (unlike the step-relaxation family). The only float-order concern is the octave loop's `smin`/`smax` accumulation, the same order-dependence class the ISA already gates; pin fp-contraction in the noise op as a precaution.
- **Puck verdict:** **BUILD (L)** — per `review-06-relaxation-fbm`, the correct realization of the roadmap's fBm item, not an optimization of an existing feature (Puck has no fBm/gradient-noise displacement today). Needs a dedicated **FBM ISA op** (not a compile-time macro expansion — footprint-driven octave-LOD is a runtime-variable loop count, and Puck programs are fixed-length) carrying `(base_amplitude, lacunarity, gain, max_octaves, basis, clip=0.1, smooth=0.3)`, running iq's smax-clip/smin-blend cascade internally over a **PCG3D integer-hash noise basis**. Needs a **closed-form `AnalyzeLipschitz` norm** for the op: because `smin`/`smax` of two K-Lipschitz fields is K-Lipschitz, combining octaves bounds `L` by roughly the *max* (finest active) octave contribution rather than the sum a naive additive fBm would need — `L_fbm ≈ C * base_amplitude * (gain*lacunarity)^(K-1) * (1 + smooth_inflation)`. The LOD↔Lipschitz↔epsilon coupling reuses the existing per-candidate **`distanceScale` channel** with zero new machinery: as footprint drops the active octave count `K`, `L` drops, and the FBM op writes its runtime `L`-correction into the same channel already carrying Scale/log-spherical metric corrections, so the marcher automatically takes bigger steps in the coarse far field. Sequencing: PCG3D value-noise op (M) before the FBM op + `AnalyzeLipschitz` term + octave-LOD (L, together); a gradient-noise basis and forward-mode gradient accumulator (L–XL) are a shared prerequisite with analytic normals and Moinet curvature stepping, deferred until one of those justifies the cost.
- **See also:** [README.md](README.md), [gradients-and-normals.md](gradients-and-normals.md), [marching-acceleration.md](marching-acceleration.md), [lod-and-bounds.md](lod-and-bounds.md), [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

---

**Gap noted: anisotropic / non-Euclidean metric step correction.** No dedicated
literature was found in this sweep deriving a step correction for sphere
tracing under an anisotropic or non-Euclidean metric — every technique above
still steps in ordinary Euclidean world space. Segment Tracing (Galin, Guérin,
Peytavie et al., CGF 2020, on [marching-acceleration.md](marching-acceleration.md))
and Generalized Lipschitz Tracing (Bán & Valasek 2025, above) are the closest
partial answers: both generalize the Lipschitz bound to depend on ray
*direction*, but neither touches the underlying distance metric itself. This
remains an open research area, not a gap in the survey.
