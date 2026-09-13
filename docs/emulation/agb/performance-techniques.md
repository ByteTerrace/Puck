# Performance techniques

The Advanced Gaming Brick (AGB) must preserve bus timing, event order, and snapshot
content while it gets faster. The techniques here reduce repeated work only
where the existing verification surfaces can still observe the same machine.

Performance changes must preserve the machine's observable timing and replay
contract. Optimize derived work and instance orchestration; do not approximate
simulation state or introduce scheduling-dependent behavior.

## Execution equivalence and candidate changes

`RunCycles` must finish on the same instruction as repeated `Step` calls
given the same cycle budget, including overshoot. Fetched instruction words
and delayed IRQ recognition remain in the CPU pipeline. Memory accesses can
trigger DMA, timers, or scheduled peripherals before an instruction ends.
Compiling existing instruction handlers still pays these costs; removing
opcode dispatch alone does not establish a performance improvement.

The built-in timer and interrupt controllers publish clock readiness into
one `AgbClockState` per bus. Ordinary clock charges check this derived status
and the scheduler's next event deadline. Register writes, pending latches,
IRQ transitions, and restore update readiness at their source; the bus keeps
the existing per-cycle path for unsettled state and custom controllers.
No charge crosses a scheduled event, including an event at its final cycle.
The readiness cache adds no snapshot fields.

A halted CPU step checks for a wake interrupt, then advances one idle cycle
and pending DMA if it remains asleep. It returns control so the next host
input or linked peer can produce the wake interrupt. `RunCycles` counts these
idle steps alongside instruction and exception steps; a sleeping CPU does
not wait indefinitely inside a call. STOP still uses the modeled restricted
wake sources while peripheral clocks continue advancing.

Text backgrounds without horizontal mosaic resolve one tile-map entry and
read one packed tile row for up to eight pixels. These bytes are used only
within the current scanline callback, so CPU and DMA writes before the next
line are visible without maintaining a persistent graphics cache. Horizontal
mosaic retains the scalar pixel sampler. The layer compositor and raster
event schedule are unchanged.

Further architectural opportunities have different benefits and boundaries:

| Direction | Intended benefit | Required boundary |
|---|---|---|
| Execute CPU blocks up to the next observable event | Reduce repeated execution and timing work | Account for scheduler events, timer overflow, IRQ synchronization, DMA, fetch/prefetch effects, and the caller's budget. Preserve already fetched words when code changes. |
| Retain decoded graphics across scanlines | Avoid repeated decoding of unchanged graphics | Invalidate affected data on CPU and DMA writes to VRAM, palettes, and OAM; preserve register-write timing and rebuild derived caches after restore. |
| Prepare immutable cartridge data once per shared image | Reduce machine construction and fleet startup cost | Share ROM identity and save/sensor classification while keeping saves, RTC, GPIO, and other mutable state per machine. Establish image ownership before caching metadata. |

Cartridge construction currently scans each ROM for library signatures, and
machine construction hashes it for snapshot identity. An immutable image
could perform that work once. It must identify the actual BIOS and cartridge
supplied by custom composition and apply runtime diagnostic overrides
separately from immutable metadata.

The Post battery's
[`cycle-budget-execution` stage and `--compare-execution` diagnostic](../../../src/Puck.AdvancedGamingBrick.Post/README.md#determinism-diagnostics)
compare complete state and audio across execution paths. They supplement
external conformance and frozen-build comparisons; comparing two paths through
the same hardware model cannot prove hardware accuracy. Future acceleration
should retain the interpreter and support runtimes without dynamic code.

## Interpreter dispatch

The ARM and Thumb interpreters use precomputed unmanaged function-pointer tables.
This removes delegate allocation and most runtime decode branching while retaining
an instruction boundary at which the scheduler can observe interrupts and device
events.

A cached, block-linked interpreter could additionally avoid repeated fetch and
dispatch work. It is only worth pursuing after profiling the current table-dispatch
baseline. Any implementation must preserve bus effects, prefetch state, open-bus
behavior, self-modifying IWRAM, and the existing scheduler boundaries. Published
speedups over interpreters that decode every instruction are not a useful estimate
for this core. See [Writing a Cached Interpreter](https://emudev.org/2021/01/31/cached-interpreter.html).

A native-code JIT is not planned. Its larger execution blocks complicate precise
device interleaving, invalidation, diagnostics, and deterministic replay without a
demonstrated fleet-level benefit.

## Scheduler and idle spans

`AgbScheduler` maintains a small ordered event set. `StepClocks` can collapse a
quiescent interval to the next event when no intervening state transition is
possible. That is the supported form of idle skipping: the result is derived from
committed machine state and does not depend on game-specific address databases or
runtime heuristics.

A prescaler timer's counter is a closed form of the master clock, so the timer
block schedules each overflow as an event rather than stepping every cycle: an
enabled Direct-Sound timer (which essentially every commercial game keeps running
all session) no longer defeats the collapse. Per-cycle stepping remains only inside
the ≤2-cycle control/reload-latch and overflow→IRQ windows, entered on a timer
register write or a pending IRQ; on window exit the block re-anchors and re-queues
its overflow. The closed form is the exact arithmetic of the per-cycle step (same
`(clock & mask) == 0` boundary), so the two are bit-identical.

The current event count is small enough that a linear ordered structure is
appropriate. Reconsider a heap or timing wheel only if measurement shows event
insertion or removal to be material. mGBA's timing implementation provides a useful
comparison: [mGBA timing.c](https://github.com/mgba-emu/mgba/blob/master/src/core/timing.c).

## Memory access

Bus access is a likely profiling target because every instruction reaches it.
A software fast-memory path may map side-effect-free RAM and ROM regions directly
while routing I/O, contention, timing, prefetch, and unmapped accesses through the
full bus path. Prefer managed representations until benchmarks show that pinned or
unsafe buffers improve the real workload; modern .NET often removes bounds checks
from simple indexed loops.

OS page-fault-based fast memory is unsuitable. Fault delivery is difficult to
integrate safely with managed code and adds platform-specific behavior that is not
needed for the GBA address map.

## Fleet execution

Parallelism belongs between independent machines, not between components of one
machine. A fleet scheduler may advance sealed machine instances through a bounded
worker pool provided that each instance owns its mutable state and externally
visible ordering is assigned by the shared deterministic timeline.

Threading the CPU, PPU, or APU within one machine is not planned. Synchronization
would enlarge the state and replay surface, and GBA layer selection and blending do
not map cleanly to fixed-function GPU blending. DSHBA documents the resulting
multi-pass compromise in a GPU-rendered design: [DSHBA](https://github.com/DenSinH/DSHBA).

## Measurement order

Use representative one-, four-, and many-machine workloads. Measure before changing
the core, then prefer work in this order:

1. Bus and memory-access costs.
2. Fleet scheduling and allocation pressure.
3. Snapshot, hash, and replay costs used by fleet diagnostics.
4. Block linking, only if dispatch and fetch remain material.

Every candidate must retain the existing conformance, co-simulation, snapshot, and
determinism evidence.
