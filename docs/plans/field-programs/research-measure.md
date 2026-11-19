# What the bake's field time is spent on

A cold bake of the debug world on the RTX 4070 spends 4.6 seconds (Direct3D 12)
or 3.4 seconds (Vulkan) of GPU time in the bake kernel for the world's own
scene, inside a bake that takes minutes. The kernel is not where a cold bake's
time goes, so neither a faster walk nor fewer taps on the GPU shortens the bake
much by itself. Inside the kernel the walk matters, more for short programs
than long ones: a straight-line specialization of the same program evaluates
the same samples, with the same answers, in 26 % to 65 % of the interpreter's
time. That gain does not pay for itself in a cold bake: compiling the glacier's
specialization takes DXC 33 seconds for DXIL and 108 seconds for SPIR-V, and
saves about 0.13 to 0.19 seconds of GPU time per bake of the glacier. The
measurements point at cheaper work first: the debug world baking a large share
of its surfaces twice, point transforms that every chain of a creation repeats,
and one Lipschitz clamp per field scope, which for the valley sector halves the
detail the refinement budget buys.
[Field programs plan](../field-programs.md) · [Bake proportional to change](../open-items.md#bake-proportional-to-change)

## What ran

Every figure below comes from one of these runs, on the RTX 4070 of the lead
machine, from one build of the integration branch. The machine was shared with
other lanes' builds and GPU work throughout, so wall-clock seconds (bake spans
and DXC times) run high; GPU timestamps, hardware counters, counts, and ratios
taken within one run are the evidence.

| Run | Command | What it gives |
|---|---|---|
| Cold bakes | `puck_world_start` windowed with a fresh `cacheDir`, `paused` and `lockstep`, then `world.wait bakes`: once with `backend: directx` and `captureTool: pix`, once with `backend: vulkan` and `captureTool: renderdoc` | Each residency's `[sdf.mesh-bake] done` line (samples, taps, dispatches, kinds, and `gpu-work` with each job's GPU time from its own timestamps), and on Vulkan each pipeline's `[pipeline-statistics]` line |
| Hardware counters | `gpu.capture` of two frames during the Direct3D 12 bake, then `pixtool save-event-list` with selected `--counters` | Per dispatch: duration, warp instructions, threads per warp instruction, pipe mix, achieved warps, register allocation, local-memory traffic, for 25 bake jobs |
| CPU bakes per creation | `puck mesh audit --cpu --world worlds/debug/debug.puck --prototype <id> [--tier <n>]` | The bake's own lattices and refinement answered by the CPU evaluator: samples, taps and kinds per creation with no device |
| Program structure | `puck creation stats` and `puck creation shape` for the same creations | Instructions per evaluation, segments, scope clamps, each shape's distance verdict |
| Walk against specialization | A throwaway generator wrote one creation's packed program as straight-line HLSL: the generic walk's arithmetic for each instruction with its words as literals, and no fetch, decode or dispatch. A throwaway device test ran the interpreter (`mapCore`) and the specialization over the same lattice, five taps a sample as the bake takes them, on both backends | Kernel time for each, the largest difference between their answers, and on Vulkan the driver's register and memory figures |
| Clamp removed | The same CPU bakes with every field scope's baked candidate scale left at one (a throwaway switch in `SdfProgram.AnalyzeLipschitz`) | What the scope clamp changes in refinement and samples |
| Compile cost | `dxc -O3 -T cs_6_6` on each kernel, with the shader build's options | Seconds and bytes per kernel |

The generator, the device test, its two kernels and the switch were deleted
after the runs.

## The cold bake: seconds of GPU in minutes of bake

The world residency's done line (the debug world's own scene):

| Field | Direct3D 12 | Vulkan |
|---|---|---|
| groups baked | 1,226 of 7,550 | 1,226 of 7,550 |
| samples | 1,034,862,846 | 1,017,456,411 |
| taps (field evaluations) | 1,495,856,334 | 1,474,428,667 |
| dispatches / GPU jobs | 48,422 / 4,434 | 48,204 / 3,294 |
| GPU time of the bake jobs | 4,596 ms | 3,361 ms |
| average tap | 3.07 ns | 2.28 ns |
| time a job was in flight | 65.8 s (occupancy 0.07) | 46.1 s (occupancy 0.07) |
| wall time to the last mesh | 188.0 s | 141.0 s |

The other 6,324 groups are copies of the baked ones within the same residency
and take their meshes.

Samples by kind on Direct3D 12: refine 137.1 M, slab 196.4 M, fine 196.7 M,
crossing 204.9 M, gradient 104.5 M, crease 8.8 M, material 155.1 M. A gradient
or crease sample takes five taps, so the gradient samples alone are about
523 M taps, 35 % of all taps; the root search along crossed edges (`crossing`)
is another 14 %.

On both backends the GPU is busy with a bake job only 7 % of the time a job is
in flight, and the slots hold a job for about a third of the bake's span. The
kernel is roughly 2.5 % of the bake's wall time. Halving every tap's cost would
save about two seconds of GPU time from a bake whose pace is set elsewhere: by
the contour and land stages and by the round trips between sampling and
contouring, which the open item's step 4 already ranks.

## Inside the shipped kernel

The capture holds 25 bake jobs, each a mask dispatch and a sampling dispatch
(one job's sampling dispatch fell outside the two frames). Summed over the 24
sampling dispatches, from `pixtool` on the Direct3D 12 capture:

| Counter | Value |
|---|---|
| duration (TOP to EOP) | 29.4 ms |
| warp instructions executed | 4.40 G |
| threads active per warp instruction | 30.3 of 32 |
| integer ALU / FMA / control (CBU) / load-store pipes | 44.9 % / 17.4 % / 10.7 % / 4.0 % of instructions |
| local-memory load sectors | 56.1 M |
| achieved warps (duration-weighted) | 29.3 % of the SM's maximum |
| register allocation (duration-weighted) | 89.5 % |
| mask dispatches | 25, 1.52 ms in all (5 % of the jobs' GPU time) |

The driver's Vulkan statistics for the shipped kernels: `sdf-mesh-bake` 128
registers a thread, 224 bytes of local memory and 6,912 bytes of shared memory;
`sdf-mesh-bake-mask` 31 registers and none.

So the kernel runs at a third of the warps an SM can hold, because its
registers fill the register file (at 128 registers an Ada SM holds 16 of its 48
warps, [gpu cost](../../../.claude/skills/rendering/references/gpu-cost.md#read-a-kernels-registers-and-occupancy)),
and it spills to local memory. Lanes rarely diverge. Integer and control
instructions outnumber floating-point arithmetic more than three to one, which
is the signature of decode, indexing and branching rather than distance
arithmetic. Program loads are only 4 % of instructions, but each instruction's
header and data loads lie on the walk's dependency chain, and a third of the
warps cannot hide their latency.

## Walking against arithmetic, measured directly

The interpreter fetches each instruction's header and two data vectors from
the program buffer, branches on the op, and keeps every op's state live across
a loop whose trip count it does not know (`mapCore` in
[sdf-map.hlsli](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-map.hlsli)).
A specialization keeps the arithmetic and drops the rest. Its kernel time over
the interpreter's is the share of the kernel the arithmetic takes, and one
minus it is the share the walk takes. Both kernels answered every sample alike:
the largest value difference was 2×10⁻⁶ for the glacier and 4×10⁻⁶ for the
valley sector, with no sign flipped.

| Creation, backend | Taps | Interpreter | Specialization | Ratio |
|---|---|---|---|---|
| Glacier, Direct3D 12 | 1,310,720 | 57.10 ms (43.6 ns a tap) | 37.02 ms | 0.648 |
| Glacier, Vulkan | 1,310,720 | 52.92 ms (40.4 ns a tap) | 22.13 ms | 0.418 |
| Valley sector north, Direct3D 12 | 5,242,880 | 2.54 ms (0.48 ns a tap) | 0.88 ms | 0.347 |
| Valley sector north, Vulkan | 5,242,880 | 3.26 ms (0.62 ns a tap) | 0.85 ms | 0.260 |

Medians of seven alternating runs each, over a cubic lattice of the program's
box with gradient taps a tenth of a cell out. For the glacier, 98,225 of the
262,144 samples lay within a cell of the surface; for the valley sector,
232,859 of 1,048,576. A first valley run over a box that missed the surface
gave the same times and ratios (0.343 and 0.257): with no culling inside a
scope, a tap costs the same wherever it lands.

The probe's interpreter is lighter than the shipped kernel's: it reads no
lattice table, no bake poses, no sampled regions and no glyph atlas. On Vulkan
it took 80 registers with 272 bytes of local memory against the shipped
kernel's 128 and 224. The glacier's specialization took 62 registers and the
valley's 40, neither with local or shared memory. The shares below are
therefore what the walk costs at least.

The glacier's per-tap cost is fourteen times the debug world's average tap.
It is one dynamic group whose 208 shapes sit in one field scope, so no sample
skips a shape: 1,866 instructions a tap, 117 segments through the host's
rigid-leaf plans and 92 through the generic loop, which holds all 83 of its
bends and 105 Gaussian pushes. The valley sector is 40 instructions with a
six-octave noise relief, and the specialization still runs it in a third
(Direct3D 12) to a quarter (Vulkan) of the interpreter's time.

So the share depends on the program and the backend: the walk took 35 %
(glacier) and 65 % (valley) of the kernel's time on Direct3D 12, 58 % and 74 %
on Vulkan. Sample count multiplies whatever a tap costs. For the debug world as
a whole the kernel is a few seconds, and the walk is part of those seconds; the
bake's minutes are elsewhere.

## Specialization costs more to compile than it saves

| Kernel | DXIL | SPIR-V |
|---|---|---|
| Interpreter probe | 1.9 s, 92 KB | 3.0 s, 269 KB |
| Glacier specialization | 32.9 s, 372 KB | 108.1 s, 911 KB |
| Valley sector specialization | 1.0 s, 12 KB | 0.9 s, 39 KB |

A CPU bake of the glacier at the 64 mm tier a view from the valley floor asks
takes 8,219,308 taps. At the measured costs that is 0.36 s of interpreter GPU
time against 0.23 s specialized on Direct3D 12, and 0.33 s against 0.14 s on
Vulkan: a saving of 0.13 to 0.19 seconds per bake of the glacier, against tens
of seconds of DXC and an unmeasured driver translation on every machine that
meets the new kernel. The valley sector's specialization compiles in a second
and saves about 3 ms per bake at tier 7. Every edit to a creation changes its
program and would compile its kernel again. Specialization per creation pays
only for a program evaluated far more often than it changes, and a cold bake
of an authored world is not that case.

## What the step-scale clamp costs

A scope's Lipschitz bound is baked as one candidate scale applied where the
scope pops (`SdfProgram.AnalyzeLipschitz`), so every sample inside the scope
reads its field multiplied by it. The refinement admits a cell whose nearest
corner reads within half the cell's diagonal of the surface
(`SdfSampledLattice.Refine`), so a scaled field admits cells the surface cannot
reach, out to the inverse of the scale. The glacier's 208 shapes share one
clamp of 0.159 (a bound of 6.3); `puck creation shape` gives the worst single
shape a step of 0.187, and 139 of its 208 shapes are uncertified warps. The
valley sector's four shapes and relief share a clamp of 0.387.

| Creation | Clamp | Cells measured | Split | Samples | Taps |
|---|---|---|---|---|---|
| Valley sector north, tier 7, clamped | 0.387 | 53,017 | 4 | 6,952,145 | 9,603,313 |
| Valley sector north, tier 7, unscaled | none | 26,626 | 5 | 8,867,589 | 12,802,873 |
| Glacier, tier 6, clamped | 0.159 | 59,094 | 4 | 6,713,796 | 8,219,308 |
| Glacier, tier 6, unscaled | none | 61,875 | not printed | 7,984,101 | 11,900,729 |

Without the clamp the valley admits half as many cells, and the bake spends
the budget it frees on a finer split: the sample count is capped by the
refinement's budget, not by the clamp. So the clamp's price is detail rather
than samples: at the same budget the clamped valley contours a quarter
coarser. The unscaled field overstates distance where the relief steepens, so
its run is an optimistic bound on what an exact field would admit, not a bake
to ship.

The glacier does not separate so cleanly. Unscaled, it measures about as many
cells (61,875 against 59,094) but takes 1.77 M fine samples against 3.81 M,
3.2 times the crossing samples (2.61 M against 0.80 M) and lands 507,515
triangles against 186,696. A positive scale cannot move the zero set, so the
difference lies in decisions that read the field's magnitude: the box
narrowing before the final lattice (`refine` samples 52,635 against 35,937),
the split a cell's samples ask for, and, by inference not checked here, the
land stage's simplification, whose errors a field scaled by 0.159 would read
6.3 times too small. The audit of the unscaled glacier was stopped before it
printed its split. The run does show that the scope clamp reaches past
refinement into every stage that compares a field value with a length.

## Exact distances against specialization

Bounds as tight as each shape's own, a clamp per shape or segment instead of
one per scope, act on the bake's budget-limited refinement and on every later
stage that reads a field value as a distance: for the valley sector, half the
measured cells, which is half the fine samples at the same detail or finer
detail at the same samples, on the CPU's contour and land as well as the GPU's
sampling. For the glacier the clamp distorts the box, the split and the
crossings at once; a tight bound would return those decisions to the field's
true scale, and its effect on samples needs the bake built that way to
measure. A specialization saves GPU time only, about two seconds of the debug
world's bake at most. Tighter bounds act where the bake's time is.

## Found on the way

- **The debug world bakes much of itself twice.** A routed session scene
  (`routed$session$…$shared`) bakes 1,017 groups beside the world's own 1,226:
  885,873,044 samples and 3,806 ms of GPU time on Direct3D 12, 829,424,287 and
  2,381 ms on Vulkan. Its slow-stage lines match the world's lattice for
  lattice: on Direct3D 12, world group −133 and session group −87 both contour
  dims (142, 40, 194) at factor 8 with 56,460 measured cells and 252,407 quads,
  and world group −7623 and session group −7419 both land 3,287,580 triangles
  to 32,192; on Vulkan both residencies land 4,039,824 triangles to 38,630 in
  the same group. The geometry key makes one body in two programs hash alike
  (`SdfMeshGeometryBuilder`), but residencies share only finished meshes: the
  table of groups already baking a geometry is local to one residency's
  restart (`leaders` in `SdfMeshPathBake.Restart`), and another residency can
  take only a mesh the library already holds (`SdfMeshLibrary.TryTake`). Two
  residencies that plan the same geometry before either lands it both bake it.
- **Every chain repeats its creation's root frame.** In the glacier's program
  209 of 1,866 instructions are resets, 209 dynamic transforms into the one
  slot it rides, and 209 each of translate, rotate and scale that repeat the
  creation's root frame (identity at rest); a placed static creation repeats
  its placement's frame in every chain the same way (the valley sector's five
  chains). Hoisting that shared prefix out of the chains removes over half of
  the glacier's instructions from both interpreters. The CPU evaluator shows
  the cost most: the CPU bake of the glacier at tier 6 took 386 seconds,
  against 6.5 seconds for the valley sector's 40 instructions at about as many
  taps.
- **The mask pass is a fixed cost per job.** Each job's mask dispatch scans
  every instance of the program for each lattice; in the capture it took 5 % of
  the jobs' GPU time.

## What the numbers say to build

1. Share a geometry being baked across residencies, not only within one: the
   library, which every residency already asks for finished meshes, also names
   the group baking each geometry, and a second residency's group waits on it
   as a group in the same residency does. It removes up to 0.89 G samples,
   2.4 to 3.8 s of GPU time and the same share of contouring and landing from a
   cold bake of the debug world, more than any change to the walk. *Check:* in
   a cold bake the session residency's start line counts these groups as
   shared, its `samples=` falls by the matching amount, and the slow-stage
   lines no longer repeat a lattice under two residencies.
2. Compile each chain's shared prefix once: drop identity point ops in the
   builder and hoist the root frame and dynamic transform out of every chain,
   a program-builder change both interpreters read. *Check:* `puck creation
   stats` prints the glacier under 900 instructions per evaluation, and the CPU
   bake of the glacier at tier 6 finishes in under half its time with the same
   mesh audit.
3. Bound each shape or segment with its own clamp instead of one per scope,
   so the refinement admits only the cells a surface can reach and the later
   stages read distances at their true scale. Each op's Lipschitz arm is
   already per instruction, so this belongs with the `field-ops` lane's one
   definition per op. *Check:* the valley's `cells measured` falls toward the
   unscaled run above at the same split, and the glacier's mesh audit stays
   clean.
4. Bring the shipped bake kernel under the register file's next step (96
   registers gives 20 warps, 80 gives 24) and remove its local-memory spill.
   The probe's 80 registers show how much of the 128 comes from what the bake
   adds around `mapCore`; the skill already names `SdfBakeTaps`, indexed by
   the loop counter, as one spill. *Check:* the Vulkan `[pipeline-statistics]`
   line for `sdf-mesh-bake` under 97 registers with no local memory, and the
   cold bake's `gpu:` falls.
5. Leave per-creation kernel specialization out of the bake. Revisit it only
   for a program evaluated far more often than it changes (a contact or query
   kernel over a fixed world), where its 1.5 to 3.9 times per tap and its lower
   register count would pay for its compile.

## What remains to measure

- **Which groups carry the debug world's taps.** A per-group GPU-time line in
  the bake report would say how much of the 4.6 s the long, unculled programs
  take. It is a small reporting change for the bake's own lane.
- **Instructions executed per tap in the shipped kernel.** The capture's jobs
  do not record their sample counts; a capture taken with the bake report's
  per-job samples beside it would turn the counters above into a per-tap
  figure for each kind of lattice.
