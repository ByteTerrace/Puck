# Physics and kernels: lessons from Box3D and OpenBLAS

The owner asked what Puck can learn now from Box3D (Erin Catto's 3D engine,
the successor to Box2D v3) and from OpenBLAS, for accuracy and for
performance. This memo is the research round's answer. It reads Puck's live
physics paths, names what the debug world measured, sets each lesson against
the opened sources (several independent ones per topic, with their
disagreements), ranks the lessons, and gives a brief for each of the top
two (the gyroscopic term, the rest latch and brief B have landed). Every page cited under [Sources](#sources) was opened for this memo;
a claim that rests on a page that could not be opened is marked so.

Nothing here ran a World or changed engine code. The counts quoted are the
ones recorded in commits, and each brief starts by taking the measurement its
claim rests on.

The frame (AGENTS.md): determinism pins the mapping, so fixed point and a
stable order give bit-identical state on every machine, backend and thread
count (rule 4); Puck code over engine code; one implementation per mechanism;
and "Puck is free to learn and combine", so each lesson says what transfers
from a float engine and what Puck's integers make better.

## What Puck does today

### The live paths

- **Kit bodies** (walkers, seats, inhabitants) advance one at a time inside
  `WorldPopulation.AdvanceSimulated`
  (`src/Puck.World.Server/WorldPopulation.Step.cs:820`). Each step resolves its
  swept move through `FixedStaticContactSolver.ResolveSweep`
  (`src/Puck.Physics/FixedStaticContactSolver.cs:260`). The move is cut into
  slices of half the body's least support radius (`:279`). Each slice's
  endpoint is relaxed for up to `MaxIterations` passes (`:381`), and every
  pass re-runs the exact collider query (`TryGetPush`, GJK and EPA for convex
  kinds) for every collider whose box comes within the skin of the body's box
  (`Misses`, `:143`; counted at `:161`). Candidates come from a 4 m grid over
  the static colliders (`WorldColliderSet.cs:66-71`). A step that ends
  ungrounded on a crest or a descent runs `FixedStepUp.TrySnap`
  (`WorldBody.Contacts.cs:83`): one sweep to test, eight bisection sweeps
  (`FixedStepUp.LandingProbes`, `FixedStepUp.cs:41`) and a landing sweep, each
  a full `ResolveSweep`.
- **Rigid bodies** (chess pieces, Go stones, reversi discs, alchemy tokens)
  are solved together by one `FixedRigidWorld` soft-step solve a tick
  (`WorldPopulation.SolveRigidContacts`, `WorldPopulation.Rigid.cs`), from
  manifolds each body owns and carries in its checkpoint entry. Each body's
  step is prepared in `WorldBody.PrepareRigidStep` (damping, gravity as the
  step's acceleration, the gyroscopic step); its surface contacts come from one
  exact query per collider within reach (`IContactField.CollectContacts`), or
  one certified sweep where the field cannot answer per surface; its body
  contacts from one overlap query per broadphase pair in
  `ResolveDynamicContacts`. `FixedRigidContacts` spreads each contact over the
  shapes' support features, and a guard sweep re-certifies a body whose solved
  motion left its predicted motion.
- **Sleep** parks a whole resting contact component
  (`WorldPopulation.ParkRigidIslands`, `WorldPopulation.Sleep.cs`). The island
  graph is the bodies' persistent contacts: an occupied rigid contact joins two
  islands and a contact with a kit body is a wake dependency, so nothing is
  rebuilt per tick. Parked bodies keep a retained index (`SleepingIndex`) that
  both the pair sweep and a bounded contact-field change query, and they observe
  the field through the population's own observation.

### The measurements

From `puck bench world` on the debug world (the lattice-wake fix and its
quiet-window measurement record them; deterministic counts, 120 sampled ticks
after 60 of warmup):

| Reading | Regression head | After the lattice-wake fix |
|---|---|---|
| Sleeping rigid bodies | 0 | 488 |
| Body advances a tick | 557 | 69.1 |
| Exact contact queries a tick (`physics.contact.queries`) | 41,341 | 4,029.8 |
| Idle tick (quiet window) | 96.5 to 102 ms | 5.8 to 6.3 ms |

A temporary per-body probe in the lattice-wake fix split the 41,341: Go stones
30,324, reversi discs 4,224, alchemy tokens 2,130, chess pieces 640, and the
always-awake bodies of the 4,030 baseline: the range walkers 2,925 (six Moths,
about 490 queries each a tick) and the fuzzer and traversal stand-ins 540
each. At the regression head a sampled trace put 76 % of the tick in the
analytic static sweep (`FixedStaticContactSolver.Sweep` into
`FixedConvexContact`), about 73 ms over 41,341 queries, or about 1.8 µs a
query including the sweep's own overhead. The same unit cost over today's
4,030 queries would be about 7 ms, more than the whole 5.9 ms tick, so a
walker's query costs less than a rigid piece's GJK query did. Brief A's profile
of the idle tick (on a loaded machine) put all contact work at 0.31 ms a tick,
17 % of the tick's managed CPU, with walker queries near 0.08 µs each; the
[brief](#brief-a-kit-body-contact-work-certify-cheaply-query-exactly-only-what-a-certificate-cannot-settle)
records what it found and what remains.

Two order-dependent accumulations were fixed just before this round: glass
optical depth now sums integer-quantized terms with integer atomics
(its commit: "every glass sum word is a function of the multiset of faces,
not their order"), and the evaluator's certificates had overflow edges closed.
The FMM gravity solver already forms exact `Int128` mass moments
([physics reference](../reference/physics.md#determinism-and-workspace-reuse)).

## The sources and where they disagree

**Box3D.** Public under the MIT licence at
`erincatto/box3d` (the announcement names no licence; the README does). "The
core architecture of Box3D remains almost identical to Box2D", with some
convex hull and collision code from Rubikon-Lite, and Catto calls it "alpha
software". Its README lists the Soft Step solver, island-based sleep,
continuous collision, "Extensive multithreading and SIMD", "Cross platform
determinism" and "Recording and replay". Its solver source (`solver.c`) was opened and
carries the Box2D structure: graph-colour overflow, the sleepiest split
candidate with island-id tie-breaking "for determinism", dynamic-versus-static
continuous collision, and a Newton-Raphson gyroscopic step. The Box2D posts
since its announcement (SIMD for collision, inscribed spheres, contact recycling) are
written about Box3D or both engines.

**Box2D v3.** The posts on simulation islands, Solver2D, SIMD, determinism,
replay, contact recycling and the 3.0 and 3.2 releases, and the source files
`constraint_graph.c`/`.h`, `solver_set.c`, `island.c`, `solver.c`,
`broad_phase.c` and `include/box2d/constants.h`.

**Independent physics sources.** Jolt's documentation (determinism, islands,
the large island splitter, double precision), Rapier's determinism guide,
Bullet's `btRigidBody.h` (gyroscopic flags), and Catto's GDC talk on
numerical methods (the gyroscopic derivation).

**OpenBLAS and the BLAS lineage.** The OpenBLAS FAQ and developer guide and
its `driver/others/blas_server.c`; Goto and van de Geijn, "Anatomy of
High-Performance Matrix Multiplication"; Van Zee and van de Geijn, "BLIS: A
Framework for Rapidly Instantiating BLAS Functionality"; BLIS's kernel guide;
and Intel's oneMKL Conditional Numerical Reproducibility pages as a second
vendor's answer to reproducibility. The ReproBLAS pages (Demmel and Nguyen's
reproducible summation) refused a TLS connection and are not cited.

Where they disagree:

- **Island splitting.** The islands post splits with depth-first search
  and names "the largest island with removed constraints" as the heuristic.
  The current `island.c` splits with union-find ("This uses union-find."), and
  `solver.c` picks the sleepiest candidate ("Track the sleepiest candidate",
  "Largest island id wins. Needed due to work stealing."). The source is the
  current truth; the post is history.
- **The cost of float determinism.** Catto reports no noticeable cost for
  Box2D's cross-platform determinism (no FMA contraction, custom `sinf`,
  `cosf` and `atan2f`). Jolt says its cross-platform mode is "approximately 8%
  slower". Rapier's `enhanced-determinism` cannot be combined with `simd8`,
  which "forms a separate determinism domain". Intel's CNR mode costs 10 to 20
  % and holds across thread counts only in a strict mode limited to the GEMM
  family on AVX2 or AVX-512 ("even when the number of threads changes").
- **Thread-count independence.** Box2D (bit arrays merged with OR) and Rapier
  ("the results of the parallel solver are identical to the results of the
  sequential one") claim results independent of worker count. OpenBLAS
  promises nothing: its FAQ attributes varying test errors to "changing the
  order of mathematical operations by splitting an original problem workload"
  and to FMA. oneMKL needs a fixed thread count outside strict mode.
- **OpenBLAS idle threads.** The FAQ gives the default
  `OPENBLAS_THREAD_TIMEOUT` as 26; `blas_server.c` defines `THREAD_TIMEOUT 28`
  (a spin of `1 << 28` timestamp-counter ticks before `pthread_cond_wait`).
- **Large worlds.** The Box3D announcement and the Box2D 3.2 post say "doubles
  for position"; Box3D's `solver.c` also re-centres each continuous sweep on a
  world base "to keep TOI in float precision". Jolt documents a 5 to 10 %
  cost for doubles. Puck's `FixedPosition` (cell plus local offset) and Q48.16
  already cover this; it is not a lesson.
- **Contact recycling's adoption.** Catto reports that Avian and Rapier
  adopted recycling. Rapier's published documentation did not show it, so the
  claim is unconfirmed here. Catto's post gives no benchmark numbers for
  recycling alone; the 3.2 post attributes about 40 % of 3.2's speed-up to it.

## The lessons, ranked

Each lesson names the general mechanism it is an instance of, so the work
builds the mechanism once rather than a fix per site.

| Rank | Lesson | Mechanism | Applies | Gain against the counters | Determinism | Cost |
|---|---|---|---|---|---|---|
| 1 | Cut the kit bodies' contact work | Certify cheaply; query exactly only what a certificate cannot settle | Directly (cull); inspires (snap) | The only measured idle physics work: 4,030 queries a tick | A conservative cull changes no result: hashes stay identical | Small to medium |
| 2 | One rigid solver: Soft Step | Hoist geometry out of the iteration loop; iterate on cached constraints | Directly: Puck already wrote it | None at idle; large in an active rigid scene, plus stacking accuracy | Fixed point already; hashes move once | Large |
| 3 | The gyroscopic term | Implicit integration of a quadratic term in the frame where its operator is constant | Directly | Accuracy for 457 thin rigid pieces in flight | One Newton step, exact integers | Small |
| 4 | Wake and islands proportional to change | A spatial index over the parked set; persistent islands | Directly (filed) | 488 sleeper visits each lattice step, about 61 a tick; allocation in active scenes | Order fixed by slot index | Small |
| 5 | Order-free merges and exact reductions for the parallel tick | Combine with an associative, commutative operation and a total tie-break | Directly (a contract) | Enables the job system's later phases without hash drift | The point of it | Small |
| 6 | Packed hot data and integer SIMD kernels with runtime dispatch | Pack once, stream many; one architecture-specific micro-kernel | Inspires | Unmeasured; follows brief A's profile | Integer lanes are exact at every width | Medium |
| 7 | Graph colouring for a parallel rigid solve | Partition constraints so no body appears twice in a batch | Inspires; after lesson 2 | Active scenes only | Colouring computed serially in a fixed order | Medium |
| 8 | Contact recycling across steps | Reuse a cached linearization while relative motion stays under a tolerance | Inspires; after lesson 2 | About 40 % of Box2D 3.2's gain; Puck's share unmeasured | The cache becomes continuation state | Medium |
| 9 | Sleep on the farthest point's speed | Bound a body's motion by its fastest surface point | Directly | Accuracy of the rest latch for thin pieces | None beyond a hash move | Small |

### 1. Cut the kit bodies' contact work

**What the sources do.** Box3D's inscribed-sphere post bounds a polytope
pair's separation by `n·d − r` with the hulls' inscribed spheres before the
exact face and edge tests, culling 86 % of face tests and 98 % of edge-pair
tests on its convex pile and cutting collide time 62 %; "This technique gives
the global maximum separation value. It is not approximate and cannot get
stuck in a local maximum." Box2D's speculative contact
(`B2_SPECULATIVE_DISTANCE`, "Box2D uses limited speculative collision. This
reduces jitter.") makes a contact that is near but not touching a constraint
the solver lands on exactly, rather than a search. Box2D fattens proxy boxes
by a small margin and found that "keeping the margin small improves
performance by reducing the number of contacts" (`B2_MAX_AABB_MARGIN`). Goto
and van de Geijn's packing argument (pack the operand "in a contiguous work
array" sized to cache and TLB) applies to the bounds pass that runs before
every exact query.

**What Puck does.** Every relaxation pass re-runs the exact query for every
collider that survives the world-axis box test
(`FixedStaticContactSolver.cs:143-161`). A rotated or large collider's
world-axis box can be far larger than the collider, so the exact query runs
where a tighter conservative bound would have said "no push". The ground snap
spends ten full sweeps to find a support distance that one distance query
returns: `FixedConvexContact` already has a nearest-point query, and the
analytic kinds have closed-form distances. The bound check reads
`FixedStaticCollider.Center` from the full collider record and the extent from
a `FixedVector3?[]`, not from a packed bounds array.

**Expected gain.** Almost all of the 4,030 queries a tick are kit bodies'; brief A
reads `physics.contact.resolves`, `.queries`, `.convex-queries` and
`.supports` per tick (already rows of `puck bench world`,
`src/Puck.Cli.Bench/WorldBenchmarks.cs:508-513`) to split them into sweeps,
passes and candidates before choosing a cut. If the 1.8 µs estimate is near
the walkers' cost, the queries are most of the idle tick and a cull that
removes half of them is worth about a quarter of it.

**Determinism.** A conservative cull skips only colliders whose exact query
would return no push, and `ResolveSweep` already states that contract
("Skipping a collider these bounds rule out never changes the result",
`FixedStaticContactSolver.cs` on `colliderExtents`). Puck's bounds are exact
integer comparisons, so the cull needs no round-off margin, where Box3D
shrinks its radius "slightly for round-off safety". State hashes must not move
at all: the strongest possible check. Replacing the snap's bisection with a
distance query does move results (the landing lands at the exact support
instead of within the bisection's resolution) and is a deliberate correction
with a re-record.

**Cost.** A tighter bound per collider kind (the oriented box Puck already
owns in `FixedOrientedBox`, a sphere for rounded and superellipsoid kinds) at
compile time, one packed bounds array, and a cull ahead of `TryGetPush`.

### 2. One rigid solver: Soft Step

**What the sources do.** Box2D v3 chose Soft Step: sub-stepping instead of
iterations, soft constraints as a damped spring tuned in hertz, warm
starting, and a relax pass that solves again without bias or softness to remove
the energy the soft constraints added (Solver2D post).
The contact points are computed once per step and updated across substeps from
anchors held relative to the bodies, so neither the broadphase nor the contact
points are recomputed inside the solver. Its stages, from `solver.c`:
prepare, integrate velocities, warm start, solve, integrate positions, relax,
restitution, store impulses. Catto: "Relaxation is expensive, but it improves
the simulation quality dramatically." Box3D keeps the same solver.

**What Puck does.** Landed (brief B): `FixedRigidWorld` is the one rigid
contact solver, the static field and body pairs alike, wired into the tick
(`WorldPopulation.SolveRigidContacts`); `FixedRigidSolver` and the World's
per-substep sweep and pair passes are deleted. The manifold slots and
accumulated impulses ride each body's checkpoint entry, so the continuation
hash, the checkpoint and a scoped replay carry the warm start. A manifold's
points are solved together rather than in visiting order, which a single-pass
Gauss-Seidel sweep needs to keep a tall piece that lands flat from spinning on
its first corner.

**Expected gain.** Nothing at idle: every rigid piece sleeps. In an active
scene (a Go board knocked, a chess capture, a reversi flip, the regression's
557 advances) the exact query count per awake rigid body falls from
substeps × passes × candidates to candidates once a step, and the pair work
from passes × (broadphase + narrowphase) to one broadphase and a fixed solver
budget over cached constraints. Stacking and resting stability improve with
warm starting and multi-point manifolds; the debug world's
`physics(quiescent)` gates (the chess demo) depend on bodies settling.

**Determinism.** Everything is already fixed point, ordered by canonical keys
with no hash container (`FixedRigidWorld`'s remarks). What does not transfer
from Box2D is its float discipline, which Puck does not need. What Puck must
add is that the manifold slots and accumulated impulses are continuation
state: they ride the checkpoint and the authority hash, as Box2D's replay post
found when its snapshots had to copy the engine's internal data structures to make
rollback possible. Hashes move once.

**Cost.** Large: candidate generation from Puck's colliders into
`FixedContactCandidate` and `FixedTwoBodyContact`, the World's features the
live path carries (sweep refusal, the continuous-collision substep bound,
support blocking, kinematic carriers, rolling resistance, sleep islands), the
pair budget, and deletion of the old path.

### 3. The gyroscopic term

**What the sources do.** Euler's equation `Iω̇ + ω × Iω = T`: the gyroscopic
term "accounts for non-spherical shapes that may wobble as they spin freely in
the air" (Catto's GDC talk). Explicit integration "blows up" because the term
is quadratic in ω; dropping it, as Catto did "for many years", makes "a slender
box that spins a bit too much" and a capsule that "spins forever". His fix
splits the step: the applied torques explicitly, the gyroscopic term
implicitly, solved in body coordinates where the inertia is constant, with one
Newton-Raphson step: residual `f = h ω_b × I_b ω_b`, Jacobian
`J = I_b + h (skew(ω_b) I_b − skew(I_b ω_b))`, `ω_b ← ω_b − J⁻¹ f`. Bullet
made the body-frame implicit form its default from 2.83
(`BT_ENABLE_GYROSCOPIC_FORCE_IMPLICIT_BODY`; "explicit version is best avoided,
it gains energy"). Box3D's solver carries the same Newton step, and the
announcement names the missing term in Unreal's Chaos as one reason Box3D
exists.

**What Puck does.** Landed: `FixedGyroscopicStep.Advance` (Puck.Physics) is
this Newton step, solved by `FixedLinearSolve.TrySolve3`, called once per
rigid step from `WorldBody.PrepareRigidStep` before contact, the one
solve's integrate stage. Its law
(`tests/Puck.Physics.Tests/GyroscopicStepLawTests.cs`) measured what the
memo had expected to be conservation "within the fixed-point budget": at
60 Hz implicit Euler's own truncation is first order and dissipative, and the
Newton step alone lowers momentum magnitude and energy on every step. So the
step then scales the corrected ω by one factor `s ≥ 1`, the smaller of
`|L₀|/|L₁|` and `√(E₀/E₁)`, both free-flight invariants: one is restored
exactly, the other stays at or below its value before the step, so energy
never rises and the scheme keeps its stability. Over 300 steps from about
4 rad/s, a disc's world-momentum drift falls from 2.1 % to 0.8 % and its
magnitude drift from 2.1 % to 0.08 % (the momentum factor binds); a slender
triaxial block's falls from 13.9 % to 8.1 %, its magnitude drift from 13.1 %
to 6.5 % (the energy factor binds). With world ω held constant the drifts are
44 % and 88 %. What drift remains is the scheme's error in the momentum's
direction, and the fixed-point run tracks the exact-arithmetic scheme to
rounding.

### 4. Wake and islands proportional to change

**What the sources do.** Box2D keeps islands persistent: a contact that
begins touching links two islands by union-find, removal only flags the island
(`constraintRemoveCount`), and splitting is deferred to one island a step,
chosen when a body "wants to sleep". Measured: "Persistent islands are roughly
an order of magnitude faster than DFS" (pyramids 0.69 ms to 0.01 ms). Sleeping
islands move to their own solver set and leave the constraint graph
(`solver_set.c`, `b2TrySleepIsland`). "Islands are not required for
correctness. They are an optimization." "Sleeping must be done per island, not
per body." Jolt documents the same per-island rest rule.

**What Puck does.** Landed (brief B): the island graph is the bodies'
persistent contact manifolds, with no per-tick rebuild or allocation, and a
bounded field change takes its candidates from `SleepingIndex` over the change
box; the parked bodies observe the field through the population's
`m_sleepFieldVersion`, so a change that reaches no sleeper visits none (the
debug world's `physics.bodies.field-observed` fell from 61 a tick to 0).

**Expected gain.** The wake: 488 box tests and 488 per-sleeper writes each
lattice step (61 `physics.bodies.field-observed` a tick) fall to the sleepers
near the change box, usually none. Small in milliseconds; it removes a cost
that scales with the parked population. Persistent islands remove a per-tick
rebuild and allocation in active rigid scenes; unmeasured.

**Determinism.** The index's answers are re-sorted into slot order before any
wake (the existing pair sweep does this), so the wake order is unchanged.

**Cost.** Small; the wake half is filed in the
[refactoring packet](open-items.md#refactoring-campaign) and is a Codex-sized
lane.

### 5. Order-free merges and exact reductions for the parallel tick

**What the sources do.** Box2D: "The atomic increment does not prevent
randomness, it just prevents a data race", so workers fill local bit arrays
that the main thread merges with OR, "a deterministic sequence of set bits",
and a cross-worker choice breaks ties by island id. OpenBLAS and oneMKL show
the cost of not designing for this: results that move with the thread count
and the kernel chosen, and a reproducibility mode that costs 10 to 20 % and
holds across thread counts for only a few routines.

**What Puck does.** The [job-system memo](engine-job-system.md#the-contract)
merges partition logs by a commit key, which is sound. It keeps the rigid
pair contacts serial (`engine-job-system.md`, "Serial phases stay serial").

**What transfers.** Integer sums of terms each rounded once are associative,
so a parallel fixed-point sum is reproducible for free, where a float engine
pays for it. A rounded product is not (maths-usage: "Do not reassociate a
rounded product"), so the rule is: round each term once onto a common grid,
then combine exactly (`Int128` where the range needs it). That is the glass
fix and the FMM's moments, stated once. A set of bodies a partitioned phase
marks (woken, enlarged, wanting to sleep) merges as a bit set by OR and is
read in index order, with no ordered log needed. A choice across partitions
takes a total key.

**Cost.** Small: the contract text in the job-system memo and, when a second
phase needs it, one exact-reduction helper in `Puck.Maths` rather than a copy
per phase.

### 6. Packed hot data and integer SIMD kernels with runtime dispatch

**What the sources do.** GotoBLAS packs a block of A "in a contiguous work
array" so that it is addressable by the TLB and streams from L2, and the
packing "needs not to create a substantial overhead". BLIS reduces the
architecture-specific code to a micro-kernel ("By implementing and optimizing
the `gemm` microkernel, all level-3 operations except `trsm` are fully
optimized") with generic loops around it. OpenBLAS's `DYNAMIC_ARCH` selects
kernels at run time by CPU, overridable with `OPENBLAS_CORETYPE`. Box2D 3.2
credits AVX2 runtime dispatch with about 15 % of its speed-up, and Box3D's
SIMD edge tests run 2.2 to 2.4× faster than scalar, with the data in
"structure of arrays format".

**What Puck does.** `SleepingIndex` already packs parked bounds so that "no
sleeping body's object is read on an idle tick". The pair sweep, the wake test
and the static bounds pass read positions and radii through `WorldBody` and
`FixedStaticCollider` objects.

**What transfers, and what Puck adds.** Bounds tests are 64-bit integer
compares, which `Vector256<long>` does four at a time and gives the same bits
as the scalar loop at every width. So unlike Rapier (where `simd8` is its own
determinism domain) or Jolt (8 % for cross-platform mode), a Puck integer
kernel can dispatch by CPU with no determinism cost, and the scalar path stays
as the oracle, forced on with the runtime's ISA-disable settings, to compare
hashes. Q48.16 products need a 64×64→128 multiply that AVX2 lacks, so the
kernels worth vectorising are the culls and bounds, not the contact
arithmetic. As with PDEP and PEXT
([maths-usage](../../.claude/skills/maths-usage/SKILL.md)), dispatch reads a
capability gate, never a bare `IsSupported`.

**Cost.** Medium; scheduled from brief A's profile, not before it.

### 7. Graph colouring for a parallel rigid solve

**What the sources do.** Box2D colours contact constraints greedily into at
most `B2_GRAPH_COLOR_COUNT` (24) colours; "The constraint is assigned to the
first color with a bitset that doesn't have either body bit set to 1", the
colouring persists across steps, and "static bodies are never set in the
bitsets because static bodies are not modified by the contact solver".
Constraints that find no colour go to an overflow set solved single-threaded,
and dynamic-against-dynamic constraints keep a lower priority than
dynamic-against-static ones, which "reduces tunneling due to push through"
(`constraint_graph.h`). Jolt's large island splitter is the same idea ("Within
a group, the order of execution does not matter since every memory location is
only read/written once").

**For Puck.** After lesson 2, a colour's constraints write distinct bodies, so
a colour can run in partitions with exact integer results at any thread count;
the colouring itself is computed serially in slot order. It changes the solve
order from today's sweep order once. Worth building only when an active rigid
scene shows in the frame; the job-system memo rightly keeps the pair pass
serial until then.

### 8. Contact recycling across steps

**What the sources do.** Box2D 3.2 and Box3D cache a pair's one-shot manifold
with the relative transform at which it was computed, and reuse it while the
relative movement (linear plus angular times extent) stays under a tolerance,
updating each point's separation from the rotations: "Suddenly I can use very
large tolerances, up to several centimeters." "A word of caution: do not
update the cached relative transform when recycling. This causes drift."
Box2D's defaults are `B2_CONTACT_RECYCLE_DISTANCE` and
`B2_CONTACT_RECYCLE_COS_ANGLE` ("0.98 ~= 11.5 degrees").

**For Puck.** Recycling is approximate, so the cached transform and manifold
become continuation state (checkpointed and hashed), and the tolerance is an
authored contact parameter with a neutral default, not an engine constant
(AGENTS.md, "Authors rule"). It belongs inside the one solver of lesson 2 and
is measured there; applied to the kit-body controller it would change step-up
and edge rules, so it is not proposed there.

### 9. Sleep on the farthest point's speed

Box2D's sleep test uses "the velocity of the farthest point on the body to
account for rotation" and folds in position correction ("Sleep needs to
observe position correction as well as true velocity", `solver.c`). Landed:
Puck's rest latch and its substep bound both read `WorldBody.RigidSurfaceSpeed`
(linear speed plus angular speed times the bounding radius) against one
authored threshold, `collision.bodyContacts.rigidRestSpeed`. Folding in
position correction is not done.

## What does not transfer

- **Float determinism discipline** (no FMA contraction, custom trigonometry,
  one SIMD width): Puck's fixed point has none of these hazards. It pays
  instead in wide multiplies, which is why lesson 6 vectorises compares and
  not products.
- **Doubles for position:** `FixedPosition` and Q48.16 already exceed it.
- **Speculative-plus-time-of-impact continuous collision for kit bodies:**
  Box2D's approach "seems to prevent almost all dynamic versus static
  tunneling"; Puck's certified sweep proves it for every core
  ([physics reference](../reference/physics.md#contact-and-rigid-body-kernels)),
  a stronger contract, and stays.
- **OpenBLAS's threading:** idle workers spin for `1 << 28` timestamp ticks
  before sleeping, and its FAQ warns that an already threaded application "will
  conflict with OpenBLAS multi-threading". AGENTS.md rules spinning out
  ("Puck plays nice with its host"), and `JobPool` already idles on an
  operating-system semaphore ([job-system memo](engine-job-system.md)). It is
  an anti-lesson.
- **A parallel union-find over islands:** Box2D tried Jolt's and rejected it,
  because its atomic compare-and-swap merging made the constraint order
  nondeterministic (islands post); Puck's serial island bookkeeping is the
  right shape.

## Brief A: kit-body contact work: certify cheaply, query exactly only what a certificate cannot settle

**Mechanism.** A conservative certificate cheaper than the exact query,
tested first, so the exact work runs only where the certificate cannot rule a
push out. It is the same mechanism as the field sweep's bounds queries, Box3D's
inscribed spheres and Box2D's fat-box margin.

**Invariant.** A collider the certificate skips would have returned no push.
**Enforcement:** one door, `FixedStaticColliderSet`: the solver takes colliders
only as sets, static and per-tick alike, and a set compiles each collider's
`FixedColliderBox` and `FixedColliderFrame` from the collider itself. **Where it
stops:** the certificates say nothing about the colliders they keep, which take
the exact query, and the snap change below is outside the invariant, a
deliberate correction.

**Where it stands.** Steps 0 to 3 closed (their evidence is in the commits). The
idle queries' cause was not walkers against the static field: every per-tick
(attached) collider, the gallery lift's car, was queried by every sweep in the
world, unculled. With it culled, `physics.contact.queries` fell from 4,030 to
766 a tick, every state hash unchanged. Carrying separation from pass to pass
(step 3) skipped 16 queries a tick and was not landed. 160 of the 170 resolves a
tick are still ground-snap sweeps: `FixedStepUp.TrySnap` runs twice per walking
body per tick (the support search in `IntegratePlanarAndVerticalVelocity` and
the crest snap in `ResolveProgramContacts`), ten sweeps each.

**The remaining step.** Replace the snap's bisection with one support-distance
query (the convex nearest-point query or the analytic distance) followed by the
landing sweep, keeping `TrySnap`'s acceptance rules, and let the two snaps of one
tick share it where the body has not moved between them. **Check:**
`physics.contact.resolves` a tick falls toward the moves' own count; the range
walkers still follow the ground on the range's slopes (a headless `body.where`
trace over their loop); hashes move once and the persisted replays it
invalidates are re-recorded in the same change. It wants a lane that loads
`sdf-authoring` for contact admission.

## Brief B: one rigid solver: Soft Step

**Mechanism.** Hoist geometry out of the iteration loop: compute each
contact's manifold once a step, then run substeps and solver passes on cached
constraints whose separations are updated from anchor displacement. One
implementation of rigid contact for the World and `Puck.Physics`.

**Invariant.** Every rigid contact, static or between bodies, is solved by
`FixedRigidWorld`'s substep sequence, and a step runs the contact geometry once
per candidate pair. **Enforcement:** the old path is deleted
(`ResolveRigidPairContact`, the pair-pass loop, `AdvanceRigid`'s per-substep
sweep, `FixedRigidSolver`), and the only caller of `FixedRigidWorld.Step` in the
World is `WorldPopulation.SolveRigidContacts`, which every awake rigid body's
prepared step reaches. **Where it stops:** kit bodies keep
`FixedStaticContactSolver` and the certified sweep; a carried body's tangibility
push is still positional (open items); a field collider within reach makes a
body's surface contacts come from one sweep rather than one query per surface
(open items).

**Where it stands.** Steps 0 to 4 closed; their evidence is in the commits. The
one remaining measure: on the Go knock scene, exact queries per awake stone a
tick fell only where surfaces are analytic, because the game tables carry
relief and take the sweep (open items, "A rigid body near a field collider").

## Open questions and who settles them

- **Whether lesson 6 pays.** After brief A the contact work of the idle tick is
  about 0.2 ms; lesson 6 is not scheduled until a quiet-window profile shows a
  bounds pass worth vectorising.
- **Whether contact recycling pays in fixed point.** Settled inside the one
  solver by measurement on brief B's Go knock scene; lesson 8 is not scheduled
  until that measurement runs.
- **Whether a parallel rigid solve pays.** The job-system memo's T2 measures
  the rigid share of an active tick; lesson 7 waits on that reading.

## Sources

Every page below was opened for this memo.

Box3D and Box2D:
- Catto, "Announcing Box3D", https://box2d.org/posts/2026/06/announcing-box3d/
- Box3D repository and README, https://github.com/erincatto/box3d
- Box3D solver source, https://raw.githubusercontent.com/erincatto/box3d/main/src/solver.c
- Catto, "Contact Recycling", https://box2d.org/posts/2026/10/contact-recycling/
- Catto, "Inscribed Spheres", https://box2d.org/posts/2026/09/inscribed-spheres/
- Catto, "SIMD for Collision", https://box2d.org/posts/2026/07/simd-for-collision/
- Catto, "Replay", https://box2d.org/posts/2026/06/replay/
- Catto, "Box2D 3.2", https://box2d.org/posts/2026/10/box2d-3.2/
- Catto, "Dynamic Tree Improvements", https://box2d.org/posts/2025/03/dynamic-tree-improvements/
- Catto, "Determinism", https://box2d.org/posts/2024/08/determinism/
- Catto, "SIMD Matters", https://box2d.org/posts/2024/08/simd-matters/
- Catto, "Releasing Box2D 3.0", https://box2d.org/posts/2024/08/releasing-box2d-3.0/
- Catto, "Solver2D", https://box2d.org/posts/2024/02/solver2d/
- Catto, "Simulation Islands", https://box2d.org/posts/2023/10/simulation-islands/
- Box2D source, the files opened:
  - https://raw.githubusercontent.com/erincatto/box2d/main/src/constraint_graph.c
  - https://raw.githubusercontent.com/erincatto/box2d/main/src/constraint_graph.h
  - https://raw.githubusercontent.com/erincatto/box2d/main/src/solver_set.c
  - https://raw.githubusercontent.com/erincatto/box2d/main/src/island.c
  - https://raw.githubusercontent.com/erincatto/box2d/main/src/solver.c
  - https://raw.githubusercontent.com/erincatto/box2d/main/src/broad_phase.c
  - https://raw.githubusercontent.com/erincatto/box2d/main/src/body.c
  - https://raw.githubusercontent.com/erincatto/box2d/main/include/box2d/constants.h
- Catto, "Numerical Methods", GDC 2015, https://box2d.org/files/ErinCatto_NumericalMethods_GDC2015.pdf

Independent physics sources:
- Jolt Physics documentation, https://jrouwe.github.io/JoltPhysics/index.html
- Rapier, "Determinism", https://rapier.rs/docs/user_guides/rust/determinism
- Bullet, `btRigidBody.h`, https://github.com/bulletphysics/bullet3/blob/master/src/BulletDynamics/Dynamics/btRigidBody.h

OpenBLAS and the BLAS lineage:
- OpenBLAS FAQ, http://www.openmathlib.org/OpenBLAS/docs/faq/
- OpenBLAS developer guide, http://www.openmathlib.org/OpenBLAS/docs/developers/
- OpenBLAS `driver/others/blas_server.c`, https://github.com/OpenMathLib/OpenBLAS/blob/develop/driver/others/blas_server.c
- Goto and van de Geijn, "Anatomy of High-Performance Matrix Multiplication",
  ACM TOMS, https://www.cs.utexas.edu/~flame/pubs/GotoTOMS_final.pdf
- Van Zee and van de Geijn, "BLIS: A Framework for Rapidly Instantiating BLAS
  Functionality", ACM TOMS 2015, https://www.cs.utexas.edu/~flame/pubs/BLISTOMSrev2.pdf
- BLIS, "Kernels HowTo", https://github.com/flame/blis/blob/master/docs/KernelsHowTo.md
- Intel oneMKL, "Reproducibility Conditions", https://www.intel.com/content/www/us/en/docs/onemkl/developer-guide-linux/2025-1/reproducibility-conditions.html
