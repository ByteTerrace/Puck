# LOD & Bounds

In-tree level-of-detail and compile-time / in-tree bounding — no baked
volumes, ever. All entries here sit on **one shared compiler bounds channel**:
per-segment early-out *reads* it, Sphere Carving *fills* it tighter, and the
CGF'25 acceleration nodes *place proxy volumes* into it. Build order follows
that dependency: prove the read pays (early-out) before filling it (carving)
before spending it (proxy/LOD nodes).

### Accelerating Signed Distance Functions (Hubert-Brierre, Guérin, Peytavie, Galin)

- **Source:** Pierre Hubert-Brierre, Éric Guérin, Adrien Peytavie, Eric Galin,
  *Accelerating Signed Distance Functions*, Computer Graphics Forum 44(7),
  2025. DOI 10.1111/cgf.70258. Wiley:
  https://onlinelibrary.wiley.com/doi/10.1111/cgf.70258. Open PDF:
  https://perso.liris.cnrs.fr/eric.galin/Articles/2025-lod.pdf. HAL:
  https://hal.science/hal-05308455v1. DigLib:
  https://diglib.eg.org/items/1a39a460-61c5-4b38-8486-d86353d2d6a4.
- **Digest:** Embeds acceleration nodes directly in the construction tree
  instead of an external octree/BVH. Proxy nodes `P` replace `f` outside a
  volume `V` with a cheap 1-Lipschitz distance-to-`V` bound `b(p)=d(p,∂V)+δ`
  that never oversteps and leaves the surface `O`unchanged, but `P` is
  discontinuous at `∂V` so it can only sit under Boolean/warp operators.
  Continuous proxy nodes `C` fix that for sub-trees under a smooth operator by
  interpolating across a Minkowski shell around `V`, staying globally
  1-Lipschitz at the cost of a slightly larger constant. Continuous LOD nodes
  `L` reuse the same blend but let the region `V` depend on the viewer and
  straddle the surface, giving popping-free viewer-distance LOD that *does*
  change `O`. Normal warping recovers shading detail lost by `L` with a single
  gradient step at negligible cost. Reported GPU speedups reach three orders
  of magnitude (Castle ×439, Wall ×382 on a GeForce 4070) — exactly the
  "big architectural scene from a distance" regime. The paper explicitly
  proposes generating `V` via Sphere Carving.
- **Determinism / cross-backend fit:** Three distinct verdicts. Proxy `P`/`C`
  are geometrically exact (`O'=O`) but change the march path, so the
  quantized hit `t` isn't guaranteed bit-identical at silhouettes — safe under
  the relaxed `mean≤0.35` parity gate, not guaranteed under
  `PUCK_PARITY_STRICT=1` unless proven bit-identical per backend (the same
  posture the existing per-program `stepScale` already occupies). LOD `L` and
  normal warping are approximations (`O'≠O` / shading-only) and must sit
  behind an explicit run-doc coarseness knob, never silent — but they are
  fully deterministic given deterministic camera state: `α` keys on `d(e,p)`,
  and **LOD-by-distance is legal in a deterministic engine only because the
  eye position is fixed-point deterministic sim state**, not wall-clock or
  temporal history.
- **Puck verdict:** ADOPT as the far-field arc — the flagship non-baking
  answer to the town render-range ceiling. Stage it: (1) conservative proxy
  nodes with existing subtree bounds, always-on, relaxed-parity — effort
  **M–L**; (2) continuous distance-LOD behind a run-doc coarseness knob —
  full paper (Sphere-Carved `V` + `L` + normal warp + placement pass) is
  **XL**, best landed staged proxy → LOD-knob → normal-warp.
- **See also:** [README.md](README.md),
  [negative-results-and-rejections.md](negative-results-and-rejections.md),
  [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md),
  [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Sphere Carving

- **Source:** Hugo Schott, Theo Thonat, Thibaud Lambert, Éric Guérin, Eric
  Galin, Axel Paris, *Sphere Carving: Bounding Volumes for Signed Distance
  Fields*, ACM TOG (SIGGRAPH) 2025. DOI 10.1145/3730845. Project:
  https://aparis69.github.io/SphereCarving/index.html. Code:
  https://github.com/H-Schott/SphereCarvingRelease.
- **Digest:** A black-box bounding-volume constructor that works from
  conservative SDF queries alone (each query yields an empty sphere of
  radius `f(p)`). It iteratively carves free space, trilaterates the
  sphere-intersection points into a point set, and runs a convex
  decomposition over that set to emit a tight assembly of half-spaces and
  ellipsoids that converges to the true surface as carving proceeds. Works
  for exact SDFs, Lipschitz SDFs, BlobTrees, and even neural SDFs; needs "a
  small number of function queries" and runs as a preprocess (the paper notes
  a few seconds for some models). It is the paper the CGF'25 acceleration
  nodes cite as their proxy-volume generator — the two are co-designed.
- **Determinism / cross-backend fit:** Runs entirely CPU-side at compile
  time, so the emitted bound data is identical on both backends by
  construction — no GPU float divergence in the bound itself. Bounds are
  conservative by construction, so a carved bound feeding the early-out or a
  proxy `V` preserves the exact-cull contract exactly: a conservatively
  bounded segment the running min already beats genuinely cannot contribute,
  and carving never changes `f` at the surface, only where the bound sits.
  Caveat: carved half-spaces/ellipsoids live in the segment's local space —
  rigid + uniform-scale instance transforms compose cheaply and exactly, but
  non-uniform scale and space-folding ops (`Repeat`/`CellJitter`) need a
  per-instance re-carve clamped to the placement extent, since a carve is
  only valid over the finite region it sampled.
- **Puck verdict:** ADOPT as a compiler subroutine, not a standalone feature
  — effort **M–L**. Value is (1) generating the CGF'25 paper's proxy volumes
  and (2) tight bounds for cases that get none or a loose one today (notably
  `Ellipsoid`, which earns no cull bound from the analytic route since its
  field can underestimate, but is a valid black box for carving). Keep it off
  the interactive rebuild path — async/static-only, since a multi-second
  carve is unacceptable on creator mode's live-edit budget. Medium priority:
  land after the early-out proves the bounds channel pays.
- **See also:** [README.md](README.md),
  [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md),
  [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### SDF Bounding Volumes (iq)

- **Source:** Inigo Quilez, *SDF Bounding Volumes*.
  https://iquilezles.org/articles/sdfbounding/.
- **Digest:** Thread the current running best distance `minDist` into each
  expensive sub-SDF and test its bounding volume first: `if (dB > minDist)
  return minDist;` — if the distance to the bound already exceeds the running
  minimum, nothing inside that volume can be closer, so the sub-tree is
  skipped for free. iq reports ≈8× on a complex multi-part character.
  Caveat: the branch itself is expensive on some platforms (WebGL/mobile), so
  it only pays when the guarded sub-tree's cost dwarfs the bound test.
- **Determinism / cross-backend fit:** The strongest of the three legs —
  bit-identical, contract-preserving, parity-safe with no knob. The
  early-out returns the identical minimum it would have computed anyway (the
  skipped segment, by construction, could not have beaten the running min),
  which is exactly the exact-cull contract: a skipped segment is
  bit-identical to an evaluated far segment. Safe under
  `PUCK_PARITY_STRICT=1`. The only risk is GPU wave divergence eating the
  saving when lanes in a wave disagree on skip-vs-full — a performance
  question to measure, never a determinism one.
- **Puck verdict:** DO THIS FIRST — effort **S–M**. No new ISA and no new
  bounds encoding for the minimal form (the segment directory already stores
  bounds where they exist); just a threaded parameter, a branch, and a
  measurement harness. It is the only leg that is unconditionally
  exact/strict-parity-safe, and its result — measured on the town scene that
  already exceeds the overview camera's render range — settles whether the
  richer bounds from Sphere Carving and the CGF'25 nodes will pay before
  building them.
- **Empirical status (Puck): ground-notch/MaxSteps hypothesis REFUTED**
  (2026-07-08 in-demo correctness hunt). The termination view shows overworld
  ground plus a controlled floor terminating uniformly on the
  footprint-adaptive threshold, never steps-exhausted, with near-black
  iteration counts — the evidence-supported mechanism is footprint-adaptive
  early termination amplified behind occluders by the per-tile marchStart
  teleport, not step exhaustion, so the lever for the notch is the
  footprint-epsilon / marchStart derivation rather than this page's bounds
  work. (Caveat: the notch geometry was not isolated in a single frame; the
  refutation rests on ground-never-red across all captures.) Full record:
  [verdict-index.md](verdict-index.md#empirical-status-in-puck).
- **Empirical status (Puck): the early-out itself is already shipped**
  (2026-07-08 finding). The segment-/shape-level `minDist` early-out this
  entry recommends building first was found **already implemented pre-survey**
  (commit `1a8330f`) — the "do this first" premise was stale. Wave 1 instead
  verified eligibility soundness for the shipped skip test, which held. Full
  record: [verdict-index.md](verdict-index.md#empirical-status-in-puck).
- **See also:** [README.md](README.md),
  [marching-acceleration.md](marching-acceleration.md),
  [verdict-index.md](verdict-index.md), [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Segment Tracing Using Local Lipschitz Bounds

- **Source:** Eric Galin, Éric Guérin, Axel Paris, Adrien Peytavie, *Segment
  Tracing Using Local Lipschitz Bounds*, Computer Graphics Forum 39(6), 2020.
  DOI 10.1111/cgf.13951. Wiley:
  https://onlinelibrary.wiley.com/doi/full/10.1111/cgf.13951. HAL:
  https://hal.science/hal-02507361.
- **Digest:** Tracks a local (per-operator/per-primitive) Lipschitz bound
  along a ray segment rather than a single global clamp, allowing much
  larger, still-safe marching steps with no extra acceleration structure —
  the direct ancestor of the CGF'25 acceleration-nodes paper above, which
  cites it as its formal foundation. On the roadmap as the directional
  Lipschitz baseline this survey's compiler-tracked `stepScale` already draws
  from.
- **Determinism / cross-backend fit:** Compatible with a deterministic
  per-pixel interpreter — no temporal state, pure per-segment closed-form
  bound propagation, matching the compiler-tracked-Lipschitz pattern the
  engine already uses for its per-program `stepScale`.
- **Puck verdict:** surveyed, not deep-reviewed as a standalone leg here —
  its content is folded into the review-08 deep review of the CGF'25 paper
  above (which builds directly on it) rather than assessed independently;
  treat this as venue/citation provenance and land it together with that
  work if segment tracing itself is revisited.
- **See also:** [README.md](README.md),
  [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md),
  [marching-acceleration.md](marching-acceleration.md),
  [../sdf-sota-survey.md](../sdf-sota-survey.md).

### Far-field proxy patterns from production engines (UE Global Distance Field clipmaps + Lumen near/far split)

- **Source:** Epic Games, *Mesh Distance Fields*.
  https://dev.epicgames.com/documentation/unreal-engine/mesh-distance-fields-in-unreal-engine.
  Daniel Wright, Krzysztof Narkowicz, Peter Kelly, *Lumen: Real-time Global
  Illumination in Unreal Engine 5*, Advances in Real-Time Rendering, SIGGRAPH
  2022. https://advances.realtimerendering.com/s2022. Epic Games, *Lumen
  Technical Details*.
  https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine.
- **Digest:** UE composites every per-mesh baked distance field in view into
  camera-centered volume-texture clipmaps (cascades doubling in size with
  distance), giving an instance-count-independent global field for
  long-range tracing. Lumen traces the precise per-mesh distance field for
  near/detail geometry but falls back to the coarser Global Distance Field
  for the remainder of a ray — a near-field-precise / far-field-coarse split.
  The transferable idea for a non-baking engine is the *pattern*, not the
  storage: reduce geometric fidelity as a function of distance from the
  camera, structured as a near/far split rather than one fidelity everywhere.
- **Determinism / cross-backend fit:** The clipmap-of-composited-fields
  architecture and the near/far split are, in the abstract, deterministic —
  pure functions of static per-frame scene state. But the concrete UE
  mechanism is a baked-volume representation (offline-baked per-mesh SDFs
  composited into GPU-resident volume textures), which is exactly what this
  engine's analytic-programs-only posture refuses. Lumen's Surface Cache
  compounds this with genuinely temporal, multi-frame-amortized lighting
  updates that would need full re-derivation as deterministic per-tick state
  to be usable here at all.
- **Puck verdict:** surveyed, not deep-reviewed. Not adopted as built — the
  storage mechanism is a baked volume. The one transferable, non-baking
  pattern is distance-based fidelity reduction itself, which this survey
  adopts analytically instead via the CGF'25 continuous LOD nodes `L` above
  (viewer-distance-dependent blending inside the construction tree, no
  volume texture). Baked-volume realizations of clipmap/cascade-style
  far-field acceleration are tracked separately as a rejected/deferred
  family.
- **See also:** [README.md](README.md),
  [negative-results-and-rejections.md](negative-results-and-rejections.md),
  [../sdf-sota-survey.md](../sdf-sota-survey.md).
