# Marching Acceleration

Techniques that change **how a single ray steps through the field** — the
`t += step * d` line of the Keinert marcher and everything that decides what
`step` should be. Scope is step schedules, over-relaxation, extrapolation, and
per-step derivative use; hierarchical prepasses, ray pyramids-as-dispatch-
structure, and instance culling live on
[hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md)
instead. Puck's baseline is Keinert Enhanced Sphere Tracing with a fixed
`omega=1.2`; every other entry here is measured against that baseline.

### Keinert Enhanced Sphere Tracing (baseline)
- **Source:** Keinert, Schäfer, Korndörfer, Ganse, Stamminger, *Enhanced Sphere
  Tracing*, Eurographics Short Papers 2014. https://diglib.eg.org (Eurographics
  Digital Library; the specific handle is not resolved in the corpus — see
  citations in Bálint & Valasek 2018 and Bán & Valasek 2023 below, both of
  which build on it directly).
- **Digest:** Over-relaxed sphere tracing (`z = omega * R`, `omega in [1,2)`)
  with a disjoint-sphere overshoot check: if the new unbounding sphere is
  disjoint from the previous one (`z > r + |R|`), the over-relaxed step was
  unsafe and the marcher reverts to a plain step `z = r` and retries. Also
  introduces a screen-space metric for picking a better final intersection
  candidate than the naive last-step point.
- **Determinism / cross-backend fit:** This is Puck's shipping baseline
  marcher today — `omega=1.2` fixed, disjoint-sphere step-back on overshoot,
  footprint-adaptive epsilon — already parity-gated across SPIR-V and DXIL.
  Every relaxation variant reviewed below is judged by how much carried state
  and float-order risk it adds *on top of* this loop.
- **Puck verdict:** adopt-now — already implemented; this is the floor every
  other entry on this page is compared against, not a pending decision.
- **See also:** [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [verdict-index.md](verdict-index.md).

### Bálint & Valasek 2018 — "Accelerating Sphere Tracing" (enhanced / planar-optimal extrapolation)
- **Source:** Csaba Bálint, Gábor Valasek, *Accelerating Sphere Tracing*,
  Eurographics 2018 (short paper). DOI 10.2312/egs.20181037.
  https://diglib.eg.org/handle/10.2312/egs20181037 ; PDF:
  https://people.inf.elte.hu/csabix/publications/articles/eurographics-2018-shortpaper.pdf
- **Digest:** Instead of a constant multiplier, infers the next radius from a
  linear (planar-optimal) extrapolation of the last two SDF samples — steps
  exactly to where the new unbounding sphere is tangent to the current one
  (Algorithm 3), falling back to a basic step `z = r` when the spheres go
  disjoint. Also proposes a cone-tracing SDFE (`F(t) = (f(p+tv) - alpha*t)/(1+alpha)`)
  that folds pixel footprint into the distance estimate — the same mechanism
  as Puck's existing footprint-adaptive epsilon. Reported up to ~50% better
  error-time than relaxed tracing on smooth scenes, but only on par with
  relaxed on the Mandelbulb fractal; carries two live per-ray values (the
  previous sample pair).
- **Determinism / cross-backend fit:** Deterministic — no RNG/history in the
  nondeterministic sense — but the step formula divides by a quantity that
  goes to zero near tangency, so FMA-contraction divergence between DXC→DXIL
  and DXC→SPIR-V could flip the disjoint-sphere fallback compare on boundary
  pixels; would need `precise`/fp-contraction pinning like auto-relaxed below.
- **Puck verdict:** optional/skip, effort S. Per the deep review: "Strictly
  dominated by auto-relaxed in the same paper's own follow-up (more state, more
  fallbacks, needs `omega` tuning) — no reason to prefer it."
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md) (the cone-tracing SDFE = the existing footprint-adaptive epsilon mechanism), [verdict-index.md](verdict-index.md).

### Bán & Valasek 2023 — "Automatic Step Size Relaxation in Sphere Tracing" (auto-relaxed)
- **Source:** Róbert Bán, Gábor Valasek, *Automatic Step Size Relaxation in
  Sphere Tracing*, Eurographics 2023 (short paper). DOI 10.2312/egs.20231014.
  https://diglib.eg.org/xmlui/handle/10.2312/egs20231014 ; code:
  https://github.com/Bundas102/auto-relaxed-trace
- **Digest:** Tracks the SDF's along-ray slope with an exponential moving
  average (`m := (1-beta)*m + beta*M`, `beta=0.3`) and derives the next step
  from it (`z = 2r/(1-m)`), resetting to a basic step on the same
  disjoint-sphere fallback Keinert uses. On an AMD RX 5700 at a 1000-iteration
  cap it runs at 81–94% of "enhanced" cost and beats relaxed `omega=1.2` by
  roughly 8–15% on tested scenes; at a 32-iteration cap it is on par with
  enhanced. Two headline properties: far fewer robustness fallbacks than
  either enhanced or relaxed, and markedly lower sensitivity to its
  hyperparameter (`beta in {0.2,0.3}` stays within 2% across scenes, where
  relaxed's `omega` needs per-scene retuning).
- **Determinism / cross-backend fit:** Adds exactly one scalar of carried
  state (`m`) — no new divergence class beyond the marcher's existing
  order-dependent float accumulation. The real risk is the same
  FMA-contraction hazard as Bálint 2018 (division near `m -> 1` can flip the
  fallback compare on a handful of boundary pixels between backends).
  Adoptable under parity *iff* fp-contraction is pinned on the step and the
  fallback comparison and `(1-m)` is clamped away from zero; keep plain
  `omega=1.2` as the `PUCK_PARITY_STRICT` fallback path so the strict gate
  never rides on the divided step. Judged the better parity citizen of the
  family: one scalar of state vs. enhanced's two-sample requirement, and far
  fewer fallback-branch divergence opportunities.
- **Puck verdict:** adopt-now, gated on fp-contraction pinning + denominator
  clamp, effort S–M. Per the deep review: "the pick of the family. Best
  robustness (fewest fallbacks), smallest carried state (one scalar), no
  per-scene float knob (fits the engine ethos), single-digit-to-teens % gain
  over our omega=1.2 on smooth content."
- **Landed (2026-07-08, commit `b6747e6`).** The per-ray slope EMA `m` drives
  `omega = max(1, 2/(1-m))`; the divided step, the slope quotient, and the
  disjoint-sphere fallback operand are all marked `precise` so DXC's SPIR-V/
  DXIL FMA contraction cannot diverge and flip the fallback branch near
  tangency, and `SlopeCap` clamps `m` at 0.8 so `(1-m)` never drops below 0.2.
  The plain `omega=1.2` path is kept reachable as a compile-time
  `#define SDF_STRICT_MARCH` fallback (default off) rather than a runtime
  toggle, since the kernels are AOT-compiled by DXC in-place with no runtime
  shader-variant selection.
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [verdict-index.md](verdict-index.md).

### DIST aggressive 1.5x marching + safe convergence band
- **Source:** Shaohui Liu, Yinda Zhang, Songyou Peng, Boxin Shi, Marc
  Pollefeys, Zhaopeng Cui, *DIST: Rendering Deep Implicit Signed Distance
  Function with Differentiable Sphere Tracing*, CVPR 2020.
  https://arxiv.org/abs/1911.13225 ;
  https://openaccess.thecvf.com/content_CVPR_2020/html/Liu_DIST_Rendering_Deep_Implicit_Signed_Distance_Function_With_Differentiable_Sphere_CVPR_2020_paper.html
- **Digest:** DIST's forward renderer steps by **1.5x the queried SDF value**
  in clear space instead of the usual 1.0x, overshooting through empty space,
  paired with a safe convergence criterion that reverts/refines rather than
  tunneling through thin surfaces near the zero band. This rides alongside
  DIST's coarse-to-fine resolution pyramid (each finer ray inherits its
  coarse parent's marched depth) — the pyramid/dispatch-structure question is
  covered on the hierarchical-acceleration page; this entry is the marcher
  constant only.
- **Determinism / cross-backend fit:** Good. The 1.5x constant lives in the
  shared `sdf-vm.hlsli`, so both backends step identically; the safe-
  convergence guard is a branch on `|d| < band`, a pure per-lane function of
  position with no wave-vote coupling lanes. DIST's motivation (amortizing
  expensive neural-network queries) doesn't transfer to Puck's cheap analytic
  `map()`, but the aggressive-step constant and guard band transfer directly
  as a near-free empty-space win, independent of pyramid depth.
- **Puck verdict:** adopt-now, effort S. Per the deep review: "take the 1.5×
  aggressive march + convergence band now (cheap, orthogonal); defer the
  third pyramid level to measurement" — it is "one constant + a guard band,
  big empty-space win, shared kernel," and compounds with the four-bound
  teleport (a hierarchical-acceleration technique): "teleport past the
  *known* empty gaps, 1.5×-march the *unknown* ones."
- **See also:** [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md) (the coarse-to-fine ray pyramid and third beam level), [march-loop-scheduling.md](march-loop-scheduling.md), [verdict-index.md](verdict-index.md).

### Moinet & Neyret 2025 curvature stepping
- **Source:** Guillaume Moinet, Fabrice Neyret, *Fast Sphere Tracing of
  Procedural Volumetric Noise for Very Large and Detailed Scenes*, Computer
  Graphics Forum 44, e70072, 2025. DOI 10.1111/cgf.70072.
  https://inria.hal.science/hal-05046040v1 ; https://doi.org/10.1111/cgf.70072
- **Digest:** Uses the field's first *and* second derivatives along the ray to
  build a local quadratic model and take a larger provably-safe step than a
  first-order Lipschitz-only bound allows. Reported headline: ~110ms → ~16ms
  (~7x) on very large, detailed procedural volumetric noise scenes (clouds,
  cosmic dust, fire, terrain) on an RTX 4080Ti, combined with the paper's
  companion fBm-as-nested-bounds LOD idea (a field-construction technique
  documented on the Lipschitz/correctness page, not here). The paper was
  behind an anti-bot wall on Inria HAL and paywalled elsewhere, so this
  digest is reconstructed from the abstract and corroborating snippets —
  flagged **lower-confidence** on the exact algorithm.
- **Determinism / cross-backend fit:** Deterministic in principle (analytic
  derivative computation, bit-stable if operator order is fixed), but the
  marcher-side half needs a forward-mode gradient accumulator and a
  second-derivative source Puck doesn't have yet.
- **Puck verdict:** defer, effort L. Per the deep review: "blocked on the
  gradient accumulator, plus a second-derivative source... low priority for
  CSG solids; revisit only if/when volumetric-noise content lands." The
  benefit is largest for procedural volumetric noise, which is not today's
  content (CSG/SDF solids and world geometry); for opaque solids the
  marginal gain over auto-relaxed is unproven.
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md) (the fBm-as-nested-bounds LOD half and the bound-preserving fBm construction it pairs with), [gradients-and-normals.md](gradients-and-normals.md) (the shared forward-mode gradient accumulator), [verdict-index.md](verdict-index.md).

### Non-linear Sphere Tracing ODE
- **Source:** Dario Seyb, Alec Jacobson, Derek Nowrouzezahrai, Wojciech
  Jarosz, *Non-linear Sphere Tracing for Rendering Deformed Signed Distance
  Fields*, ACM TOG 38(6), 2019 (SIGGRAPH Asia).
  https://cs.dartmouth.edu/~wjarosz/publications/seyb19nonlinear.html ; open
  PDF: https://par.nsf.gov/servlets/purl/10172295
- **Digest:** Reformulates sphere tracing in object space as an ODE
  initial-value problem so that, under a non-linear forward deformation (e.g.
  skinning), the ray becomes a curved path that is numerically integrated —
  avoiding the need for an inverse deformation map. An alternative to
  conservative Jacobian-bound marching through warped fields: exact rather
  than conservative, relevant to Puck's existing domain-warp/twist/bend
  Jacobian-bound handling.
- **Determinism / cross-backend fit:** With caveats, per the sweep — ODE
  numerical integration is deterministic given a fixed step count/order, but
  materially more expensive per-step than a closed-form Jacobian bound; would
  need fixed-iteration-count integration to stay bit-stable cross-backend.
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [verdict-index.md](verdict-index.md).

### Quadric Tracing
- **Source:** Mátyás Kiglics, Csaba Bálint, *Quadric Tracing: A Geometric
  Method for Accelerated Sphere Tracing of Implicit Surfaces*, Acta
  Cybernetica 25, 2021. https://cyber.bibl.u-szeged.hu/index.php/actcybern/article/view/4203
- **Digest:** Precomputes a grid of bounding/unbounding quadric surfaces per
  grid cell, each fully containing or fully excluding the scene geometry;
  intersecting the ray with the quadric first cheaply skips or shortcuts the
  expensive SDF evaluation. Two modes: augment-only (exact SDF preserved) or
  replace (fully interpolated surface). Reports 20–100% speedup on static
  scenes.
- **Determinism / cross-backend fit:** Yes, per the sweep — a precomputed
  static grid plus closed-form quadric ray intersection, trading a
  GPU-resident precomputed grid buffer for the speedup. Flagged elsewhere in
  the corpus as a spatial acceleration structure orthogonal to Lipschitz
  correctness, "only relevant if a baked spatial index is added."
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [lod-and-bounds.md](lod-and-bounds.md), [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [verdict-index.md](verdict-index.md).

### InverseVis curved sphere tracing
- **Source:** Kai Lawonn, Monique Meuschke, Tobias Günther, *InverseVis:
  Revealing the Hidden with Curved Sphere Tracing*, Computer Graphics Forum
  43(3), 2024 (EuroVis). DOI 10.1111/cgf.15080.
  https://arxiv.org/abs/2404.09092
- **Digest:** Bends camera rays via an optimized curvature field during
  sphere tracing to reveal back-facing/occluded regions — a visualization
  technique, not a speed optimization. Included as a related-work pointer:
  the underlying "curved ray" sphere-tracing formulation generalizes the
  step-schedule family above, even though its goal is visibility, not
  marching cost.
- **Determinism / cross-backend fit:** Yes, with caveats, per the sweep —
  lowest priority; a related-work pointer only, not evaluated against Puck's
  march loop.
- **Puck verdict:** surveyed, not deep-reviewed.
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [verdict-index.md](verdict-index.md).
