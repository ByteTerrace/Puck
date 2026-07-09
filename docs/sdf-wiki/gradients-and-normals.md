# Gradients and Normals

Puck's `renderView` currently gets its shading normal from a lazy 4-tap
tetrahedron finite-difference (FD) at the hit — four narrow re-walks of the
`uint[]` program, each perturbing one coordinate and subtracting. The
combined verdict of this page is to replace that with a forward-mode dual
(value + `float3` tangent) walked through the same op switch the distance
march already uses: each op's `case` gains a flagged block that updates the
tangent alongside the scalar, reusing whatever subexpressions the distance
computation already produced, composed at runtime by the interpreter's own
loop rather than by a build-time codegen pass.

**LANDED — 2026-07-09** (commit `ce36f80`), as `mapGradCore`, a hit-only dual
specialization of the map switch. Analytic normals are now the default
(the FD tetrahedron tap described above is the pre-landing baseline this page
argued against); the cross-backend gate is the new `world-analytic-normal`
Post stage. Hero-scene parity improved 51→11 px and `gpu-budget` moved
1.87→1.58 ms.

### Forward-mode dual gradient accumulator (design synthesis)

- **Source:** Puck internal design review — `review-07-gradients.md`,
  synthesizing Inigo Quilez, "SDFs and Gradients (3D)"
  (https://iquilezles.org/articles/distgradfunctions3d/); Zach Corse et al.,
  "Vertex Shader Domain Warping with Automatic Differentiation," arXiv:2405.07124,
  2024 (https://arxiv.org/abs/2405.07124 ,
  https://ar5iv.labs.arxiv.org/html/2405.07124); Inigo Quilez, "Analytic
  Derivatives of Gradient Noise" (https://iquilezles.org/articles/gradientnoise/);
  and Inigo Quilez, "Smooth Minimum" (https://iquilezles.org/articles/smin/).
- **Digest:** The four sources combine into one shape: carry a `float3`
  tangent alongside the scalar distance through every op, updated by
  op-specific micro-code that is cheap for isometries/CSG-select (identity,
  orthonormal multiply, branch-select), moderate for warps with a near-diagonal
  or similarity Jacobian (DomainWarp, LogSphere). The already-derived Lipschitz
  operator norms (Bend `1+a`, Twist `√((2+a²+a√(a²+4))/2)`, Displace
  `1 + amp·max|freq|`) bound the march **step**, not the gradient — gradient
  propagation instead chain-rules through each op's runtime point-Jacobian
  (folds apply their own actual reflection/rotation, DomainWarp its matrix
  transpose, Displace its exact analytic derivative), hand-written per op. At
  the hit this becomes a **single wide walk** that fetches and decodes
  each program word once and updates all four accumulator components
  (distance + 3 tangent components), versus the FD scheme's four separate
  narrow walks that each re-fetch and re-decode the whole program — the
  fetch-bound interpreter's real cost is memory traffic, not ALU, so one
  shared fetch stream beats four.
- **Determinism / cross-backend fit:** This is the strongest argument in the
  corpus, and it is Puck-specific — none of the four source papers raise it.
  FD central differences compute `f(p+h·e) − f(p−h·e)`: a subtraction of two
  nearly-equal values whose low bits carry the answer, and DXC's SPIR-V and
  DXIL backends are free to contract and reassociate `fma` differently, so the
  two evaluations round differently and the *cancelled difference* diverges
  more than the primal distance ever does — precisely the "±1-LSB clusters
  along gradients" signature the fuzz harness already observes, and the reason
  lit scenes currently lean on the relaxed parity threshold. A dual walk has
  no such subtraction: the tangent is *built up* by the same multiplies, adds,
  orthonormal transforms, and branch-selects the distance channel already
  makes (min/max, folds, a smooth blend's `h`), using the same fma/contraction
  discipline the distance channel already passes parity with, and the noise
  term reuses the integer PCG3D hash — already bit-identical across backends,
  with the entire smooth part of the derivative living in the quintic `du`
  term (the hash itself is piecewise-constant per lattice cell, so its own
  derivative is zero). A dual walk is therefore **as parity-stable as the
  distance channel**, and more stable than FD because it never cancels;
  expect this to reduce reliance on `PUCK_PARITY_STRICT` for lit scenes, not
  merely hold it.
- **Puck verdict:** adopt-now — make this *the* design of the roadmap's
  reserved gradient accumulator for analytic normals, effort **L**. Per
  source: iq's distgradfunctions3d supplies the core mechanism (distance and
  unit-gradient sharing subexpressions per primitive, and the transform rule —
  translate leaves the gradient untouched, rotate multiplies it by the
  orthonormal matrix, non-uniform scale is forbidden because it would need an
  inverse-transpose the exact-distance property can't survive); the Corse et
  al. AD paper supplies the *math* (forward-mode, Jacobian-as-columns, normal
  from tangents via `normalize((u+Ju)×(v+Jv))`) but not the *mechanism* — its
  build-time DSL codegen has no analogue in an interpreter walking shared
  `uint[]` data, so Puck hand-writes each op's derivative micro-code once in
  the HLSL op switch and lets the VM loop compose the chain at runtime instead
  of a compile-time codegen pass; iq's gradient-noise article supplies the
  Displace-op derivation, re-based onto PCG3D as noted above (the
  highest-value op to dualize, since FD-on-noise is the worst FD case); iq's
  smooth-minimum article supplies the blend gradient, `mix(∇b, ∇a, h)` with
  the same `h` already computed for the blended distance, no extra term.
  Guardrails carried forward from the design review: keep the dual path a
  **hit-only second specialization** of `mapCore` (`MAP_DISTANCE_GRAD`,
  separate entry point from today's `MAP_DISTANCE`) so the march kernel's lean
  register footprint is unaffected by the 4×-wide accumulator — sharing one
  kernel would force the compiler to allocate for the worst case and drop
  march occupancy for a normal computed once per pixel; keep the gradient
  channel **strictly parallel** to the `PushField`/`PopField` distance
  accumulator (separate storage, matching push/pop lifetime, never tangled
  into the same stack); the existing Lipschitz operator norms stay in their
  step-bound role only — gradient propagation chain-rules through each op's
  runtime point-Jacobian instead (folds' actual reflection/rotation,
  DomainWarp's matrix transpose, Displace's exact analytic derivative), each
  hand-derived per op rather than reused from the norms. Watch smooth-blend
  liveness (both branches'
  `float3` gradients alive at once at a blend) as the real register cost, not
  FLOPs. The CPU reference marcher needs the same dual path (the C#↔HLSL
  sync-pair discipline) so a new Post stage can gate distance+gradient
  bit-parity across backends.
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md),
  [march-loop-scheduling.md](march-loop-scheduling.md),
  [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

## See also

- [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md) —
  the Bend/Twist/Displace operator-norm derivations this page's Jacobian norms
  are reused from.
- [materials-and-primitives.md](materials-and-primitives.md) — the smin
  taxonomy and primitive catalog the gradient micro-code rides alongside.
- [verdict-index.md](verdict-index.md) — this page's verdict in the
  all-techniques table.
- [../sdf-sota-survey.md](../sdf-sota-survey.md) — the ranked decision
  shortlist this wiki is a reference companion to.
