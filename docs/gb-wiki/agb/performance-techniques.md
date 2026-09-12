# Performance Techniques

Performance changes must preserve the machine's observable timing and replay
contract. Optimize derived work and instance orchestration; do not approximate
simulation state or introduce scheduling-dependent behavior.

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
