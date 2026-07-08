# The SDF accumulator — findings, and the scoped-accumulator decision

`mapCore` (`src/Puck.SdfVm/Assets/Shaders/Sdf/sdf-vm.hlsli`) carries **one**
running nearest-surface distance for the whole program. `SDF_OP_RESET`
(`ResetPoint`) resets the evaluation **point** — `localPosition`,
`distanceScale`, `parityMaterialDelta` — and never `result.distance`. So every
op that *reads* the accumulator composes against the entire scene emitted before
it, not against the shape you meant.

The settled contract is stated where authors meet it: `SdfBlendOp`'s summary,
the `SDF_BLEND_*` block in `sdf-vm.hlsli`, the `sdf-world` skill, and
[capability-catalog.md](capability-catalog.md)'s VM row. **This doc is the
evidence and the open decision**, not the contract — don't duplicate the
contract here.

## Which ops read the accumulator

| Op | What it does to the accumulator |
|---|---|
| `Intersection` / `SmoothIntersection` / `ChamferIntersection` | `max(acc, candidate)` — returns the **candidate** everywhere outside its own shape |
| `Xor` | `max(min(acc, b), -max(acc, b))` — xors against the union of everything so far |
| `Onion` | `abs(acc) - t` |
| `Dilate` | `acc - r` |
| `Displace` | `acc + amplitude·sin-product` |

`DomainWarp` is **not** in this set — it is a POINT op and never reads the
accumulator. Union and the subtraction family are *local*: a far candidate
returns the accumulator (`min(a,b) = a`, `max(a,-b) = a`, `blendSmoothUnion` is
far-exact by construction, and the chamfer union/subtraction bevel planes fall
away), so they may be emitted anywhere.

## Why it hid for so long — severity is a property of the op

- **Intersection is loud.** It annihilates every earlier shape it does not
  overlap, the ground plane included. Obvious once you look.
- **The field ops are silent.** `abs(d) - t = 0` ⟹ `d = ±t`, so the **outer**
  surface moves *outward* by `t`. Every earlier solid simply grows by `t` and
  goes hollow. That reads as "a slightly larger object."

That asymmetry is the whole story. `world-chamfer` was caught by a **2-pixel**
cross-backend diff — an order of magnitude below every other world stage.
`world-warp` was never caught at all.

## Where it bit (all measured, all fixed)

| Site | Symptom | Evidence |
|---|---|---|
| Post `world-chamfer` | Rendered a lone violet wedge on empty sky while its doc claimed a three-cluster scene: the trailing `ChamferIntersection` deleted the ground plane, the copper weld and the jade trench | Ground-plane coverage 100% → 0% on a CPU replica of `mapCore` |
| Post `world-warp` | `.Sphere(0.65).Onion(0.06)`, commented *"shell first, then carve the shell"*, shelled the **scene**: the plane's top rose to `y = +0.06` and became a 0.12-thick slab; the twisted column, the bent capsule and the elongated bar grew 0.06 fatter and hollow | CPU replica; before/after captures |
| Creator mode | Every placed shape emits `Onion` inside `BeginInstanceDynamic`. An `Onion(0.05)` over a ground plane turns the ground into a 0.05-thick shell | Solid fraction of a scene slice 51.3% → **6.3%** |
| World sculptor (`WorldSceneRenderer`) | `.Translate(c).Onion(0.03).Box(...)` — the onion could never reach *forward* to the plate it preceded. It shelled the terrain and lamps emitted before it, and **nested** with every earlier ghost's onion, burying the very plates it preceded | With two overrides: terrain slice 83% → **15%** solid; terrain top rose 0.06 vs a 0.02-thick plate |
| Run document (`SceneObject.Emit`) | Object *N*'s `dilate`/`onion` inflates and hollows objects `0..N-1`. `docs/examples/world-warp.json` had a `dilate` on object 3 | Schema fields that only work on object zero |
| Instance culling | An instance carrying **any** accumulator-reading op cannot be masked out of a tile: the far-field answer is not the accumulator, so no bound inflation closes the gap | See below |

## Where it is safe, and why

**The forge.** `CreationBakePlanner.BuildProgram` constructs a fresh
`SdfProgramBuilder` **per bake view containing only the creation's shapes** — no
ground plane, no other objects — and `AvatarDefinition` emits no field ops. So
"everything accumulated so far" *is* the object. The forge is correct by
construction, not by care.

That is the general principle: **the failure is a function of what else is in
the accumulator, not of who is authoring.** A single-object program is safe. The
moment a program has a floor, "everything so far" stops meaning "this object."

## Instance culling — the unmaskable consequence

An instance is maskable only if skipping it is bit-identical to evaluating it,
which requires the far-field answer to be the accumulator. `SdfProgram` now
packs any instance carrying an accumulator-reading op with
`UnmaskableBoundRadius` (`HasUnmaskableCompose`), and throws if such an instance
is *parked* (a parked slot asserts "contributes nothing", which such an op
violates wherever its own geometry is absent).

Gated by `world-instanced`'s two guard scenes. Both were verified to **fail**
with the corresponding half of the predicate disabled:

| Guard | Bound authored | Differing bytes without the fix |
|---|---|---|
| Intersection | deliberately under-covering (0.15 vs a 0.6 sphere) | 14,372 |
| Onion | **honest** (0.7 vs a 0.6 sphere) | **318,422** |

The asymmetry is instructive. A merely tight-but-covering bound *hides* the
intersection bug: the beam prepass cone-marches the **unmasked** field, an
intersection annihilates everything outside its own shape, so every tile the
cone march leaves non-empty is a tile whose cone passes through the shape — and
therefore through any bound containing it. **The intersection bug hides behind
the cull that precedes it.** Nothing hides the onion. That is why no finite
bound is correct for an intersection member, and why the guard scene authors a
deliberately wrong one: the packer must override whatever was authored.

## Landed

| Commit | What |
|---|---|
| `b6d51e3` | Seven render bugs; corrected the Lipschitz warp norms; ~13% off the interpreter. Includes `blendSmoothUnion`'s **near-endpoint select** — a prerequisite for everything below |
| `d18d238` | Intersection has global reach → unmaskable instance; `world-chamfer`'s scene reordered |
| `05d500b` | The FIELD ops are unmaskable too, and are the *visible* half |
| `449dc9b` | A `ChamferUnion` instance's cull halo was `0.71` radii short of far-neutral |
| `9fe3a6d` | `world-warp`, the walk-override ghosts, and the run-document docs |

`Puck.Post` 48/48 throughout.

### The chamfer halo, for the record

`PackInstances` inflates a soft-blended instance's bound by its blend radius so
a masked-out tile's skip stays exact. That is right for three of four soft
families and wrong for the fourth. `ChamferUnion` is
`min(min(a,b), (a+b-c)/√2)`; the bevel plane keeps sagging below the accumulator
long after the candidate passes `c`. Neutrality needs `(a+b-c)/√2 ≥ a`, whose
worst case `b == a` gives **`a ≥ c·(2+√2)/2 = 1.70711·c`**. Verified at the
crossover: with `c = 1`, `a = b = 1.700` evaluates to `1.697056` (sags) and
`1.710` is neutral. `SmoothUnion`/`SmoothSubtraction` saturate at one radius —
and `max(k)` really is the supremum, not a coincidence: a chain of *N* smooth
unions of radius `k` approaches sag `k` monotonically from below and never
exceeds it. `ChamferSubtraction`'s alternatives both fall away for `b ≥ c`.

## The proposal: a scoped accumulator

`PushField(blendOp, smooth)` / `PopField` — a scope evaluates into a fresh
accumulator seeded with `SDF_FAR_DISTANCE` and composes into its parent with the
recorded blend. All five accumulator-reading ops become local at once, because
they act on the *current* accumulator, which is now the scope's.

### Measured cost

`sdf-beam.comp` inlines `mapCore` **exactly once**, so it is the clean
per-evaluation probe. Baseline: **2089** DXIL instructions, **1** alloca.
(`dx.op` counted as call sites; an independent reviewer's tooling reported 350
vs 363 for the same build — the *deltas* agree exactly.)

| Shape of the idea | instr | dx.op | alloca | notes |
|---|---|---|---|---|
| Naive `float scopeD[4]`, dynamic-indexed | +147 | +21 | **5** | **scratch memory** — dead on arrival |
| Depth-4 shift registers, own `blendShape` | +122 | +18 | 1 | cost is a *second inlined* `blendShape` |
| Depth-1 explicit ops, own `blendShape` | +84 | +18 | 1 | same |
| Instance-implicit scope (no new opcodes) | +10 | +1 | 1 | **fatal** — see below |
| Push/Pop, POP fused into `SHAPE`'s blend tail | +17 | **+0** | 1 | a POP *is* a candidate |
| **…and POP carries its own compose blend** | **+7** | **+0** | 1 | stack shrinks to `(distance, material)` |

With the POP carrying its host-baked blend, the stack holds two values per
nesting level:

| Nesting depth | instr | dx.op | registers |
|---|---|---|---|
| 1 | +7 | +0 | 2 |
| 2 | +8 | +0 | 4 |
| 4 | +8 | +0 | 8 |
| 8 | — | — | — | *won't compile: HLSL vectors cap at 4 components* |

Independently reproduced on the 6×-inlined kernel: `sdf-world-views` at depth 4
is **+160 instr (1.40%)**, `dx.op` unchanged, alloca unchanged, and the
**float-op count is identical at 1084** — the only new computation is an integer
`op != SDF_OP_SHAPE` compare, so no new float decides a branch.

Two insights make it that cheap. **A POP is just another candidate**, so it
reuses `SHAPE`'s existing `blendShape` + material-winner tail instead of
inlining a second copy of a ten-way switch — that duplicate was the *entire*
cost of the naive explicit-op designs. And **the POP carries its compose blend**,
so the stack need not.

### Why instance-implicit scoping is fatal

Making `BeginInstance` imply a scope measured at +1 `dx.op` and looked like a
steal. It breaks `world-instanced`, whose entire premise is *"the instruction
streams are IDENTICAL, the declarations are metadata"* — and which puts a
`SmoothUnion` sphere **inside** an instance. Under instance-implicit scoping
that sphere blends against `FAR` instead of the ground plane, so
`instanced == flat` dies **by construction**, and with it the strongest
invariant in the instancing system.

Explicit Push/Pop are *instructions*: they appear in both streams identically,
so instances stay metadata.

### A prerequisite that already shipped

`PushField` seeds a scope with `SDF_FAR_DISTANCE`, so a scope's first
`SmoothUnion` member evaluates `blendSmoothUnion(1e9, b, k)`. The near-endpoint
select landed in `b6d51e3` returns `b` **exactly**. Without it the first scope
in any program detonates the field (`a + (b - a)` with `a = 1e9` returns 0, not
`b`). Nothing currently proves that select — **any scope work must add a Post
gate pinning `blendSmoothUnion`'s far and near endpoints.**

### The prize: culling comes back

`BeginInstance; PushField; …intersections…; PopField(Union); EndInstance` gives
the instance a far-neutral compose, so it is maskable again.
`UnmaskableBoundRadius` demotes from *the answer* to *the fallback for an
unscoped accumulator-reading op*. Today every creator shape with `onion != 0` is
an unmaskable instance, evaluated for every tile of every frame. **That is the
flat model's real running cost.**

## What a scope costs

Not expressiveness. Unscoped ops still see the whole accumulator, so "intersect
the entire scene with a box" (a cutaway view) and "onion the whole world"
survive — **the flat accumulator's bug is also a feature**, and Push/Pop keeps
it. Material semantics survive under a Union compose (`argmin` is associative).
Far-neutrality ⇒ skippability survives, hierarchically. Scope-free programs
render bit-identically (`min` associativity verified bit-exact over 5×10⁶
random triples, 0 differing).

What it does cost:

1. **The mental model.** "One number, folded left" is why this VM is legible.
2. **`AnalyzeLipschitz` becomes a tree fold.** A chamfer POP composing a warped
   subtree needs `√2 · max(L_parent, L_child)`. Get it wrong and you get march
   holes — the one failure mode cross-backend parity cannot see. **Expect the
   next bug here.**
3. **`SDF_FAR_DISTANCE` gains a new exposure surface** — it now meets the first
   op of *every* scope. `Push; Onion; Pop` yields `abs(1e9) - t`.
4. **Two always-evaluate segments per scope**: a whole-segment skip must never
   jump a `Push` or `Pop`, so they want their own `ResetPoint`.
5. **A trap at the fusion point.** `SHAPE` multiplies its candidate by
   `distanceScale`; a POP's candidate is already in world units and must **not**
   be. Same for `parityMaterialDelta`.
6. **Registers** — 2 per nesting level, live across the inner loop. No scratch
   (measured); occupancy is the one cost DXIL will not show.
7. **The run document** gains a nesting construct. Real design work in
   `Puck.Scene`.

Note what it does **not** cost: pruning. `AnalyzeSegment` already gives up
(`segmentEligible = false`) on any non-Union blend or field op, so a creator
group's members and an onion'd chain carry **no** segment or shape bounds today.
Scoping forfeits nothing that was still there — and hands back the instance
mask, which skips the whole range.

## Corrections a design pass must carry

Produced by an adversarial review of the design; each is a real defect in the
obvious implementation.

1. **`AnalyzeSegment` needs explicit `Push`/`Pop` cases.** Its `default:` arm
   sets `chainBoundable = false` *and* `segmentEligible = false`. The second is
   mandatory; the first is a cull **regression** — it suppresses the per-shape
   bound for every shape after the Push in that chain. Push/Pop touch the field,
   not the point, so the chain's world-space sphere stays sound.
2. **`AnalyzeLipschitz`**: fold the chamfer factor in at POP chain-close, or
   forbid chamfer blends on `PopField`.
3. **Validator**: balance within each instance range *and* within the world set;
   depth ≤ 4; reject `POP` without `PUSH`; reject a shape-less scope.
4. **POP's material tie-break must match `SHAPE`'s** — strict `<`, so the parent
   keeps its material on a tie. A scoped shape resting on the ground plane is a
   contact locus of ties.
5. **Post gate pinning `blendSmoothUnion`'s two endpoints** (see above).

## Why host-side lowering cannot substitute

"Emit intersections first" is real discipline and it costs nothing — but it
lowers **at most one** accumulator-reading op per program. Emit `A`, `Onion`,
`B`, `Onion`, and the second onion shells `A ∪ B`. A per-shape modifier cannot
be reordered ahead of the world when there are two of them.

The run document proves it outright: `SceneObject.Emit` composes objects from a
JSON array, so **the array order *is* the semantics.** There is nothing to
reorder. `onion` and `dilate` are shipped schema fields that work correctly on
object zero and no other. For an engine whose thesis is *one versioned JSON
document describes a run*, that is a hole in the product, not a wart.

The same limit shows up in `WorldWarpStage`: **only one cluster in a
flat-accumulator program can own a clean accumulator.** Its `Xor` pair still
composes globally, and reads correctly only because the teal sphere overlaps
nothing but its brick partner.

## Recommendation

**Do it, at depth 1.** Depth is not in the ISA — it is a shader constant plus a
validator rule. Start at 2 registers and `+7` instructions, which covers every
case that exists (creator groups cannot nest — `BeginInstanceCore` throws; the
chamfer wedge is depth 1). Raise the cap to 4 later with a `#define` and zero
re-gating of the packed layout.

Staged landing:

1. **Engine, Post-gated.** The ops, the fused tail, the validator, the
   `AnalyzeSegment`/`AnalyzeLipschitz` corrections, a `world-scope` stage
   asserting a scoped intersection renders as the intersection of its own
   members *and* that a scoped instance is maskable with `instanced == flat`,
   plus the `blendSmoothUnion` endpoint gate.
2. **Demo (greenfield — verify by running).** Creator wraps Pass-2 groups and
   each Pass-1 shape's onion in a scope. Fixes the workbench wipe and the
   ground-shell, and hands culling back to every onion'd shape.
3. **Run document.** The nesting construct; `WorldChamferStage` can drop its
   emit-intersections-first trick.

**What would change the recommendation:** a VGPR count showing the register cost
eats occupancy in `sdf-world-views`. DXIL will not give it — RGA, or an A/B on
`world-swarm`'s `PUCK_TIMING=1` numbers, would. Nothing else found so far argues
against it.

## Open, filed elsewhere

- **Creator's `Intersection` blend wipes the workbench** — Pass-2 groups are
  emitted after the floor. Options: reorder, scope, or drop the blend.
- **The run document's op gap** — `logSphere`, `repeatPolar`, `symmetryPlane`,
  `displace`, `domainWarp` and the whole 2D-primitive family are unauthorable
  from a data file.
- **`SDF_WPG_P4G` renders as `p4`** — zero mirror classes; a redesign of the
  turn cocycle. (`CMM` and `P6` carried the same class of defect and are fixed.)
- **The horizon dips behind foreground objects** — the ground plane is genuinely
  missing in a tile-shaped notch. Hypothesis: `MaxSteps` exhaustion on grazing
  ground rays that inherit a nearby object's small `marchStart`. Pre-existing.
  A ray that runs out of steps has *not* proven it missed, yet `renderView`
  falls through to `skyColor` as though it had.
