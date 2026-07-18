# Machine-fleet performance plan

This document records the fleet benchmark, reference measurements, accepted
optimizations, and regression guards. The workloads are defined in
[machine-fleet-briefing.md](machine-fleet-briefing.md).

Measurements are hardware-specific. Preserve the machine description and raw
report when refreshing them; do not compare values from different systems as a
single trend.

## Benchmark

The Humble Post executable exposes a diagnostic benchmark:

```powershell
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --bench `
  [--bench-rom <path>] [--bench-frames <minimum>] `
  [--bench-fleet <comma-separated-counts>] [--artifacts <directory>]
```

The report covers:

- independent and shared-timeline fleets;
- serial and parallel stepping;
- one-machine and parallel burst catch-up;
- `Create`, `Snapshot`, `Restore`, and `Fork` latency and allocation;
- a restore/run/read-cartridge-RAM/snapshot mailbox cycle;
- managed memory per resident machine.

Each fleet cell includes equal-input machines that must finish byte-identically.
Parallel results must also match serial results. A determinism mismatch fails the
run instead of publishing a throughput number.

The Advanced Post executable carries the same `--bench` diagnostic (fleet
sizes x independent/shared-stream x serial/parallel, Create/Snapshot/Restore/Fork
latency and allocation, plus the serial-vs-parallel bit-lock guard); the AGB
fleet budget is no longer unmeasured. Event-scheduling the prescaler/cascade
timers (the AGB throughput arc) measured MKSC (a Direct-Sound title) at
167→349 machine-frames/s single-machine and 1,508→3,030 machine-frames/s at
16-parallel; a synthetic no-audio control ROM confirmed the already-fast path
is unregressed. This document otherwise remains Humble-only below; refresh it
with an Advanced reference-measurement table the next time the AGB fleet is
benchmarked on this development system.

## Reference measurements

The following Release measurements use a commercial CGB cartridge on a
16-logical-processor development system. The synthetic cartridge remains within
approximately four percent for the fleet curve.

| Machines | independent serial | independent parallel | choir serial | choir parallel |
|---:|---:|---:|---:|---:|
| 1 | 520 | 529 | 530 | 530 |
| 4 | 530 | 1,926 | 528 | 2,039 |
| 16 | 559 | 4,232 | 540 | 4,495 |
| 64 | 562 | 4,473 | 548 | 4,186 |
| 256 | 556 | 4,471 | 550 | 4,115 |

Single-threaded throughput is approximately 520–560 machine-frames per second
across the measured fleet sizes. Parallel stepping plateaus near 4,200–4,500
machine-frames per second. The latter supports about 75 continuously stepped
60 Hz machines before presentation and engine headroom.

| Operation | Typical latency | Typical allocation |
|---|---:|---:|
| `Create` | 3.0–3.5 ms | 268 KiB |
| `Snapshot` | refresh required | refresh required |
| `Restore` | 30 us | 32 B |
| `Fork` | 62 us pooled | refresh required |

The current Humble `Fork` latency reflects the shared `Puck.Snapshots`
substrate and parked-instance pooling. The equivalent Advanced-core measurement
is 42 us. Snapshot latency/allocation and fork allocation have not been
re-measured on the current substrate; refresh those cells with the diagnostic
benchmark before using them for a capacity decision.

Additional observations:

- One machine catches up at about 8.8 times real time. Sixteen parallel replays
  sustain about 4.5 times real time each.
- Restore, run 15 frames, read cartridge RAM, and snapshot takes about 35 ms;
  emulated frames dominate the cost.
- Resident managed memory is about 345 KiB per machine. Dormant machines retain
  their snapshot images instead of active component graphs.
- CPU execution accounts for roughly 63–70 percent of emulation time, PPU work
  15–20 percent, component ticks 7–11 percent, and clock fan-out 5–8 percent.

## Accepted architecture

### Concrete component fan-out

`ComponentClock` stores concrete component fields and directly invokes their
tick methods. The cartridge's optional timed facet remains the only interface
slot. Construction validates each declared `ClockDomain`, and the call order
keeps timer before serial. This structure measured about 1.6 times faster than
interface dispatch in the same benchmark.

### Parallel machine execution

`OverworldRenderNode` calls `PrepareStep` serially because timeline cursors and
input drainers are shared. It then executes independent machines, or linked
pairs as single units, in parallel. The barrier completes before GPU work, so
render submission order remains deterministic.

### Choir parking

Machines group only when ROM, effective boot model, speed policy, and other
simulation configuration match. `TryParkBehind` requires `ContentEquals` before
the follower stops stepping. A parked follower mirrors the leader's staged
framebuffer; presentation choices are not part of the grouping key.

### Dormancy

Freeze-and-wake is the default: keep a snapshot, restore on demand, then catch
up only as required by the fiction. Replay from an earlier epoch is more
expensive and should be selected deliberately.

## Landed optimizations

- **Spawn pooling and snapshot allocation reduction** landed as ARC C
  (`95936fe`): `MachineInstance.Fork` restores into a bounded pool of parked
  instances instead of rebuilding a DI container, and the shared
  `Puck.Snapshots` writer is reused instead of newing one per call. Both
  Humble and Advanced cores migrated onto the one substrate.

## Deferred optimizations

- **Idle fast-forward:** measured HALT occupancy is about 27–30 percent, while
  PPU and APU work still advances. The estimated gain does not justify an SM83
  event-scheduler rewrite by itself.
- **Instance-mask decoding:** pre-decode per-tile instance masks only after a
  profile shows that repeated bit decoding contributes meaningful renderer
  cost.
- **Sensor-feed benchmark axis:** add a deterministic framebuffer-sized input
  copy when fleet sensor use becomes a material workload.
- **Linked-pair benchmark axis:** measure pair throughput separately when link
  gameplay becomes a capacity constraint.

## Renderer context

Fleet density also stresses the SDF renderer. Per-object bounds and per-tile
instance masks are the required scaling substrate: a ray evaluates world
segments plus visible-instance segments, not every instance in the scene. GPU
renderer measurements belong with the SDF performance artifacts and must name
the GPU, backend, resolution, content, and render scale. They are not directly
comparable to emulator machine-frames per second.

## Regression guards

For emulator-side fleet changes:

1. Run Humble POST Tier A.
2. Run the benchmark through its full serial/parallel equality checks.
3. Record the reference ROM, configuration, fleet sizes, and before/after
   machine-frames per second.
4. Preserve timer-before-serial ordering and compare deterministic frame hashes
   when changing the tick path.

For demo integration, run the demo's fixed-tick validation and smoke command.
For shared engine stepping, also run the engine battery selected by the
`verifying-puck-changes` skill.
