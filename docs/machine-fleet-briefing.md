# Machine-fleet workload briefing

GamingBricks are first-class world objects, not isolated emulator windows. The
fleet design must support active machines, dormant snapshots, linked pairs, and
many displays derived from a smaller number of deterministic timelines.

Measured performance and optimization decisions live in
[machine-fleet-plan.md](machine-fleet-plan.md). This document defines the
workloads those measurements must represent.

## Workload classes

- **Independent machines:** each machine consumes its own input stream.
- **Choirs:** multiple compatible machines consume one timeline. Machines that
  converge byte-for-byte may park behind a leader and mirror its framebuffer.
- **Linked groups:** a cable-connected pair or group advances as one serialized
  work unit while unrelated machines may run in parallel.
- **Dormant machines:** inactive machines retain snapshots and resume by restore
  plus deterministic catch-up. Replaying elapsed time is reserved for fiction
  that requires it.
- **Dynamic migration:** a running SM83 cartridge may change DMG, CGB, or AGB
  costume through snapshot, reconstruction, and restore when the target profile
  is compatible.
- **Sensor-fed cartridges:** camera and world-lens cartridges receive
  deterministic host-produced images. A future sensor must use the same
  recordable input boundary.
- **Machine-to-host events:** cartridge RAM or a link-peer protocol can carry
  deterministic events into the world. The carrier is part of the machine
  contract and must be replayable.
- **Recursive composition:** a machine framebuffer may become another machine's
  sensor input. Budget one produced frame of latency per host-mediated hop unless
  a stricter contract is introduced.

## Capacity posture

The active target is 64 real-time-stepped machines with frame headroom on the
reference development system. Hundreds of resident machines are supported by
dormancy. Choir width is primarily a presentation cost because one verified
leader can supply many followers.

These targets cover both independent and shared-timeline workloads. A benchmark
that measures only identical machines or only one-machine throughput is
insufficient.

## Determinism constraints

- A machine is a pure function of configuration and consumed input.
- Preparation may read shared timeline and input services only on the render
  thread. Execution touches private machine state and may run in parallel.
- Linked endpoints advance through their shared link session as one unit.
- Timer processing precedes serial processing at equal timestamps.
- Parking requires a byte-for-byte state comparison. A mismatch refuses the
  park; divergence after parking requires restore and independent stepping.
- Whole-application screenshots are presentation evidence, not a simulation
  determinism oracle. Use fixed-tick validation and same-run machine comparisons.

## Integration surfaces

- `GamingBrickChildNode.PrepareStep` stages timeline and input work.
- `GamingBrickChildNode.ExecuteStep` advances an unlinked machine.
- `GamingBrickChildNode.ExecuteLinkedStep` advances a linked pair.
- `OverworldRenderNode` prepares work serially and fans independent units out
  behind a barrier before GPU submission.
- `MachineInstance.Snapshot`, `Restore`, and `Fork` provide migration, dormancy,
  and counterfactual-machine primitives.

## Verification expectations

Fleet changes preserve the relevant emulator POST battery, the benchmark's
serial/parallel equality checks, and the demo's fixed-tick validation. Changes
to shared engine stepping also run the engine battery selected by the
`verifying-puck-changes` skill.
