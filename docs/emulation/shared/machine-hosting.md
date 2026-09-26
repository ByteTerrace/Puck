# Machine hosting runtime

`Puck.GamingBricks` supplies state serialization, fork ownership, and queued hosting
for the [Humble](../hgb/README.md) and [Advanced](../agb/README.md) emulators.
The hardware core owns its CPU, picture processing unit (PPU), audio processing
unit (APU), bus, and cartridge. The shared layer controls how an application
advances that core and consumes its outputs.

## Choose a hosting path

| Application need | Start here |
|---|---|
| Run a core on an application-owned thread | [Synchronous core hosting](#synchronous-core-hosting), then the [HGB](../hgb/embedding.md#quick-start) or [AGB](../agb/embedding.md#quick-start) example. |
| Submit work to a bounded background worker | [Queued screen-machine hosting](#queued-screen-machine-hosting). |
| Save a host and continue it later | [Durable checkpoints](#durable-checkpoints). |
| Explore a possible future or reduce display latency | [Forking a machine](#forking-a-machine) and [time travel](#rewind-runahead-and-fast-forward). |
| Connect running machines | [Cable-linked groups](#cable-linked-groups) and [serial link sessions](link-cables.md). |

The neutral interfaces live in `Puck.Abstractions.Machines`. `IMachineEngine` exposes
an engine id and descriptor; `CreateMachine` accepts structured configuration and
host-prepared assets, while `Create` accepts the engine’s option string. A runtime
reports availability independently of whether it has a display. Its `Advance` call
is synchronous; `IQueuedMachineRuntime` adds asynchronous submission. Video, audio,
input ports, feedback, hardware access, and time travel are optional capabilities.
The [machine contracts](../../../src/Puck.Abstractions/README.md) own their full API.

`QueuedMachineHost` exposes the bricks' named `video`, `audio`,
and `controls` capabilities through that contract. `AccessHardware` marshals a
coherent inspection or a validated patch/bus operation to the owning worker or
coupled link. Successful state-changing accesses invalidate rewind history in the
same ordered work item; unavailable or unsupported hardware returns an explicit
status rather than a substitute zero.

## Forking a machine

The following sketch assumes a factory for your machine and configuration types.
The concrete core construction examples are linked above.

```csharp
using var instance = MyMachineFactory.Create(configuration, compose);

// A fork is an independent machine loaded with instance's current state.
// Stepping either machine afterward leaves the other untouched.
using MachineFork<MyMachine, MyConfiguration> fork = instance.Fork();

fork.Machine.RunCycles(cycles: 200);
```

`Fork()` reuses a bounded pool of parked siblings once warm: a disposed fork
returns its underlying sibling to the pool instead of tearing its container
down, and the next `Fork()` call rents it back and restores into it—a
restore using the retained scratch writer. A generation check on the rental
handle prevents a disposed handle from reaching a sibling that has since been
rented again. Dispose each fork before its source instance.

## Synchronous core hosting

`IQueuedMachineCore` also works in a host's own synchronous update loop.
Construct [AdvancedGamingBrickCore](../agb/embedding.md#quick-start)
or [HumbleGamingBrickCore](../hgb/embedding.md#quick-start)
with ROM bytes and explicit configuration. The name describes the adapter
contract; constructing a core starts no worker or rendering infrastructure.

- Keep stepping, input, output, snapshots and disposal on one owning thread.
  Separate cores can run concurrently. Keep supplied ROM/configuration buffers
  immutable for the lifetime of the core and its forks.
- Apply `MachinePadState` before advancing. `RunCycles` takes master-clock
  cycles (AGB CPU cycles, HGB LCD dots) and
  completes the instruction in flight, so a call can overshoot its budget.
  Carry fractional pacing remainders in a long-running host. HGB carries
  instruction overshoot internally; AGB callers subtract the previous
  call's overshoot from the next budget. AGB's rate is
  16,777,216 cycles/second; HGB's hardware rate is 4,194,304 LCD dots/second,
  including CGB double speed. HGB's `CyclesPerSecond` retains the queued
  host's speed policy: pass `dmgSpeed: true` to keep that reported rate at
  the dot rate when using it for your host's pacing. `NativeFrameIndex` is
  based on the master clock and remains usable while the LCD is disabled.
- `Framebuffer` is a live, row-major `0x00RRGGBB` span: 240 × 160 for AGB,
  160 × 144 for HGB. Copy it before another thread uses it or the core advances.
  Alpha is absent; set it when uploading to a format that requires it.
- Call `ConfigureAudio` with the output rate, drain signed 16-bit interleaved
  stereo samples regularly into an even-sized buffer, and pass only the
  returned sample count to the audio device. The returned count includes
  left and right separately. Rate 0 disables output. Audio buffers are
  presentation state and are cleared on restore.
- `CaptureState`/`RestoreState` reuse a caller-owned buffer for **same-core**
  rewind. These raw bytes carry no identity guard; do not load them into a
  different ROM, model, BIOS or configuration. Use the machine's typed
  `Snapshot`/`Restore` API when identity validation is needed. Dispose each
  `CreateLookahead()` rental before disposing its source core.
- Omit `savePath` for in-memory operation and use the cartridge's export/import
  API for a custom save service. File-backed hosts should call `FlushSave`
  periodically; disposal performs a final flush. Optional file I/O failures
  are reported to standard error.

The `embedding` stage in each Post battery exercises this path without a
worker, graphics backend or audio device.

### Firmware and startup

`MachineBootMode.Cold` executes the selected firmware from reset.
`MachineBootMode.Fast` starts at the seeded cartridge handoff while retaining
that firmware's identity. On AGB the same BIOS continues serving software
interrupts and hardware interrupts; skipping the animation is not a substitute
for these services.

The screen engines share `cold`/`fast` tokens and a final `bios=<path>` option.
The path consumes the rest of the option string, so paths with spaces work;
matching surrounding double quotes are optional. `bios=puck` selects bundled
firmware. Machine-specific options, such as HGB's `cgb dmgspeed`, precede the
path. The [HGB guide](../hgb/embedding.md#firmware-and-startup)
owns revision-specific startup behavior.

### Host-selected content admission

`GamingBrickContentPolicies.Puck()` creates an immutable policy that admits
arbitrary user-authored cartridges with the verified `puck.cartridge.v1` source
format. Authors do not need publisher approval for each cartridge. The generic
`MachineContentAdmissionPolicy` also supports open admission, other explicitly
trusted source formats, and exact executable SHA-256 approval. Hashes can be
exceptions to an authored-format policy or the sole admission rule for a strict
host.

The host chooses the policy; a cartridge or its metadata cannot change it.
The host must stamp the source format only after its trusted provider has
successfully parsed and compiled the source, then evaluate the exact pinned
source and executable bytes before mounting them. A renamed native ROM has no
verified source format. A filename, header, or caller-supplied hash is not proof
of admission. Keep the bytes immutable through use, and apply the current policy
when creating, replacing, or restoring content. Calling a core constructor
directly does not invoke this policy for an embedding application.

Auxiliary `AssetPath` fields have a separate, explicit `MachineAssetAdmission`
choice; the policy defaults to denying them. Hosts that permit native content
and explicit firmware can select
`MachineContentAdmissionPolicy.Open(MachineAssetAdmission.Allow)`. Firmware
selection, privileged hardware access, and configuration authority remain
separate permissions. Content admission neither establishes copyright status
nor prevents an authorized program from executing arbitrary instructions.

## Tick-to-cycle translation

Queued hosts consume the integer time domain defined by `Puck.Hosting.EngineTicks`.
For a constant reported core frequency, their remainder accumulator computes:

$\text{cycles} = \left\lfloor \frac{\text{stepTicks} \times \text{CoreFrequency} + \text{remainder}}{\text{EngineTicksPerSecond}} \right\rfloor$
$\text{remainder} \leftarrow (\text{stepTicks} \times \text{CoreFrequency} + \text{remainder}) \bmod \text{EngineTicksPerSecond}$

Each budget rounds down and carries its fractional part into the next call.
The [synchronous contract](#synchronous-core-hosting) states the rate and overshoot
policy for each core. HGB consumes LCD-dot budgets even in CGB double speed;
CPU T-cycles are a separate hardware domain. Its reported pacing frequency can
also follow the queued host’s speed policy unless `dmgspeed` is selected.

## Queued screen-machine hosting

`QueuedMachineHost` is the base class exposed to machine-specific adapters. A
concrete host has one main job: turn loaded content into an
`IQueuedMachineCore`.

This adapter template assumes a `MyMachineCore` implementation of `IQueuedMachineCore`.

```csharp
using Puck.GamingBricks;

sealed class MyMachineHost : QueuedMachineHost {
    public MyMachineHost(string? savePath = null)
        : base(
            width: 160,
            height: 144,
            maximumPendingSteps: 3,
            workerName: "my-machine",
            audioSampleRate: 48_000,
            savePath: savePath) { }

    protected override IQueuedMachineCore CreateCore(
        byte[] data,
        string? savePath) => new MyMachineCore(data, savePath);
}
```

The core adapter deliberately stays narrow. `IQueuedMachineCore` advances a
requested cycle budget, applies one held `MachinePadState`, exposes native-frame
progress and packed `0x00RRGGBB` pixels, drains presentation audio, reports
feedback, flushes its save, and captures/restores complete deterministic state.
Optional default methods expose coherent worker-thread memory access and live
reconfiguration.

`QueuedWorkerLifecycle<TWorkItem>` owns the thread, bounded FIFO, backpressure,
stop, drain, and fault lifecycle used by both the worker and `LinkedMachineGroup`.
The worker applies these policies:

- `Submit` enqueues asynchronously. When the finite pending window is full, the
  producer waits until capacity opens and receives
  `AcceptedAfterBackpressure`; work is never dropped or coalesced.
- `IMachineRuntime.Advance` submits one segment and drains through a barrier
  before returning. Set optional input ports before advancing or submitting;
  submission captures their state before returning.
- Engine ticks become core cycles through `RationalRateAccumulator.TakeCycleBudget`,
  a remainder-carrying integer conversion against
  `Puck.Hosting.EngineTicks.PerSecond`. A core may change `CyclesPerSecond`;
  the conversion still carries phase rather than accumulating drift. A rewind
  restores that phase with the core, and a durable checkpoint persists it as
  its cycle remainder.
- Pixels are repacked only when a new native frame completes for queued calls.
  The synchronous path forces a stage to preserve its contract.
- GPU publication serializes uploads but does not hold the frame lock during
  the upload. The leased array stays outside the worker's write rotation until
  the upload returns.
- Audio crosses through a host-owned ring, so a consumer never touches the
  core's execution thread. When full, the ring drops the oldest audio and keeps
  the newest emulated second. It is the same `StereoSampleRing` both cores
  buffer their own output in, so a host that stops draining finds the newest
  second at every stage.
- Dirty save flushing is debounced by native-frame transitions—300 native
  frames, roughly five seconds for the supported handheld cores—not by the
  number of host submissions.
- Load, eject, and disposal stop acceptance, drain already accepted history,
  join the worker, and then dispose the core. Device loss drops only the GPU
  upload object; CPU machine state survives.
- A worker exception stops acceptance, wakes waiters, and appears through
  `QueueFault`. Synchronous operations surface it as an
  `InvalidOperationException` with the worker failure as its inner exception.

These guarantees matter more than queue throughput: an accepted segment is
part of authoritative history and must execute exactly once, in order.

## State serialization and divergence

`StateWriter` and `StateReader` encode scalar widths little-endian and copy
`WriteBlock<T>` spans verbatim; bulk values retain their in-memory layout.
`Reset` reuses the writer’s backing buffer once its
capacity is sufficient. A component whose save and load mirror each other
lists its fields once, in a method generic over `IStateTransfer`, and runs
that list through `StateSaveTransfer` to save and `StateLoadTransfer` to load,
so the two directions cannot disagree about order or width. Each transfer
member names the width it moves, so retyping a field fails to compile instead
of silently changing the layout. `SnapshotSection` names each captured byte range;
`SnapshotDivergence` compares two `SnapshotImage` values and reports the first
differing section and byte offset.

A complete core snapshot includes registers, pipeline and scheduler state, bus
latches, timers, memory, and peripherals; the exact sections belong to each core.
Restoring it and replaying the same inputs and budgets reproduces the captured
machine state and deterministic outputs. HGB BESS import/export covers that
format’s modeled scope and is distinct from a complete machine snapshot. Typed
core snapshots validate identity; raw capture buffers follow the same-core
restriction in the synchronous contract.

`RationalRateAccumulator` supplies both audio output stages with an exact rational
emission cadence in integer arithmetic, and `StereoSampleRing` buffers what they
emit. Presentation resampling does not feed back into emulated state.

## Durable checkpoints

Queued hosts implement `IMachineCheckpointRuntime`. Capture joins the worker
queue after accepted steps and returns an owned, checksummed image of the core,
held input, fractional clock conversion, completed-step count, and playback
settings. Restore targets a fresh, unstepped host with identical firmware,
cartridge, behavioral options, and snapshot format. Invalid checksums or content
identities refuse before changing the core. Discard the fresh host if a core
restore fails. Audio and uploaded pixels are presentation outputs rebuilt after
restore; queued audio from the retired host is not replayed.

Enabled rewind history and cores lent to a live link currently refuse capture.
Their history and shared medium need a complete persistence format before a
world using them can qualify for release management. Both Post batteries exercise
checkpoint restoration and matching continuation through `queued-host-time-travel`,
including nonzero fractional pacing, held input, and identity refusal. HGB core
snapshots also preserve the cumulative instruction budget: resetting it to the
completed clock would lose instruction overshoot and change continuation timing.

## Rewind, runahead, and fast-forward

`MachineTimeTravel<TInput>` is built over `ITimeTravelMachineCore<TInput>`, a
machine-neutral whole-state snapshot and lookahead interface. All operations
stay on the machine's single producer thread.

- *Rewind* stores a full keyframe at a fixed interval and records the input,
  exact cycle budget, and host accumulator phase for intervening frames. A
  rewind restores the nearest keyframe, deterministically replays to the target,
  restores the host conversion phase, and discards the abandoned future. A
  memory budget bounds the ring by evicting the oldest keyframe span.
- *Runahead* keeps one persistent, headless fork a configured number of native
  frames ahead on predicted held input. The fork supplies presentation pixels;
  the authoritative core remains tick-locked and is the only audio source.
- *Fast-forward* repeats the exact input/tick segment up to a capped factor and
  skips intermediate presentation staging. It does not multiply a core clock or
  replace several deterministic segments with one oversized step.

Rewinding also clears host audio from the abandoned future and republishes
feedback and pixels from the landing. Memory pokes or instruction-granular
advances that the frame-oriented replay log cannot reproduce invalidate stale
history rather than pretending it remains safe.

## Cable-linked groups

`LinkPacer` advances the machine furthest behind its cumulative target, one CPU
step at a time, breaking ties by cable position. SM83 pair sessions and native
GBA sessions share this interleave.

A *link* is an object that owns its members' cores. `LinkedMachineGroup` forms
one by quiescing each member's `QueuedMachineWorker` at a frame boundary
(`LendCore`) and lending its core to the group's single execution thread, where
an `IMachineGroupCore`—the medium plus its deterministic interleave—advances
every member through one shared cycle budget.

- *One publication path.* After each group step the members publish through
  their own workers (`PublishLentStep`): the same framebuffer, audio ring,
  feedback, and completed-step count a host already reads. Nothing above the
  worker changes when a cable goes in.
- *Per-seat input.* `MachineLinkPads` carries one `MachinePadState` per seat, in
  cable order, and is the held-input image the group's rewind ring replays.
- *One unit for the queue.* `Submit` accepts exact (tick budget, seat inputs)
  segments up to a finite pending window and backpressures at capacity;
  `IMachineLink.Step` is the synchronous submit-and-drain path. A lent member's
  own `Advance`/`Submit` refuses work, and its peek/poke/reconfigure/flush marshal
  onto the link thread through `IMachineCoreLender`.
- *Coupled time travel.* One `MachineTimeTravel<MachineLinkPads>` rides the group
  core, whose state image holds every member's snapshot **and** the medium's own
  pacing state, so a rewind lands the members and the interleave together and
  the resumed future matches the un-rewound run. Fast-forward repeats the exact
  segment for the whole group. Runahead is refused: a lookahead would have to
  fork every member and the medium, and a peer's future is not a function of
  held input.
- *Severing.* `Dispose` stops the group thread, disconnects the medium at once —
  an unfinished externally-clocked transfer stays pending, as an unplugged
  cable's does—and returns each core to its own worker with the group's
  tick-to-cycle accumulator phase, so the conversion carries no drift across the
  seam. Disposing a member while it is lent severs the link first; a second
  concurrent severing caller (typically another member disposing itself at the
  same instant) waits for the first to finish rather than observing a false
  "already severed" before the group thread has actually stopped. Every
  severing caller raises `SeverWaiting` immediately before it waits, so a
  caller can order work against severs that are known to be blocked.

Cross-process transport is out of scope here. The seam it would carry is the
group core's serializable state image plus each submitted segment; nothing in
this project reaches beyond the process.

## Core types

| Area | Types | Purpose |
|---|---|---|
| Serialization | `StateWriter`, `StateReader`, `IStateTransfer`, `StateSaveTransfer`, `StateLoadTransfer`, `SnapshotSection`, `ISnapshotable`, `SnapshotImage` | Little-endian whole-state capture/restore |
| Divergence | `SnapshotDivergence` | Section-localized first-difference report |
| Fork lifecycle | `ISnapshotableMachine`, `MachineInstance<TMachine, TConfiguration>`, `MachineFork<TMachine, TConfiguration>`, `MachineInstancePool<TMachine, TConfiguration>` | Pooled, ABA-safe forked-instance rentals |
| Queued machines | `QueuedMachineHost`, `QueuedMachineWorker`, `IQueuedMachineCore`, `QueuedWorkerLifecycle<TWorkItem>`, `IQueuedWorkItem<TSelf>` | Ordered off-thread emulation and complete-frame publication |
| Time travel | `MachineTimeTravel<TInput>`, `ITimeTravelMachineCore<TInput>`, `ITimeTravelLookahead<TInput>` | Bounded rewind, persistent runahead, and fast-forward |
| Cable links | `LinkedMachineGroup`, `IMachineGroupCore`, `IMachineCoreLender`, `MachineLinkPads`, `LinkPacer`, `ILinkPacerParticipants` | Group-owned cores, per-seat input, the shared interleave, and coupled time travel |
| Rate conversion | `RationalRateAccumulator` | Drift-free integer rate conversion: both audio stages' sample cadence and the host's tick-to-cycle budgets |
| Audio output | `StereoSampleRing` | The drop-oldest stereo frame ring both cores and the queued worker buffer audio in |
| Contract proof | `QueuedHostContractProbe`, `QueuedHostProbeResult` | Shared observable checks for concrete queued hosts |

Each brick re-exposes the closed generics under its own bare name through a
`global using` alias (`MachineFork`/`MachineInstance` in `Puck.HumbleGamingBrick`,
`AgbMachineFork`/`AgbMachineInstance` in `Puck.AdvancedGamingBrick`)—see each
project's `GlobalUsings.cs`.

## Verification and further reading

The [shared test suite](../../../tests/Puck.GamingBricks.Tests/README.md) owns its
run instructions. QueuedHostContractProbe exercises backpressure, frame and
audio publication, coherent hardware access, time travel, and whole frames written
into an uploaded source's region against real adapters. Both the [HGB Post battery](../../../src/Puck.HumbleGamingBrick.Post/README.md)
and [AGB Post battery](../../../src/Puck.AdvancedGamingBrick.Post/README.md) use it;
their fork-determinism stages also exercise pooled instance ownership.

- [Shared emulation infrastructure](README.md) — related machine contracts.
- [Project map](../../project-map.md) — dependency ownership.
- [GamingBricks license](../../../src/Puck.GamingBricks/LICENSE.md) — the shared legal terms.
