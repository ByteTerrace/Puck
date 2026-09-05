# Puck.GamingBricks

Puck.GamingBricks is the substrate both GamingBrick cores (`Puck.HumbleGamingBrick`'s
SM83 machine and `Puck.AdvancedGamingBrick`'s native ARM7TDMI machine) build
on: state serialization, forked-instance lifecycle, and the machine-neutral
queued-host substrate that turns a core into an off-thread, backpressured
emulation worker. It carries no console-specific CPU, PPU, or cartridge logic
of its own — that lives in each brick that references it.

## ✨ Key features

- *Little-endian state serialization:* `StateWriter`/`StateReader` write and
  read fixed widths plus `WriteBlock<T>` memcpy spans over a reused buffer, so
  a steady-state capture allocates nothing.
- *Section-localized divergence:* `SnapshotSection` names a captured byte
  range; `SnapshotDivergence` walks two `SnapshotImage`s and reports the first
  differing section and byte offset instead of a bare mismatch.
- *Pooled forked instances:* `MachineInstance<TMachine, TConfiguration>.Fork()`
  rents a sibling from a bounded per-instance pool and restores this
  instance's current state into it through a retained scratch writer — no
  container rebuild and no intermediate snapshot image once the pool is warm.
  `MachineFork<,>` is the per-rental owner handle; a generation check closes
  the ABA hole where a stale handle could reach a re-rented sibling.
- *Machine-neutral queued hosting:* `QueuedMachineWorker` owns one
  machine-owning thread and a bounded FIFO. A full queue backpressures the
  producer instead of dropping or coalescing authoritative input history;
  `QueuedMachineHost` forwards the neutral `IScreenMachine`/`IQueuedScreenMachine`/
  `IAudioMachine`/`IFeedbackMachine`/`ITimeTravelMachine` surfaces to one worker.
- *Machine-neutral time travel:* `MachineTimeTravel<TInput>` builds bounded
  rewind, persistent-fork runahead, and capped fast-forward over the small
  `ITimeTravelMachineCore<TInput>` adapter.
- *Owned cable links:* `LinkedMachineGroup` takes ownership of two or more
  workers' cores, steps them as one group through an `IMachineGroupCore`
  medium, and publishes each member back through its own worker — with the
  group's own bounded queue, backpressure, and coupled time travel.
- *Shared contract proof:* `QueuedHostContractProbe` drives the neutral
  `IScreenMachine`/`IQueuedScreenMachine` surface through the same checks —
  backpressure, frame publication, audio, coherent memory access,
  deterministic rewind, runahead lead, fast-forward bounds, upload leases,
  device loss, and disposal serialization — so both cores' Post batteries
  exercise identical substrate guarantees.

## 🚀 Quick start — forking a machine

```csharp
using var instance = MyMachineFactory.Create(configuration, compose);

// A fork is an independent machine loaded with instance's current state.
// Stepping either machine afterward leaves the other untouched.
using MachineFork<MyMachine, MyConfiguration> fork = instance.Fork();

fork.Machine.RunCycles(cycles: 200);
```

`Fork()` reuses a bounded pool of parked siblings once warm: a disposed fork
returns its underlying sibling to the pool instead of tearing its container
down, and the next `Fork()` call rents it back and restores into it — a
restore, not a container build.

## 🎮 Queued screen-machine hosting

`QueuedMachineHost` is the base class exposed to machine-specific adapters. A
concrete host has one main job: turn loaded content into an
`IQueuedMachineCore`.

```csharp
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

The core adapter deliberately stays narrow. `IQueuedMachineCore` advances an
exact cycle budget, applies one held `MachinePadState`, exposes native-frame
progress and packed `0x00RRGGBB` pixels, drains presentation audio, reports
feedback, flushes its save, and captures/restores complete deterministic state.
Optional default methods expose coherent worker-thread memory access and live
reconfiguration.

The worker owns the cross-thread policy:

- `Submit` enqueues asynchronously. When the finite pending window is full, the
  producer waits until capacity opens and receives
  `AcceptedAfterBackpressure`; work is never dropped or coalesced.
- `Step` is the compatibility path for a generic `IScreenMachine`: it submits
  one segment and drains through a barrier before returning.
- Engine ticks become core cycles through a remainder-carrying integer
  accumulator (`Puck.Hosting.EngineTicks.PerSecond`). A core may change
  `CyclesPerSecond`; the conversion still carries phase rather than
  accumulating drift.
- Pixels are repacked only when a new native frame completes for queued calls.
  The synchronous path forces a stage to preserve its contract.
- GPU publication serializes uploads but does not hold the frame lock during
  the upload. The leased array stays outside the worker's write rotation until
  the upload returns.
- Audio crosses through a host-owned ring, so a consumer never touches the
  core's execution thread. When full, the ring drops the oldest audio and keeps
  the newest emulated second.
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

## ⏪ Rewind, runahead, and fast-forward

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

## 🔗 Cable-linked groups

A *link* is an object that owns its members' cores. `LinkedMachineGroup` forms
one by quiescing each member's `QueuedMachineWorker` at a frame boundary
(`LendCore`) and lending its core to the group's single execution thread, where
an `IMachineGroupCore` — the medium plus its deterministic interleave — advances
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
  own `Step`/`Submit` refuses work, and its peek/poke/reconfigure/flush marshal
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
  cable's does — and returns each core to its own worker with the group's
  tick-to-cycle accumulator phase, so the conversion carries no drift across the
  seam. Disposing a member while it is lent severs the link first.

Cross-process transport is out of scope here. The seam it would carry is the
group core's serializable state image plus each submitted segment; nothing in
this project reaches beyond the process.

## 📋 Core types

| Area | Types | Purpose |
|---|---|---|
| Serialization | `StateWriter`, `StateReader`, `SnapshotSection`, `ISnapshotable`, `SnapshotImage` | Little-endian whole-state capture/restore |
| Divergence | `SnapshotDivergence` | Section-localized first-difference report |
| Fork lifecycle | `ISnapshotableMachine`, `MachineInstance<TMachine, TConfiguration>`, `MachineFork<TMachine, TConfiguration>`, `MachineInstancePool<TMachine, TConfiguration>` | Pooled, ABA-safe forked-instance rentals |
| Queued machines | `QueuedMachineHost`, `QueuedMachineWorker`, `IQueuedMachineCore` | Ordered off-thread emulation and complete-frame publication |
| Time travel | `MachineTimeTravel<TInput>`, `ITimeTravelMachineCore<TInput>`, `ITimeTravelLookahead<TInput>` | Bounded rewind, persistent runahead, and fast-forward |
| Cable links | `LinkedMachineGroup`, `IMachineGroupCore`, `IMachineCoreLender`, `MachineLinkPads` | Group-owned cores, per-seat input, and coupled time travel |
| Contract proof | `QueuedHostContractProbe`, `QueuedHostProbeResult` | Shared observable checks for concrete queued hosts |

Each brick re-exposes the closed generics under its own bare name through a
`global using` alias (`MachineFork`/`MachineInstance` in `Puck.HumbleGamingBrick`,
`AgbMachineFork`/`AgbMachineInstance` in `Puck.AdvancedGamingBrick`) — see each
project's `GlobalUsings.cs`.

## 🧪 Verification

```powershell
dotnet test tests/Puck.GamingBricks.Tests/Puck.GamingBricks.Tests.csproj
```

`QueuedHostContractProbe` supplies the heavier shared contract checks for real
machine adapters. Both `Puck.HumbleGamingBrick.Post` and
`Puck.AdvancedGamingBrick.Post` invoke those probes in their own batteries, so
the same substrate guarantees are exercised against the SM83 and ARM7TDMI
hosts. The forked-instance triad (`MachineInstance<,>`/`MachineFork<,>`/
`MachineInstancePool<,>`) is exercised through each battery's
`fork-determinism` stage.

## 📦 Packaging

`ByteTerrace.Puck.GamingBricks` depends on `Puck.Abstractions` (machine and
GPU contracts) and `Puck.Hosting` (`EngineTicks`' tick-to-cycle conversion).
`Puck.HumbleGamingBrick` and `Puck.AdvancedGamingBrick` both depend on it for
snapshot, fork, and queued-host substrate; it carries no console-specific
CPU, PPU, or cartridge logic of its own.
