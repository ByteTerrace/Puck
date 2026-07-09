# SDF VM evolution — plan of record

The signed-distance-field renderer's research-driven evolution: a sequence of
engine-contract changes (all `Puck.Post`-gated) that made the marcher robust,
faster, and more expressive. This is the **living roadmap** — what has landed (as
settled context; the *contracts* live in the `sdf-world` skill) and, for a future
agent, **what remains unrealized**.

Sourced from three articles (2026-07): a recursive/cone-marching renderer
([pointersgonewild](https://pointersgonewild.com/2026-03-06-a-recursive-algorithm-to-render-signed-distance-fields/)),
"the wrong way to use an SDF" (the Lipschitz-1 requirement,
[winterbloed](https://winterbloed.be/the-wrong-way-to-use-a-signed-distance-function/)),
and [log-spherical mapping](https://www.osar.fr/notes/logspherical/).

## The through-line

The marcher's speed **and** correctness both rest on the field being a true
Lipschitz-1 distance bound — `sdf(p)` never overstates the distance to the
nearest surface. Every change here either exploits that bound harder, preserves
it, or bends-and-corrects it. That single property is the substrate everything
below plugs into.

## Landed (branch `features/alpha-prep`, battery 48/48)

- **D1 — a Lipschitz-safe marcher.**
  - *Keystone (A):* `SdfProgram.AnalyzeLipschitz` bakes a per-program
    `stepScale = 1/L` into the segment-directory header; `mapCore` multiplies its
    final distance by it once, so sphere tracing cannot overstep a non-1-Lipschitz
    warp or an eccentric ellipsoid. The warp norms are **not one formula**: a
    `Bend` keys on a coordinate INSIDE the plane it rotates, so its exact operator
    norm is `1 + a` (attained); only `TwistY` keys outside and collapses to
    `√((2 + a² + a√(a²+4))/2)`. Using the twist form for a bend under-clamps by up
    to 24% and holes the march (`BendOperatorNorm` vs `TwistOperatorNorm`). Isometric programs bake exactly `1.0f` (byte-identical).
    A **distinct** per-sample `distanceScale` channel carries true domain-metric
    corrections (Scale, and D2's log-spherical) — never merge the two.
  - *Cull fix (E):* `blendSmoothUnion` reformulated `lerp(a, b, 1-h)` (bit-exact
    at far saturation, and — since `b6d51e3` — bit-exact at the NEAR endpoint too,
    which is what a scoped accumulator would depend on) + `PackInstances` inflates
    a smooth instance's cull bound by its blend radius `k` (a `ChamferUnion` needs
    `1.70711·k`; its bevel plane sags past one radius) → soft-blended instances cull **bit-exactly**,
    closing the long-standing "SmoothUnion-vs-world defeats its own cull" gotcha.
  - *Marcher (C+B):* Keinert Enhanced Sphere Tracing — over-relaxation (`ω·d`,
    ω=1.2, with a disjoint-sphere step-back, safe **because** the field is
    clamped) fused with a footprint-adaptive hit threshold (resolution-independent
    silhouettes). ~2× fewer march steps on loaded scenes; cross-backend parity
    improved.
- **D2 — log-spherical "Droste" domain warp** (`SdfOp.LogSphere`): folds the
  radial log-coordinate to the nearest shell, so a translation along `log(radius)`
  becomes a Cartesian scaling → infinite self-similar shells from one prototype,
  plus an optional per-shell Z-spin. **Radial-only fold ⇒ no polar pinching**; the
  `r/density` correction rides `distanceScale`; an `exp(w/2)` Lipschitz factor
  keeps the over-relaxed march hole-free across shell boundaries.
- **Whimsy:** 4-tap tetrahedron normals (6→4 evals of the hottest call, same
  gradient); the **Vesica** lens primitive (exact 2D-vesica-of-revolution, id 7);
  **SDF soft shadows** (penumbra march in `renderView`, tile-masked, reach 12).

The C#↔HLSL sync pairs, packed word layout, and exact-cull semantics for all of
this are in the **`sdf-world` skill** — load it before touching the code.

The VM's **flat accumulator** — and the open `PushField`/`PopField` decision that
would scope it — is a separate live thread:
[sdf-accumulator-plan.md](sdf-accumulator-plan.md).

## Unrealized / next (for a future agent)

None of the below is started. Roughly ordered by when it will matter.

### D3 — hierarchical cone marching + instance BVH (the GPU-bound lever)
The Stage-0 beam is **single-level** (fixed 16×16 tiles) and does an
**O(instances)** flat sphere-vs-cone loop per tile. The hero scene is ~1 ms today
(CPU-simulation-bound, not GPU-bound), so this is deferred until complex scenes
make it bite.

> **2026-07-08: the trigger is now MEASURED, not deferred-on-vibes.** The
> `sdf.bench sweep` instrument (inside the `sdf` fullscreen debug mode) measured
> `sdf-beam` growing essentially **linearly** in instance count — O(instances),
> exponent ≈0.97 across 64→256→1024→4096 — and overtaking `views` (the
> per-pixel march) at **n ≈ 256 live instances**, owning **77%** of a
> 4096-instance frame (~4 fps). That crossover *is* "complex scenes make it
> bite." The promoted follow-up is narrower than an instance BVH, though: the
> SOTA survey's adversarial review ([sdf-sota-survey.md](sdf-sota-survey.md),
> row 15) settled on a deterministic **uniform-grid / spatial-hash instance
> cull** (a CSR scatter build, no atomic append) over a BVH — the same
> O(instances) → O(instances-in-nearby-cells) win without a non-deterministic
> build step. It is the gate before raising `MaxInstances` past 4096. Full
> numbers: [sdf-bench-notes.md](sdf-bench-notes.md). Not yet started; segment
> tracing (below) stays deferred behind it as originally scoped.

Two moves:
- A coarser **pre-beam** (e.g. 64×64 → 16×16 — the recursive/cone-marching idea
  from the pointersgonewild article) to hand Stage 1 a tighter `marchStart` and
  cull instances against big cones once.
- An **instance BVH** so the per-tile cull isn't O(n). This ties directly into
  [machine-fleet-briefing.md](machine-fleet-briefing.md) (the fleet-scale arc).

D3 is also the natural home for the three items below.

### Segment tracing (Galin et al. 2020 — the deferred "D")
Per-segment **directional** local Lipschitz bounds → larger provably-safe steps,
no fallback, far fewer field queries. Our **segment directory** is exactly the
structure this technique wants, and A's per-program `L` stays valid as the
whole-field/beam bound — so segment tracing is **additive**, not a retrofit (bake
a per-segment directional bound alongside the per-shape bounding spheres).

### Per-tile / per-segment Lipschitz refinement
The keystone's `stepScale` is **per-program** and coarse: one steep warp (or one
noisy shape) slows the *whole frame's* march. Designed-in fast-follow: fold each
visible warped instance's baked `L` into a **per-tile `max-L`** buffer in the beam
(which already loops the tile's visible instances) so only warp-bearing tiles
slow. Per-segment directional bounds (above) eventually supersede it.

### A compute shadow-cull
The soft-shadow march has no acceleration structure, so on dense lit scenes it
costs ~2.6× the per-pixel march (bounded by reach-12). The RT path already solves
this: `sdf-world-rt-debug.rq.comp.hlsl`'s `lightShadow` uses the TLAS to
fast-forward the shadow ray to the occluder bound and skip the empty space. Port
that occluder-cull to the compute shadow march — it naturally rides the D3 BVH.

### ~~Noise / displacement primitives~~ — LANDED
`SdfOp.Displace` (a FIELD op) and `SdfOp.DomainWarp` (a POINT op) both ship, as
does `SdfOp.CellJitter` (a stochastic domain-repeat fold with White/Blue/Gaussian
flavors). The section's own predictions held: they are encoded as ops, not
primitives; the hash is **integer-only** (canonical PCG3D, plus an R3 fixed-point
lattice for the Blue flavor) so cell decisions are bit-identical across DXC's
SPIR-V and DXIL; and `AnalyzeLipschitz` clamps them. One correction — the factor
is `1 + amplitude·max|frequency_i|`, the **infinity** norm, not `‖f‖₂`: Displace's
squared gradient norm is multilinear in the three squared sines, so it maximizes
at a cube vertex, and DomainWarp's `J - I` is a generalized permutation matrix
whose spectral norm is its largest entry. Gated by `world-displace`,
`world-domain-warp`, `world-cell-jitter*` and their solidity stages.

Also landed since this doc was written: the **2D-primitive family** (rounded
rectangle, regular polygon, star, trapezoid, ellipse — revolved or extruded, each
earning a real cull bound), the **chamfer blends**, `RepeatPolar`, and
`SymmetryPlane` (which retired `SymmetryX/Y/Z`).

### Smaller deferred items
- **Analytic normals** — the 4-tap finite difference is cheap and fine for clean
  fields, but a high-frequency noise field wants analytic (or footprint-scaled)
  gradients; revisit when noise lands.
- The **`SurfaceChart` op** (reserved id 19) — parked pending "noise lib +
  pixel-footprint + atan determinism"; the pixel-footprint half now exists (C).
- The **ellipsoid** (`SdfShapeType.Ellipsoid`) stays a first-order approximation,
  handled conservatively by its eccentricity Lipschitz factor. The exact
  alternative already exists: the 2D-family `Ellipse` revolved at offset 0 is an
  exact spheroid and *does* earn a real cull bound. Prefer it where a bound matters.

## Verifying
Engine-contract work — gated by `Puck.Post` (**not** the greenfield demo). The
world-render stages (`world`, `world-warp`, `world-menagerie`, `world-instanced`,
`world-swarm`, `fuzz`, `rt`) exercise every kernel; the D1/D2 gates are
`sdf-lipschitz` (CPU bake assert) plus `world-warp-solidity` and
`world-log-sphere-solidity` — single-backend "no overstep holes" checks, because
cross-backend parity **cannot** catch overstepping (both backends overstep
identically). Cost via `PUCK_TIMING=1`; the render path also smoke-tests through
the demo (`dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 2`).
See the `verifying-puck-changes` and `sdf-world` skills.
