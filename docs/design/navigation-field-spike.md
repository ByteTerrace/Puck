# Navigation field — design spike

**Status: DESIGN ONLY.** Nothing in this document is implemented. It exists to settle the
representation, and above all the determinism/quantization boundary, before an impl lane writes
any code. No plan of record sequences that lane yet.

**Scope.** A general navigation primitive for ALL worlds — not a dungeon feature. Walkability is
*derived* from the SDF a world already authors (never hand-placed), built per chunk on demand, and
queried through a split surface: a shared flow field for many-agents-few-goals, per-agent
hierarchical A* for scattered goals. A single static dungeon is the degenerate case of the same
machinery, not a separate code path.

---

## 0. The one decision that gates everything else

Puck already crosses the float→fixed-point boundary for SDF queries exactly once, and it does so
**before** any per-query arithmetic runs, not per-sample:

- `SdfFieldEvaluator` (`src/Puck.SdfVm/Queries/SdfFieldEvaluator.cs:86-90`) takes an `SdfProgram`
  (whose instruction operands are authored/baked as `float`/`Vector3`) and its constructor calls
  `Compile` (`SdfFieldEvaluator.cs:354-385`), which walks every instruction **once** and converts
  every `Data0`/`Data1` float lane to `FixedQ4816` via `FixedQ4816.FromDouble` (`.cs:368-381`).
  After construction, `m_instructions` is an all-`FixedQ4816` array and every query
  (`TryDistance`, `TryFieldGradient`, `Raycast`, `SphereCast`, `Overlap`, `TryGroundHeight`,
  `LineOfSight`) is pure integer fixed-point arithmetic — no `float`, no `double`, anywhere in the
  per-sample path.
- The same discipline is named explicitly for a 2D grid bake: `WorldQueryBaker`
  (`src/Puck.SdfVm/Queries/WorldQueryBaker.cs:6-9`) calls it "quantize-once-per-edge" — every
  authored rectangle edge is snapped to raw Q48.16 via `FixedQ4816.FromDouble` exactly once
  (`.cs:32-35, 72, 106-107`), and every per-cell loop after that is pure integer arithmetic.
- Collision/contact tuning follows the identical shape: `FixedWorldCollision.Compile`
  (`src/Puck.World.Data/WorldDefinition.cs:2213-2219`) converts the authored contact skin, slope
  threshold, and gradient probe from `double` to `FixedQ4816` once, and `WorldSolidField` never
  reads the authored floats again (`src/Puck.World.Server/WorldSolidField.cs:46-55`).

**The navigation field adopts this exact discipline and nothing new.** There is only one place a
`float` is allowed to exist on the path from "authored SDF" to "a mob's next tick position": the
one-time bake step that produces the chunk's baked cells. From the moment a chunk's baked array
exists, every consumer — flow-field propagation, A* cost accumulation, agent steering — reads only
`FixedQ4816`/`int` values and touches no float, ever, on the simulation-tick path. This is not a
new rule; it is Puck's existing SDF-query rule, applied to one more consumer.

The rest of this document is that discipline worked through for a *chunked, hierarchical,
dynamically-dirtied* consumer, which the existing bake surfaces (whole-world, static) don't have to
handle.

---

## 1. Reuse survey

Everything below was verified against the current tree (`git rev-parse HEAD` at time of writing:
`4cdbfceb`, reset from a stale sibling worktree base — see the session's tie-break note below) with
`puck search` / direct reads. No pathfinding, navmesh, flow-field, or chunk primitive exists
anywhere in the repo today (`puck search 'A-Star|AStar|Pathfind|FlowField|NavMesh' -M 0 src` and
`puck search 'class.*Chunk|struct.*Chunk' -M 0 src` both return zero matches) — this is genuinely
greenfield, and nothing here conflicts with an existing chunk system because there isn't one.

### 1.1 The deterministic SDF query substrate (the load-bearing reuse)

| Provides | Where | Notes |
|---|---|---|
| `IWorldQuery` — `Raycast`, `SphereCast`, `Overlap`, `TryGroundHeight`, `LineOfSight`, all fixed-point in/out | `src/Puck.SdfVm/Queries/IWorldQuery.cs:51-95` | The shared query seam. Two implementations exist today; navigation should be a third caller of the same seam, not a parallel one. |
| `SdfFieldEvaluator` — exact, live-program evaluator; `TryDistance` (signed clearance + material), `TryFieldGradient` (unit gradient via 6-tap per-axis central difference) | `src/Puck.SdfVm/Queries/SdfFieldEvaluator.cs:96-205, 208-240` | `Capabilities.WarpFree = true` (`.cs:93`); rejects (at construction, once) any program using an op/shape needing runtime trig not in fixed point — see the constructor's excluded-op list (`.cs:15-24, 395-428`). `TryDistance` returns `false` only when the compiled PROGRAM contains no shape at all (`!sawShape`) — a program-level check, never position-dependent; a navigation bake must tolerate that program-level failure exactly like contact resolution already does. |
| `BakedWorldQuery` + `WorldQueryArtifact` + `WorldQueryBaker` — an *existing* resolution-quantized 2.5D grid: bit-packed blocked bitmap, per-cell height, `WorldQueryConfidence.Bounded` vs `.Exact` | `src/Puck.SdfVm/Queries/{BakedWorldQuery,WorldQueryArtifact,WorldQueryBaker}.cs` | **This is the closest prior art for a baked navigation representation.** Row-major `ulong`-packed occupancy (`WorldQueryArtifact.cs:16-18`, identical packing scheme reusable verbatim), raw-Q48.16 origin/cell-size fields so the artifact round-trips deterministically regardless of the float geometry that baked it (`.cs:4-8`), and a named `Confidence` axis distinguishing "exact live query" from "quantized bake" that answers the same question this spike needs to answer for navigation. The baker's default cell size is a *raw* constant, `CellSizeRaw = 16384L` = 0.25 world units in `FixedQ4816`, never re-derived from a float at bake time (`WorldQueryBaker.cs:15-17`). |
| `IFieldEvaluator.Capabilities.WarpFree` | `src/Puck.SdfVm/Queries/IFieldEvaluator.cs` (referenced at `SdfFieldEvaluator.cs:93`) | The capability flag a navigation bake should also check before trusting a program's gradient/clearance semantics. |

Comments in this trio explicitly reference a predecessor "walk grid" (`BakedWorldQuery.cs:6-13`,
`WorldQueryArtifact.cs:18,49`, `WorldQueryBaker.cs:12,14,25`) with the same cell size and packing —
but no `WalkGrid` type exists in the tree today (`puck search 'WalkGrid' -M 0 src` — zero matches).
Treat those comments as documenting a *packing convention* worth matching, not a live component to
extend.

### 1.2 Fixed-point primitives

| Provides | Where | Notes |
|---|---|---|
| `FixedQ4816` — Q48.16 (48 integer bits, 16 fraction bits), deterministic across machines | `src/Puck.Maths/FixedPoint/FixedQ4816.cs:20-24` | Resolution `2^-16 ≈ 0.0000153` world units (documented at `SdfFieldEvaluator.cs:38-41`). Ties-to-even is the general rounding law; one documented half-up exception at `Exp2`'s `-17` boundary (`FixedQ4816.cs:711`) — irrelevant to navigation unless a future cost function uses `Exp2`. |
| `FixedVector3` — `Dot`, `Cross`, `Length`, `LengthSquared`/`TryLengthSquared`, `Normalize`, `Lerp`, `MoveToward` | `src/Puck.Maths/FixedPoint/FixedVector3.cs` | `Dot`/`Cross` widen to `Int128` above a magnitude threshold rather than overflow silently (`.cs:81-99, 107-129`) — the pattern a flow-field cost accumulator should copy rather than accumulate in raw `long`. |
| `FixedPosition` — cell index (`long`, 3-axis) + centred `FixedVector3` local offset, `2^20`-unit cells | `src/Puck.Maths/FixedPoint/FixedPosition.cs:16-24` | **This is the hierarchical-coordinate precedent the chunk index should mirror**: coarse integer address (here, a world cell) plus a fine fixed-point local offset, with exact, overflow-checked translation between them (`TryTranslate`, `TryDelta`, `.cs:116-146, 164-188`). A chunk coordinate is naturally "coarse chunk index (long, 3-axis or 2-axis) + local `FixedVector3` inside the chunk," at one more level of the same scheme `FixedPosition` already uses for world cells. `WorldCoord3` (`src/Puck.Maths/WorldCoord3.cs`) is the same struct duplicated for a pre-`FixedPosition` naming generation — confirm with the owner at impl time whether navigation should depend on `FixedPosition` (the one `SdfFieldEvaluator`/`IWorldQuery` already use) or whether `WorldCoord3` is still live for a different consumer; don't introduce a third parallel copy. |
| `FixedQuaternion.Rotate`/`RotateInverse` | used throughout `WorldSolidField.cs` | Needed if agent facing/steering wants oriented local frames; not obviously needed for a scalar walkable field. |

### 1.3 What `WorldPopulation` already computes deterministically, fixed-point

| Provides | Where | Notes |
|---|---|---|
| `BodyTargetConeSense.Contains` — origin/forward/candidate cone test, returns `distanceSquared` as an out-param so the caller never redoes the subtraction | `src/Puck.World.Data/WorldDefinition.cs:629-641` | `delta.LengthSquared` (the saturating, overflow-safe property) — not raw multiplication — is the call it makes (`.cs:632-633`). This is the primitive a "sensed nearby agents for separation" query should call, not a hand-rolled squared-distance. |
| `WorldPopulation.HasLineOfSight` | `src/Puck.World.Server/WorldPopulation.cs:1182-1186` | Thin wrapper over `m_targetField?.LineOfSight(...)` — i.e. over the exact same `IWorldQuery`/`SdfFieldEvaluator` seam section 1.1 describes. Confirms LOS-for-gameplay and LOS-for-navigation should be the *same* query, not two. |
| `WorldPopulation.DistanceSquared` (private) | `src/Puck.World.Server/WorldPopulation.cs:1188-1194` | A **second**, hand-rolled squared-distance helper, `dx*dx+dy*dy+dz*dz` in raw `FixedQ4816` multiplication with no overflow widening — used only for a sensor "current target still in range" re-check. Note the duplication with `FixedVector3.LengthSquared` for the impl lane; navigation code should call the vector member, not copy this helper a third time. |
| `WorldSolidField.TryUp` — "up" is the field gradient when the world authors `GradientDerivedUp`, world `+Y` otherwise | `src/Puck.World.Server/WorldSolidField.cs:252-272` | Direct precedent for "local avoidance = the SDF gradient": under `GradientDerivedUp` the engine already steers a grounded body by `TryFieldGradient` today, just for the vertical axis instead of the horizontal escape direction navigation wants. Same call, different use of the same vector. |

### 1.4 Grid indexing / locality

| Provides | Where | Notes |
|---|---|---|
| `HilbertCurve.Encode`/`Decode` — exact-integer, bijective, locality-preserving 2D↔1D | `src/Puck.Maths/Geometry/HilbertCurve.cs:16-77` | Its own doc names the exact use case: *"cache-coherent chunk and tile ordering"* (`.cs:10-14`). Candidate for ordering a world's live chunk set for streaming/eviction priority and for a chunk-local cell's storage index within a chunk (`order` must stay in `[1,31]`, i.e. chunks up to `2^31` cells/side — no chunk will need that, so pick a small fixed `order` per chunk edge length). Exact inverse on every machine (`.cs:6-7`) — safe on the determinism boundary. |
| `LayerSequence` — closed-form index↔concentric-layer mapping | `src/Puck.Maths/Geometry/LayerSequence.cs:16-35` | A possible fit for *streaming radius* (which ring of chunks around a player is resident) if that ever needs a closed-form "which layer is chunk N in" query instead of a simple Chebyshev-distance comparison. Not clearly needed — flag as a maybe, not a decision. |
| `MonotonicPartitioner` | `src/Puck.Maths/MonotonicPartitioner.cs:51-655` | Considered and **rejected** for chunk indexing: it is a consistent-hash shard router over a flat `ushort`/`Guid` domain (network partition routing), not a locality-preserving spatial map — adjacent chunks would land in unrelated buckets. Not a fit here. |

### 1.5 Deterministic tie-break house style

Puck already has a settled answer for "two candidates compare equal — what next," used in at least
two unrelated places:

- `src/Puck.Maths/Oracle/SmithNormalForm.cs:551` — *"smallest row and then the smallest column.
  Row-major iteration IS that tie-break."*
- `src/Puck.World.Server/IWorldGrantsView.cs:138` — *"a deterministic order — capability
  declaration order, then a stable (kind, value, id) order."*

The house pattern is: **break ties by a fixed structural key derived from the data's own address,
never by insertion/hash-map/iteration order.** Section 3.4 applies this directly to flow-field and
A* frontier expansion.

### 1.6 Document/world-count facts relevant to a future impl lane

Four shipped worlds exist today under `src/Puck.World/Assets/worlds/*.world.json` (`play`, `dive`,
`kart`, `jump` — the four-world charter's whole roster, 2026-08-06) — the shipped-world sweep an impl
lane must run if it adds a top-level document section. No world today streams its SDF in chunks; each
world's solid geometry compiles into ONE `SdfProgram`/`SdfFieldEvaluator` at boot or rebuild
(`WorldPopulation.CompileFixedTables`, `src/Puck.World.Server/WorldPopulation.cs:183-227`,
`WorldSolidField.TryBuild`, `src/Puck.World.Server/WorldSolidField.cs:92-168`). This is exactly why
the design below treats "one whole world = one chunk, built once, never dirtied" as the honest
degenerate case rather than an approximation to special-case away.

---

## 2. Representation

### 2.1 The walkable predicate

For an agent of radius `r` (a `FixedQ4816`, never a `float`), a point is walkable iff:

```
clearance(p) = TryDistance(p).distance   [FixedQ4816, exact per §0]
walkable(p, r) = TryDistance(p) succeeded AND clearance(p) >= r
```

This is a direct extension of what `WorldSolidField` already does for collision (`ResolveSphere`,
`WorldSolidField.cs:344-385`, treats `distance >= radius + skin` as "not penetrating"). Navigation
asks the same field the same question — "how much room is here" — and answers by comparison against
an agent radius instead of a body's collider volume. No new geometry query verb is needed; `TryDistance`
already answers it.

A second, optional predicate layers on top for *grounded* agents: `walkableGrounded(p, r)` also
requires `TryUp`/`TryFieldGradient` at `p` to exist and the surface normal's alignment with the
agent's up axis to clear a slope threshold — literally `WorldSolidField`'s existing
`m_groundedThreshold` test (`WorldSolidField.cs:711`), reused rather than reinvented, so a walking
mob and a standing player agree about what counts as "floor" without a second slope constant to keep
in sync.

### 2.2 Per-cell baked value

Following `WorldQueryArtifact`'s shape (§1.1), each chunk bakes one cell array. Unlike
`WorldQueryArtifact`'s single blocked bit, a navigation cell needs enough information to answer
"walkable for THIS agent radius" without re-marching the SDF per query, and enough to drive a cost
function:

- **Clearance** (`FixedQ4816`, one value per cell, sampled at the cell center) rather than a
  boolean, so a single baked chunk serves every agent radius up to some declared maximum — a cell is
  walkable for radius `r` iff `bakedClearance >= r`. This is strictly more general than a
  per-radius boolean bake and costs one `FixedQ4816` (8 bytes) per cell instead of one bit, which is
  a deliberate size/generality trade the impl lane should confirm against real chunk cell counts.
- **Grounded flag** (1 bit) — whether the slope test in §2.1 passed at bake time, for worlds using
  the grounded predicate.
- **Material** (already returned by `TryDistance` at zero extra cost, `SdfFieldEvaluator.cs:96-104`)
  — carried through unconditionally; costs nothing to keep and lets a future cost function
  (mud slows movement, lava blocks it) read it without a second SDF pass.

Conservative dilation (a cell reads as non-walkable if *any* sub-sample within it fails, not just
the center) is an open question — see §5.

### 2.3 Coordinate scheme

Extend `FixedPosition`'s cell/local split by exactly one level (§1.2): a **chunk coordinate** is a
3-tuple of `long` chunk indices (or 2-tuple, if navigation stays XZ-planar — see §5) plus a
`FixedVector3` local offset inside the chunk, using the identical overflow-checked
translate/delta arithmetic `FixedPosition` already implements. A chunk sits inside exactly one
`FixedPosition` world cell for any reasonable chunk size (world cells are `2^20` units; a chunk on
the order of tens of units is nowhere near that boundary), so the existing world-cell scheme is
untouched — chunking is a subdivision *within* a world cell, not a competing coordinate space.

---

## 3. The determinism / quantization decision (primary)

This is the deliverable §0 promised worked through in full. Four sub-decisions, each pinned:

### 3.1 Where the float→fixed boundary sits

**Exactly once, at chunk bake time, and nowhere else.** A chunk's bake step:

1. Reads the world's already-fixed-point-compiled `SdfFieldEvaluator` (§1.1 — the float→fixed
   conversion for the geometry itself already happened once, at world/`WorldSolidField` build time,
   not at chunk-bake time; the chunk bake does not re-touch a single float from the SDF program).
2. Samples `TryDistance` at each cell center (`FixedPosition`, exact per §0) — the ONLY new
   information a chunk bake produces, and it is `FixedQ4816` from a `FixedQ4816`-in/`FixedQ4816`-out
   call. **No `float` is read or produced anywhere in this step.**
3. Writes the baked `FixedQ4816` clearance (and grounded bit, material) into the chunk's cell array.

After step 3, the chunk's baked array is the sole input to every downstream consumer — flow-field
propagation, A* — and none of them ever calls `TryDistance` again on the simulation-tick path
(they may call it once more, off the tick path, when a chunk is *rebuilt* after a dirty event; see
§4). This mirrors `WorldQueryBaker`'s own framing exactly (§1.1): bake touches the exact query
surface once, every consumer after that touches only integers.

**Consequence for agent radius:** an agent's radius must already be a `FixedQ4816` wherever it's
authored (a kit tuning row, matching `WorldCollider`'s existing fixed-compiled shape — see
`FixedWorldCollision.Compile`, `WorldDefinition.cs:2213-2219`, and `WorldCollider`'s own compiled
volumes referenced by `WorldSolidField.Resolve`, `.cs:242-301`). The walkable comparison
`bakedClearance >= agentRadius` never converts either side through `float`/`double` — this is a
plain `FixedQ4816` comparison, using the type's own deterministic `<=`/`>=` operators.

### 3.2 Cell-center sampling vs. conservative dilation

Two choices, not yet decided (flagged as an open question, §5), but BOTH candidates stay entirely
inside FixedQ4816:

- **Point-sample** (cheap): one `TryDistance` per cell center. Risk: a thin wall thinner than one
  cell can sit entirely between two sample points and read as open on both sides.
  `WorldQueryBaker.MarkBlocked` accepts the equivalent risk for its blocked bitmap today (a rectangle
  narrower than one cell can be missed by a naive span-only approach; `TryCellSpan`'s ceil/floor
  span math, `WorldQueryBaker.cs:105-115`, is written to *not* miss thin spans, which the navigation
  bake's sampling should copy rather than accept the naive risk).
- **Multi-sample / erosion** (safer, costlier): sample a small fixed-point offset pattern per cell
  (e.g. the 4 corners, or a scaled-down version of `SdfFieldEvaluator`'s own central-difference
  gradient offsets) and take the minimum clearance. Still zero floats — the offsets are
  `FixedQ4816` constants exactly like the evaluator's axis probes already are.

Either way, the sampling PATTERN (which offsets, in what order) must be a fixed, checked-in
constant, not derived from anything that varies by machine (thread count, SIMD width) — the same
constraint `SdfFieldEvaluator`'s own gradient probe already satisfies (`GradientEpsilon`,
`.cs:45`, a named `FixedQ4816` constant, not re-derived).

### 3.3 Cost quantization for flow field / A*

A flow field's propagated cost and an A* node's `g`/`h` must be `FixedQ4816` (or a plain integer
tick-count — see below), accumulated with the SAME overflow-safe widening `FixedVector3.Dot`/`Cross`
already use (`FixedVector3.cs:81-99, 107-129`: fall back to `Int128` above a magnitude threshold)
rather than raw `long` addition that could wrap silently on a large or long-running world. Two
concrete options for the impl lane to choose between, both fully deterministic:

- **Distance-based cost** (`FixedQ4816`, Euclidean or octile step cost between cell centers) —
  matches how a human reads "shortest path," but requires a canonical `Sqrt` for diagonal steps;
  `FixedQ4816.Sqrt` already exists and is used throughout the SDF evaluator (`SdfTrapezoid2D`,
  `SdfFieldEvaluator.cs:546`, etc.) — reuse that one function, never a second square-root
  implementation, so two cost accumulations of the same path never disagree by a rounding-law
  mismatch.
- **Tick-based cost** (plain non-negative integer, e.g. "cells traversed weighted by material") —
  avoids `Sqrt` entirely, trivially exact, and is the safer default for a first pass since it removes
  an entire class of fixed-point rounding question. Diagonal-vs-orthogonal weighting (if wanted)
  becomes one more small integer constant, not a geometric distance.

Either choice is a closed, finite domain (`FixedQ4816`'s own range, or a bounded integer), so
"deterministic" here reduces to "never let a float touch this accumulator" — already guaranteed by
construction once §3.1 holds.

### 3.4 Deterministic tie-break

Per §1.5's house style, when two frontier candidates (flow-field BFS/Dijkstra fringe, or A*'s open
set) compare equal on cost, the tie breaks on a **fixed structural key derived from the cell's own
address** — the row-major (or Hilbert-curve-encoded, §1.4) linear index within the chunk, then the
chunk's own coordinate tuple for a cross-chunk tie. This must NOT be:

- Insertion order into a priority queue (varies with which neighbor happened to be visited first,
  which can vary if a future parallel bake visits neighbors out of a fixed order).
- A managed hash-map/hash-set enumeration order (never guaranteed stable across .NET versions or
  even across runs).
- Entity/agent instance identity *unless* that identity is itself a stable, document-derived index
  (matching `WorldPrincipal.Peer(index, generation)`-style stable identity, not a runtime object
  reference).

This single rule, stated once, is what keeps a flow field or an A* path bit-identical across
machines and across backends even when the underlying algorithm has genuine ties to break — exactly
the failure mode the head brief calls out as the landmine an unspecified tie-break leaves for the
impl lane.

---

## 4. Chunk & dirty model

- **Chunk size** is a world-authored constant (candidate default: matching `WorldQueryBaker`'s own
  `CellSize = 0.25` world units per cell, §1.1, times some small fixed cell-count per chunk edge —
  concrete numbers are an impl-lane tuning question, not a design-spike decision).
- **Build on demand.** A chunk has no baked array until something asks a navigation query that
  touches it — the first `TryDistance`-derived walkable query, or the first flow-field seed, inside
  a chunk's bounds triggers its bake. An unbounded/streaming world never bakes a chunk nobody
  queries.
- **Dirtied locally.** A chunk's baked array is invalidated when the world's SDF changes in a way
  that could move its geometry — i.e. exactly the same event that already invalidates
  `WorldSolidField` today (`WorldPopulation.Rebuild`'s `solids` parameter, a live
  solid-geometry or collision-tuning edit — see `WorldPopulation.cs:301-361` and the
  `Rebuild` doc comment at `.cs:291-300`). The invalidation must be scoped to chunks whose bounds
  intersect the changed geometry's bounds, not a whole-world rebuild — this is the one place the
  spike departs from today's `WorldSolidField`, which *is* rebuilt whole-world on any change
  (`ResolveContactField`, `.cs:268-278`, always rebuilds the one shared field). A chunked navigation
  bake must track which chunk(s) a changed solid/placement overlaps and re-bake only those; the
  mechanism for that spatial-overlap test (broad-phase AABB against chunk bounds, most likely) is an
  open question, §5.
- **Portal graph incrementally updated.** A chunk rebake recomputes only that chunk's boundary
  connectivity to its immediate neighbors (§5 for exact shape), not the whole coarse graph —
  otherwise a single dirtied chunk in a large streamed world forces an unbounded re-solve.
- **Degenerate case: one chunk, static, never dirtied.** A world whose whole solid geometry is one
  `WorldSolidField` compiled once at boot (true of every shipped world today, §1.6) needs exactly
  one chunk, baked once, portal graph empty (no neighbors), and flow-field/A* degrade to their
  single-region form automatically — this is not a special case in the design, it's `chunkCount ==
  1` falling out of the general machinery with no branch.

---

## 5. Hierarchical / portal model, including cross-world

- **Chunk-level portal graph.** Each chunk boundary shares an edge (2D) or face (3D, if navigation
  is not planar-restricted — open question) with up to N neighbors. A **portal** is a maximal
  walkable span along a shared boundary — a coarse graph node per chunk, edges to neighbor chunks
  weighted by the (baked, fixed-point) traversal cost of crossing between portal midpoints. This is
  the standard chunked-navmesh hierarchy shape; nothing about Puck's substrate changes the shape,
  only the arithmetic it runs in (all `FixedQ4816`/integer, per §3).
- **Coarse search, fine search.** A long route between chunks searches the coarse portal graph
  first (small node count, cheap even with per-agent A*), then resolves the fine, in-chunk path
  only within the 1-3 chunks the coarse route actually crosses at any one time — never a fine-grained
  search across the whole route up front. This is what makes an unbounded streamed world tractable:
  the coarse graph's node count is `O(resident chunk count)`, not `O(world size)`.
- **Cross-world / federation.** At the top of the hierarchy, the portal graph's node granularity
  becomes "world" instead of "chunk" — consistent with the federated-worlds model where every player
  owns a world and worlds compose (see the project's federation doctrine; not re-derived here since
  it is out of this spike's code surface). A cross-world portal edge is authored, not derived (there
  is no SDF spanning two worlds to sample a clearance from) — this is the one place the "derived,
  not authored" rule in the head brief does not apply, and that exception should be stated
  explicitly wherever this design is implemented, not left implicit.
- **Planar vs. volumetric.** Not decided here: whether the fine within-chunk representation is a 2D
  grid (XZ, with height read separately via `TryGroundHeight`/the grounded predicate, §2.1 — matching
  how `BakedWorldQuery` and `WorldQueryArtifact` are already 2.5D, §1.1) or a genuine 3D voxel grid
  (needed for flying/swimming agents or true multi-level 3D geometry). The 2.5D choice reuses far
  more existing machinery (§1.1's whole baked-grid precedent is 2.5D) and matches every shipped
  world's current geometry; a 3D voxel chunk is a strictly larger data/compute cost that should be
  justified by an actual flying/swimming agent requirement before it's built. **Recommendation:
  start 2.5D, leave the chunk format able to add a Z-layer later** — flagged as an open question for
  the impl lane to ratify, not decided here.

---

## 6. Query split

- **Shared flow field (many agents, few goals).** One goal (or small goal set) seeds a
  Dijkstra/BFS-style cost propagation across a chunk's (or several chunks', via the portal graph)
  baked cells, producing a per-cell "direction toward the goal" vector or "next cell" pointer, shared
  by every agent heading to that goal. Cost accumulation and tie-break per §3.3-3.4. This is the
  right tool whenever goal count is small relative to agent count (a crowd converging on one
  objective, mobs swarming a player) — one propagation serves unboundedly many followers.
- **Per-agent hierarchical A* (scattered goals).** When goals are as numerous/varied as agents (each
  mob has its own wander target, a quest NPC has its own route), a shared flow field wastes memory
  and propagation time on goals with one follower each. Per-agent A* over the coarse portal graph
  (§5), refined to fine in-chunk search only for the 1-3 chunks currently relevant, is the right tool
  here. Same baked cells, same cost function, same tie-break — only the search shape differs.
- **Local avoidance = the SDF gradient.** An agent already following a flow-field direction or an
  A* waypoint steers away from a nearby surface using `TryFieldGradient` at its own position —
  literally the same call `WorldSolidField.TryUp` already makes (§1.3), just read as a horizontal
  escape direction instead of a vertical up axis. No separate "avoidance field" needs baking; the
  live gradient query already answers "which way is away from the wall," and it's cheap (4
  `TryDistance` taps, `SdfFieldEvaluator.cs:226-234`) because it's one query per agent per tick, not
  one per baked cell.
- **Agent-vs-agent separation.** A light separation term over nearby agents, using
  `FixedVector3.LengthSquared`/`BodyTargetConeSense.Contains`'s existing squared-distance pattern
  (§1.3) rather than a new distance primitive — sum a small repulsion vector from every other agent
  within some fixed radius, weighted by inverse squared distance or similar, all `FixedQ4816`. This
  is a per-tick, per-agent-pair-in-range computation, not baked; bounding the "nearby agents" set
  (a spatial hash or the same chunk grid used for navigation) is an open question, §5/§7.
- **Dungeon degenerate case.** One static chunk (§4), one flow field seeded once at the goal (the
  exit, or a fixed objective), every mob in the dungeon reads the same field. No portal graph
  traversal (one chunk, no neighbors), no per-agent A* needed unless a mob has an individual goal —
  and if it does, that's the scattered-goal path already described, applied to a graph with one node.
  Nothing about the dungeon case is special-cased; it's what the general machinery does when
  `chunkCount == 1`.

---

## 7. Open questions for the impl lane

1. **Chunk edge length and cell resolution** — concrete numbers, tuned against real world/kit
   authoring, not decided here beyond "match `WorldQueryBaker`'s existing 0.25-unit cell precedent
   unless measurement says otherwise."
2. **Point-sample vs. multi-sample/erosion bake** (§3.2) — thin-wall risk vs. bake cost; needs a
   concrete authored-wall-thickness floor to reason about.
3. **Distance-cost vs. tick-cost** for flow field / A* (§3.3) — tick-cost is the safer first cut;
   confirm before committing to `Sqrt`-bearing distance costs.
4. **Planar (2.5D) vs. volumetric (3D) chunk format** (§5) — recommend starting 2.5D; needs an
   owner ruling before an impl lane locks the chunk schema, since the schema shape differs.
5. **Dirty-scoping mechanism** — how a changed solid/placement's bounds map to the set of chunks to
   re-bake (broad-phase AABB test, most likely) and how that interacts with `WorldSolidField`'s own
   existing whole-field rebuild trigger (§4) without re-baking every chunk on every unrelated edit.
6. **Portal-graph edge weight recomputation cost** — whether a single-chunk rebake's neighbor-facing
   portal recompute is cheap enough to run inline on the tick that dirties it, or needs to be
   deferred/amortized across ticks for a large streamed world.
7. **Where the baked chunk data lives** — whether it round-trips through `WorldQueryArtifact`
   itself (extended with a clearance layer) or a new, navigation-specific artifact type; §1.1's
   `WorldQueryArtifact` is the closest shape but was designed for a single whole-world 2.5D bake, not
   a chunked, incrementally-rebuilt one — reusing the type directly vs. defining a chunked sibling
   type is an impl-lane call once the schema questions above are answered.
8. **Document surface** — if any of this needs a document section (chunk-size tuning, per-kit agent
   radius, goal declarations), it is a NEW top-level or nested section and inherits the strict-parse
   + shipped-world sweep obligation (§1.6) in whatever change adds it. Nothing in this spike proposes
   the JSON shape.
9. **Cross-world portal authoring** (§5) — where in the document model an authored cross-world edge
   lives, and how its cost compares against a same-world derived edge's cost, given the two are
   computed by entirely different means (authored constant vs. baked propagation).
10. **Agent-vs-agent separation neighbor query** (§6) — whether it reuses the navigation chunk grid
    directly as a broad-phase spatial index or needs its own, given agents move continuously while
    chunks are the coarser navigation unit.

---

## 8. Non-goals of this spike

- No code, no schema, no document section, no console verb — all deferred to the impl lane per the
  head's own charter.
- No claim about GPU-side navigation (debug visualization, a navigation heatmap render) — this
  spike is the simulation-facing fixed-point side only.
- No re-litigation of the federation/authority model — §5's cross-world note assumes that model as
  given and does not restate it.
