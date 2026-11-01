# The simulation step off the frame thread

Design memo for the [simulation tick off the frame thread](open-items.md#simulation-tick-off-the-frame-thread)
packet, which the owner queued. It settles the shape from the code and hands
specialist lanes a phased build. File and line citations name the sources as
they stand before phase 1 at `8075db0d56`; a builder re-reads each before editing.
The delivery notes below map the current seams after phase 1.
This is the second revision: the first (one free-running simulation thread
owning the pump, with a folding mailbox to the frame) had its one cross-family
review, and [its findings](#review-resolution) changed the shape.

The goal is the [frame-rate bar](../decisions/rendering.md#the-frame-rate-bar):
on the RTX 4070 every frame under 16.7 ms at the highest settings. Today a
windowed iteration that carries a 30 Hz tick pays the tick (6 to 12 ms walking,
2 to 4 standing) and the frame's produce (6 to 7 ms on Direct3D 12, 8 to 11 on
Vulkan) on one thread, so one iteration in two or three misses the blank
([frame time](open-items.md#frame-time-on-the-lead-machine)).

## Decisions

1. **The frame thread keeps everything it owns today; one worker runs only
   the step body.** The pump, the input router, the console drain, the
   capture scheduler, the tick script, the wait gate, the test-run scheduler,
   the seat half of `WorldClient` and every hook that publishes a tick stay on
   the thread that produces frames. The worker runs `WorldHostStep`'s body
   (`WorldServerStepShell.Step` for boot and `StepInstances`,
   `src/Puck.World/WorldHostStep.cs:140-151`), which touches server-owned
   state only, between a *dispatch* and a *join* on the frame thread.
   Overturned: a simulation thread that owns the pump and the client's seat
   half (review F05 to F09, F15).
2. **The step overlaps the frame's idle, not its work.** The loop dispatches
   the body right after present and joins it after the next iteration's input
   capture, so the body runs under the present block and the pacer's wait.
   The frame never waits for the body: a body still running at the join is
   applied one iteration later, counted, and the frame shows the last applied
   tick with alpha clamped at one.
3. **The handoff is a tick record, written by the worker and replayed by the
   frame thread, never shared live.** The hub delivers the body's ticks into
   one recording sink; at the join the frame thread replays the record into
   `WorldClient` and the boot endpoint's `WorldSessionMirror`, calling the
   `IClientSink` methods the hub would have called. The writer runs only
   between dispatch and join and the reader only between join and dispatch,
   so the record needs no ring, no fold and no publication protocol beyond the
   join's own barrier. Every delivery is applied, in order, before the next
   dispatch. Overturned: a depth-four mailbox folding superseded snapshots
   with `EntityContinuity.Then` (F01 to F04).
4. **The frame derives its clock and alpha from the record it applied.**
   `WorldClient.Tick`/`EngineTick` (`src/Puck.World.Client/WorldClient.cs:132-135`)
   replace `IWorldSimulationClock` and every `m_server.NextInputTick` read on
   the frame path; the interpolation fraction is the wall time since the join
   that applied the tick over its step period, the formula
   `WorldSessionMirror.ResolveInterpolationAlpha` already uses
   (`src/Puck.World.Protocol/Protocol/WorldSessionMirror.cs:896`). One formula
   for the boot world and every session view.
5. **One console drain, on the frame thread, between join and dispatch.**
   Every verb runs where it runs today, with the server idle. No routing axis
   is added and no verb is reclassified; `TextCommandSource` stays the one
   single-threaded arbiter it is (`src/Puck.Commands/TextCommandSource.cs:21-45`).
   The drain is skipped in an iteration whose body has not joined (the server
   is busy), so a console line waits at most a late tick. Overturned:
   `CommandRouting.Presentation` and a second, filtered drain (F08, F09).
6. **One step per dispatch.** The pump decides a step as now (accumulator,
   `mayStep`, `HoldsClock`, rate), snapshots and applies its commands on the
   frame thread, and dispatches exactly one body. Catch-up after a slow frame
   proceeds one step per iteration against the accumulator the runaway clamp
   already bounds (`src/Puck.Launcher/FixedStepPump.cs:203-210`). Only a
   replay drive bursts inside a body, to a budget the frame thread computes
   before dispatch from the next tick barrier (section 2).
7. **Lockstep, offscreen and unpaced headless dispatch and join in the same
   iteration**, through the same code: the join is immediate. The record path
   therefore runs in `puck test --reproduce` too, which is what proves it a
   pure relay. There is no pump handoff between threads and no parked state.
8. **While a body is in flight the frame reads no server state**, which a
   debug guard on the server's frame-facing seams enforces (section 1). What
   the frame needs from the server during produce arrives in the record or is
   copied at the join.
9. **The three host loops converge into one before any thread is added** (the
   owner's duplication ruling, AGENTS.md); the thread is built once, on the one
   loop, with the one drain operation at both of its scheduling points (F16).

## 1. Thread ownership

### Today

Every boot shape drives one pump on one thread. The windowed loop
(`src/Puck.Launcher/LauncherWindowHostedService.cs:395-720`) polls the window,
captures input (`:460-568`), drains the console (`:577`), runs
`FixedStepPump.Advance` or the lockstep `TryStep`/`Hold`, builds a
`FrameContext` from the pump's accumulator and produces the frame. The headless
and offscreen loops (`HeadlessTickHostedService.cs`,
`OffscreenTickHostedService.cs:221-260`) repeat the drain, the flush, the exit
deadline, the snapshot-input captures and the step with their own pacing.
`WorldHostStep.Step` (`src/Puck.World/WorldHostStep.cs:107-159`) is the one
step sequence, wrapped twice (`WorldSimulation.cs:16`,
`HeadlessWorldSimulation.cs:17`), which differ only in their post-step.

Inside the step, the server delivers the tick's snapshot synchronously to every
typed subscriber (`WorldOutputHub.cs:315`) from one borrowed scratch array the
next tick overwrites (`WorldDocument.Apply.cs:900-905`), and a state delivery's
`MovedRows` alias the server's buffer until `ForgetMovedRows`
(`WorldServer.StateStamp.cs:15`, `WorldStateStamp.cs:12-13`). The boot world has
two local subscribers: the presentation `WorldClient` (bound at
`WorldBootComposition.cs:329`) and the boot authority endpoint's own
`WorldSessionMirror` (`WorldAuthorityEndpoint.cs:121-125`), which the seat
intent projection reads (`WorldClient.cs:406-426`, `:1158-1236`).

### The step, split in three

`WorldHostStep.Step` becomes three calls in today's order, the middle one
dispatched:

| Part | Thread | Runs |
|---|---|---|
| Prologue | Frame | `sourceWatch.Poll`; `DeliverRouteChanges`; `ShouldStepBoot`; `PrepareBootSeatIntents` (the client's `SubmitAuthorityIntents`, `ComposeMoveFrame`, the seat view's yaw); `DrainPendingTransfers`; `DeliverRouteChanges`; `SubmitExternallyClockedSeatIntents` (`WorldHostStep.cs:108-139`). |
| Body | Worker | `StepBoot` (`WorldServerStepShell.Step` with its peer drain, authority gate, `Advance`, tape close, and the replay burst to its budget; `ScanBootBoundaryTriggers`; `SettleBootScreenSessions`) and `StepInstances` (`:140-151`). The shell records each completed tick's `(hostTick, serverTick)` pair in the record instead of calling `publishTick`. |
| Epilogue | Frame, at the join | Replay the record into the local sinks; replay the recorded echo-tap effects; `DeliverRouteChanges`; `PublishSeats` (the seat bindings' `SyncRoute`, which now reads a current mirror); `FinishSeatIntents`; the publish hooks per completed tick in order (`waitGate`, `captureScheduler`, `tickScript`, `scheduleRunner`, `WorldHostStep.cs:26-31`); `extensions.Pump`. |

Nothing in the prologue or the epilogue changes thread, and nothing in the body
touches a client, a seat, the router, a session or the renderer. The split
is the whole of the ownership change.

### After

| Thread | Owns and touches |
|---|---|
| Frame | The window and swap chain; input capture into the router, `ReleaseHeld` included (`LauncherWindowHostedService.cs:480`, `InputRouter.cs:2060`); the pump, `SnapshotForTick` and `ApplySnapshot`; the one console drain and every verb; `WorldClient` whole; `WorldSeatBindings`, `WorldSeatViewState` and its `Nudge`; the capture scheduler (arming, `HoldsClock`, publication, serving), `WorldTickScript`, `WorldConsoleWaitGate`, `WorldTestRun`; `WorldFramePresenter`, residencies, uploads, present, `WorldScreenBinder`; the audio director's frame feed; overlay and HUD; `HostFrameTimeline`; the record's read side; machine and instance retirement; `SettleOwedFrames` at shutdown. |
| Worker (between dispatch and join only) | `WorldServer.Advance` and the arena; `WorldInstanceHost.StepInstances`; `WorldReplayTape` taps and `NoteTick` (`WorldReplayTape.cs:64-66` already assumes one thread at a time); `WorldPeerHost.DrainPending`; `WorldMachineHost.Advance` and queued segment submission; `WorldAdjacencyFields.BeginTick`; the hub's delivery into the record; the server's `EchoTap` into the record. |
| Already their own | The audio render thread (`WorldAudioRenderService.cs:179`); queued machine workers (`QueuedMachineHost.cs:11`); socket readers and the accept loop, which reach the server only through `ExecuteAuthorityOperation` (`WorldServer.cs:465-476`, contending with the body's gate as they contend with the step today) and the body's drain; extension workers; the hot-reload agent; the code warmer. |

The worker is one dedicated thread (`LauncherHostLoop.StartBackgroundThread`),
the same thread for every body, woken by a dispatch event and signalling a
done event; never the thread pool. The server's and instance host's
thread-affine scratch (the hub's borrowed arrays, the snapshot cells) is then
always touched from one thread.

### What the frame reads from the server during produce

Produce overlaps the body, so every server-side read on the produce path must
go through the record, a join-time copy, or a reference-swapped record. Found
by walking the frame path; the guard below finds the rest.

| Read today | Where | After |
|---|---|---|
| `IWorldSimulationClock.Tick` = `server.NextInputTick - 1` | `WorldFramePresenter.cs` (five sites), `.Convergence.cs:117` | `m_client.Tick`; delete the interface (decision 4; phase 1). |
| `m_server.NextInputTick` | `WorldOverlayFacts.cs:26,64` | `client.Tick`/`EngineTick` (phase 1). |
| `FrameContext.InterpolationAlpha` from the accumulator | `LauncherWindowHostedService.cs`, `SdfWorldResidency.cs:817-822` | From the applied record's join timestamp (decision 4); `FrameContext` keeps its shape. |
| `IWorldAdjacencySource` tick-side state written in `BeginTick`, read in `Pin` | `WorldAdjacencyFields.cs:290`, `:669-700`; `WorldFramePresenter.cs:1140-1148` | `BeginTick` builds an immutable tick record (projections, frame, field, reason) and publishes it by one reference write; `Pin` reads it once. The pose copy already goes through the mirror's gated `CopySnapshotTo` and is coherent. |
| Machine video outputs and instance servers resolved live per frame | `WorldScreenBinder.Device.Sources.cs:27-42` (`MachinesOf(world)?.VideoOutput`), `WorldScreenBinder.Sources.cs:56`; `WorldMachineHost.Instances.cs:50-61` reads `m_instances`, which `CommitInstances` (`:996-1010`) replaces | The binder resolves its source table at the join (frame thread, server idle) and holds it until the next join. A machine plan's commit happens in the body; its disposals (retired runtimes and leases, `:1059`, `:1104`) are queued and run at the *following* join, after the binder re-resolved. An instance stop's `Dispose` (`WorldInstanceHost.cs:1405-1419`) is likewise deferred to the join. |
| The server's `EchoTap` runs presentation callbacks inside the step | `WorldPostBuildWiring.cs:302-345` (`editorSeats.Reconcile`, `graphHost.Rebase`, `definitionSource.SourcePath`, `deferredVerbAnswers.Answer`, `sourceWatch.NoteResult`, toasts and cues) | The tap records each `WorldEditEcho` into the record; the epilogue invokes today's tap body per echo, in order, on the frame thread. The tap's body does not change. |
| `WorldCaptureScheduler.PublishTick` arms the renderer inside the step | `WorldCaptureScheduler.cs:426-532`, `:249-277`; `WorldHostStep.cs:28` | Runs in the epilogue, after the record applied, with the server at the completed tick (barriers below). Its state hash (`:282-285`, `:433`) is computed with the server idle. |
| `HostFrameTimeline` written and read on one thread | `HostFrameTimeline.cs:24-28` | Still one thread: the body returns its step cost in the record; the timeline's tick lane is written at the join. |
| `WorldSessionMirror` of the boot endpoint delivered in place, read per property | `WorldSessionMirror.cs:533-594`; `WorldSessionSceneEmitter.cs:693-705` | Delivered at the join, on the frame thread, so no write overlaps a frame's reads. A *remote* mirror's per-property reads against its socket thread are a pre-existing hazard, filed in the packet, not this design's. |
| `server.Definition`, `server.Grants`, `server.Population`, `server.Machines` reads during produce | Audit | Every such read either moves to the prologue or epilogue, or copies at the join; the guard finds the ones the walk missed. |

Rows of the first revision's table that needed no work once the client's seat
half stays on the frame thread: `ActiveLayoutName`, the perception anchor,
the overlay facts' router tables, `WorldStateMirror`, the capture scheduler's
fields, `WorldClient`'s file split, `TickLocked`, `WorldSeatViewState`.

**The guard.** `WorldServer` and `WorldInstanceHost` gain a `StepInFlight`
flag the loop sets at dispatch and clears at the join, and a
`[Conditional("DEBUG")]` `AssertFrameMayRead()` on their frame-facing seams
(`Definition`, `Grants`, `Population`, `Machines`, `NextInputTick`,
`CompletedEngineTicks`, `Body`, `TryGet`/`Boot`, `VideoOutput`), which throws
naming the seam when called off the worker while a body is in flight.
`ExecuteAuthorityOperation` is exempt (it takes the gate). Phase 3 arms the
guard in *rehearsal*: the loop sets the flag around produce with no thread
running, so a sweep of the debug world throws at every read the audit missed
before any thread exists.

Hot reload needs no new seam: `CodeRevision.Applied` fires on the agent's
thread and every cache compares `Current` on its own thread
(`CodeRevision.cs:29-34`). `CodeDerivedTable` has a separate, pre-existing
defect (F17) fixed in phase 2. Audio needs none: the server's music taps fire
from the body into a director that gates every entry
(`WorldAudioDirector.cs:180-205`) and feeds its own render thread.

## 2. The handoff

### The iteration

```
pacer wait → poll window, capture input → JOIN (if the body is done:
apply the record, run the epilogue) → drain (skipped while a body is in
flight) → flush → pump: gate; if a step is due: SnapshotForTick,
ApplySnapshot, prologue, DISPATCH → produce (from the applied state)
→ present → next iteration
```

Under `StepsOneTickPerFrame` and unpaced headless the dispatch is followed by
an immediate join inside the pump's step, so `TryStep` keeps its meaning (one
step, its record applied, before the host produces the frame that shows it).
`HostPacing` (`src/Puck.Hosting/HostPacing.cs`) grows from two values to the
policy: wall clock overlapped, wall clock inline, one tick per frame, unpaced;
the overlap applies only when the step period is at least the frame period,
and a world whose rate exceeds the display's steps inline, as today.

### The record

One `WorldTickRecord`, owned by the recording sink, reused every step, grown
once to its high-water mark:

- Entries in delivery order, each one of: `Snapshot` (the copied
  `WorldSnapshot`: entries, `State` longs sliced from the record's own
  `long[]`, `FieldCells`, `Inputs`), `Definition` (reference and version),
  `State` (definition reference, version, and the stamp with `MovedRows`
  copied into the record's `int[]`, F02), `Composition`, `SessionLever`,
  `Answer`, `Echo` (a `WorldEditEcho` for the tap).
- The completed ticks: a list of `(hostTick, serverTick)` for the publish
  hooks, one per tick the body completed (several under a replay burst).
- The body's cost: start and end timestamps, and `StepTicks`.

The recording sink subscribes to the hub with full disclosure in place of the
two local sinks, which subscribe to it. It *records* between dispatch and
join and *passes through* otherwise, so the attach primer
(`WorldDocument.Delivery.cs:59-123`), a checkpoint restore delivered outside
the tick, and every delivery an Immediate verb causes in the drain reach the
sinks synchronously on the frame thread, as today. Socket and federation
sinks stay directly on the hub: they serialize to their own writers and are
already cross-thread.

`Replay` calls each local sink's `IClientSink` method per entry, in order;
`WorldClient`'s code does not change. Every entry is applied: there is no
fold and no loss, because nothing can be superseded between a join and the
next dispatch (F03, F04). The record's memory is bounded by one body's
deliveries: one tick in steady state, the replay burst's budget at most.

### Alpha

`alpha = clamp((now - joinedAt) / stepPeriod, 0, 1)` (decision 4), pinned to
one under lockstep, offscreen and a converging capture, as `PinsPresentation`
pins it today (`WorldFramePresenter.Convergence.cs:74`). `RenderTicks` becomes
`ElapsedTicks + alpha * StepTicks`, rounded down. The arrival form is smooth
where the accumulator form would jump by the body's latency at every join.

### Latency

A tick's visible effect lands one iteration later than today: dispatched after
present, applied at the next join, drawn by that iteration's produce. At 60 Hz
that is about one frame period more input-to-photon than today, and about
8 ms more than a free-running simulation thread would give, which is the price
of keeping every client object single-owner. The owner sees this trade under
Needs you in the packet.

Phase 1's arrival-form fraction already adds about half a step of presentation
latency. In the inline host the arrival time is stamped inside the step and the
frame is produced right after it, so the fraction is about 0 on every frame that
steps, where the accumulator form averaged about 0.5. A 60 Hz world on a 60 Hz
display presents poses about 8 ms later on average than before phase 1, before
any of the thread's own latency.

### Barriers and the replay budget

A body steps one tick, except a replay drive, whose shell burst
(`WorldServerStepShell.cs:45-78`, `WorldReplayTape.Drive.cs:65-77`) steps up
to a *budget* the frame thread computes before dispatch:
`min(FastForwardBurst, nextBarrier - completedTick)`, where `nextBarrier` is
the least of the publish hooks' next due ticks: the tick script's earliest
pinned line, the wait gate's release tick, the schedule runner's next due
step, the capture schedule's next row tick. Each hook owner exposes
`NextDueTick`. The shell takes the budget as a parameter beside the pacing
policy (phase 1 already threads the policy through it, F14) and returns the
ticks it completed; the epilogue runs the hooks per tick in order, so a
pinned line still runs between its tick and the next, and a capture still
arms with the server at exactly its tick. An unscheduled capture
(`world.screenshot`) and `world.rate pause` are drain verbs and take effect
at the next dispatch, as today.

### Holds

A capture armed at tick N owes a frame of tick N before N+1 steps. The pump
asks `HoldsClock` before each step on the frame thread, which is also the
thread that presents, so a hold is the frame thread declining to dispatch
until it has presented a frame whose applied tick is N. No thread waits on
another; the hold budget is spent in host time as now
(`WorldCaptureScheduler.cs:345-364`, `FixedStepPump.cs:305-311`), and
`AwaitsFrame` ends the pump's call as now (F10, F15).

### When the body is late

A body still running at the join leaves the frame on the last applied tick
with alpha at one, skips the drain, and is counted (`steps.late`). The pump
does not dispatch another body until the join, so the accumulator grows and
the runaway clamp (`FixedStepPump.cs:203-210`) sheds what one frame cannot
owe; the shed count is a counter (`steps.shed`, phase 1). Device-loss
recovery on the frame thread therefore pauses the simulation as it does
today, with nothing folded.

### Garbage collections

Workstation concurrent GC suspends every managed thread for a pause, so a
10 to 12 ms gen0 lands on both threads at once; nothing here changes that.
Neither thread holds anything the other waits on across a pause: the join is
a done-event read the frame never blocks on, and the only lock across a body
is the authority gate the step already holds (`WorldServerStepShell.cs:49-57`).

## 3. Determinism

What a tick computes depends on its `CommandSnapshot` and the server state;
neither changes. `SnapshotForTick`, `ApplySnapshot` and the prologue run on
the frame thread immediately before the dispatch, in today's order
(`FixedStepPump.cs:332-360`); the body runs the step; the tape's taps and
`NoteTick` stay inside the body on one thread (`WorldReplayTape.cs:64-66`).
State hashes do not move: the same document and the same captured input give
the same snapshots and the same steps. A live run's wall-clock arrival still
decides which tick a physical press lands in, exactly as today, because
capture, `ReleaseHeld`, the seat view's `Nudge` and the snapshot all stay on
the frame thread, in today's order (F06, F07).

One-tick-per-frame shapes:

- `puck test --reproduce` boots `--headless true --unpaced true`
  (`TestCommand.Leg.cs:111-112`): one `TryStep` per iteration, dispatch and
  immediate join, the record replayed before the next step. Hashes identical
  to today is the check that the record is a pure relay.
- `world.lockstep`, offscreen and the sweep's tours: one tick per produced
  frame, immediate join, alpha pinned to one.
- `world.at` lines run in the epilogue's tick script hook, between the tick
  they name and the next, because the budget never steps past a pinned tick.

## 4. Commands

- **Simulation-routed verbs** are injected into the router
  (`CommandRegistry.cs:1738`) and fold into the next tick's snapshot;
  unchanged. A bound Simulation verb whose handler touches presentation
  (`view.override`, `WorldViewCommandModule.cs:277-294`) runs in
  `ApplySnapshot` on the frame thread, as today (F08).
- **Immediate verbs** run in the one drain, on the frame thread, with the
  server idle; the read-after-write barrier (`TextCommandSource.cs:49-163`)
  is unchanged. A verb reaching the server while a body is in flight is
  impossible by construction: the drain does not run then.
- **Host operations** (`ConsoleControlSession.InvokeAsync`, the MCP capture
  at `ConsoleControlSession.cs:104-121`) run in the same drain, on the frame
  thread, ordered behind the session's prior submissions as today.
- **`world.reload` and live edits**: `WorldSourceWatch.Poll` runs in the
  prologue and submits the ordinary reload line; the swap applies in the
  body at the tick boundary and reaches the frame as a `Definition` entry
  followed by the tap's `Echo` entry, so the frame installs the document and
  then reconciles the editor and rebases the graph host, in that order
  (F11).
- **`world.save`** runs in the drain: `WorldSaveSnapshot.Compose`
  (`WorldSaveSnapshot.cs:21-35`) reads the server and folds the presentation
  levers on one thread with no body in flight, so the saved generations are
  coherent (F11).
- **Socket, federation and extension work** enters through
  `ExecuteAuthorityOperation` or the body's peer drain, as now.
- **Hot reload** needs no marshalling (section 1).

## 5. Shutdown, exceptions, diagnostics

**Shutdown.** The host cancels; the loop finishes its iteration: it joins the
in-flight body with a bound (30 s, enough for a replay burst), applies the
record, runs the epilogue, calls `SettleOwedFrames` (owed captures refused by
name), stops the worker thread, then disposes the render root and the
instances. A body that faults completes the done event with its exception;
the join rethrows it on the frame thread, which faults the hosted service and
stops the host with the exception in hand, as a loop fault does today
(`LauncherHostLoop.cs:85-102`). A join that times out means a hung step: the
loop fails the process fast with the tick and the elapsed time, rather than
disposing the server underneath a live worker (F15). Pending host operations
and captures settle with that error on either abort.

**Device loss** is recovered on the frame thread (`LauncherWindowHostedService.cs:700-740`)
with no dispatch until it ends; the simulation pauses as today.

**Diagnostics.** `HostFrameTimeline` gains a `Join` phase (record replay and
epilogue) beside the frame phases, and a tick lane written at the join: the
body's cost, late joins, steps the pump shed. `host.frames` prints the frame
iterations with `simulate` read from the tick lane, the `join` phase's avg,
p50, p99 and max, and the counters `steps.shed`, `steps.late`, `drain.skipped`.
`world.rate` adds `shed` to its read-back. Counters are `Interlocked`; the
timeline is written on one thread.

## 6. Duplicated mechanisms on this path

Named under the owner's duplication ruling; the plan converges each on one
rather than building the thread beside them.

1. **Three host loops** (`LauncherWindowHostedService.cs`,
   `HeadlessTickHostedService.cs`, `OffscreenTickHostedService.cs`), the
   windowed loop's lockstep branch being the offscreen body a fourth time.
   One loop with a pacing policy (`HostPacing`).
2. **Two step shells** over `WorldHostStep` (`WorldSimulation.cs:16`,
   `HeadlessWorldSimulation.cs:17`), differing in `afterInstances` only. One.
3. **Two console drain sites per windowed iteration**: the loop's own
   `Collect` (`LauncherWindowHostedService.cs:577`) and the pump's
   `beforeStep` (`FixedStepPump.cs:150-154`). They serve two scheduling
   points, not one: the pump's runs only on a step path, and `Hold`
   (`:305`) and a render host with no simulation (`:139-144`) never reach
   it. One drain *operation*, kept at both the between-step and the
   held/no-step points (F16).
4. **Three simulation clocks the frame reads**: `IWorldSimulationClock.Tick`,
   `WorldClient.Tick`, `WorldOverlayFacts.CompletedTick`. One: the client's.
5. **Two interpolation fractions**: the accumulator's
   (`FrameContext.cs:52`) and the arrival form
   (`WorldSessionMirror.cs:896`), with a pinned copy in
   `WorldAdjacencyFields.cs`. One: the arrival form.
6. **Two mirrors of the boot world's snapshot**, both hub subscribers:
   `WorldClient` and the boot endpoint's `WorldSessionMirror`. Both now
   receive through the one record; folding them is larger than this packet
   and is filed in the [refactoring campaign](open-items.md#refactoring-campaign).
7. **Three per-sink redaction scratch buffers** (`WorldOutputHub.cs:71`,
   `WorldSessionSink.cs:29`, `WorldFederationProjectionSink.cs:42`). Filed
   with the above; the record adds none (it copies the delivered, already
   redacted snapshot).

## 7. Phased build

Each phase is a branch that builds and runs on its own. Finish lines name
what to run; test requirements are lifted (AGENTS.md), so each phase is
verified by building and running what it touched, plus the one check that
would catch its main risk.

### Phase 1: one host loop, no thread (delivered)

**Delivered by `sim-phase1`.** `Puck.Launcher.HostLoop` owns the iteration,
input contributions, console scheduling, stepping, holds, deadlines and pacing.
The three hosted services only start it with their presentation adapter;
`WindowHostPresentation` owns window input, rendering and display waits, and
`OffscreenHostPresentation` owns offscreen rendering. All simulation still
runs inline on the existing host pump thread; this phase starts no simulation
thread, and phase 4 adds the pacing it needs.

`WorldSimulation` is the single shell over `WorldHostStep`, with its post-step
supplied by composition. Frames and overlay facts read `WorldClient.Tick`;
`WorldRenderRoot` fills the graph's `FrameContext` from the client's delivered
engine time, step width and arrival fraction. The client, session views and
pinned adjacency views all use `WorldSessionMirror.ResolveInterpolationAlpha`.
Lockstep and settled captures pin that fraction to one.

Two corrections to the proposed convergence are part of delivery:

- F16: `HostConsoleDrain.Collect` is the shared operation. The pump's
  `BeginIteration` runs it even without a due tick or during a held frame;
  the first step reuses that drain and subsequent catch-up steps drain before
  each snapshot. A host with no pump calls the same operation each iteration.
  Ready immediate commands, including quit, recovery and presentation controls,
  can progress during a capture hold. Simulation submissions wait for a tick;
  session waits and read-after-write barriers continue to preserve each
  session's FIFO, so a blocked session does not acquire an out-of-order quit.
- F14: window composition registers the live `HostPacing` owned by
  `PresentPacingControl`. `world.lockstep` retargets that same object, which
  `WorldHostStep` and `WorldInstanceHost` pass to `WorldServerStepShell`.
  Replay fast-forward therefore runs one authority tick per outer step under
  runtime lockstep, instead of retaining an independent wall-clock policy.

`HostFrameTimeline` retains the frame's `simulate` phase and adds an independent
per-step cost ring (count, avg, p50, p99, max). `steps.shed` is an atomic,
host-lifetime counter of whole steps discarded when a catch-up burst spends
its wall-time budget; holds and the runaway-frame clamp are distinct and are
not counted as shed. `host.frames`, `world.rate` and `world.rate shed` report
it. Resetting the frame sample window does not reset that counter. The join
phase, late-step and skipped-drain metrics remain for the phases that introduce
the tick record and worker.

Verification and windowed measurements are recorded in the delivery commits.
The four focused debug sources are `controls-dash-tests.puck`,
`input-clock30-tests.puck`, `relative-input-tests.puck` and `noclip-tests.puck`:
nine scenarios reproduce, with byte-identical authority hash sequences and
canonical state exports before and after. The standard package has no
`*-tests.puck` source. A headless stdin script lands movement at ticks 10 and
30 and reads tick 31 hash `d8484552ca622a93`, then tick 32 hash
`b30470686bd27afd`, before and after.

The merged-head headless sweep returned 0: 19 clean signals, no red or
unmeasured signals, and the same two collider/contact coverage ambers as the
earlier run. The focused launcher checks cover a real offscreen adapter
holding tick 1 on an unrenderable frame, a render host without a simulation,
between-step input/console ordering and device recovery. A live replay fork
publishes one authority tick with lockstep on, then publishes the remaining
three in order with it off; every driven tick matches the recording.

The granted Direct3D windowed run completed under the GPU lease, with devices
disabled and bakes settled before sampling. Standing: 58 iterations, frame
avg/p99/max **69.47/205.25/205.25 ms**, simulate avg/p99
**45.03/168.53 ms**, tick avg/p50/p99/max **45.93/11.05/168.52/168.52 ms**.
Walking for exactly 60 ticks (movement armed at 144, stopped at 204): 94
iterations, frame avg/p99/max **25.46/207.07/207.07 ms**, simulate avg/p99
**10.26/189.91 ms**, tick avg/p50/p99/max **13.73/10.37/171.57/171.57 ms**.
Both reports retained `simulate`, the tick lane and `steps.shed`;
`world.rate` and `world.rate shed` read the same final **325** shed steps.
During a capture hold, `world.state.hash` and `host.frames` answered at tick
225 before the capture settled; its still stamp reported `tick=225`,
`shown=225`, state hash `f99b7a4e8581f128`. The World quit normally.

The frame-rate bar is **not met** by this observation. It was not a paired,
quiet before/after measurement, and the main mesh-GI job completed only later
in the run; neither an improvement nor a regression is established. The raw
stdin scripts, exports, replay hashes, sweep report, windowed log and still
are kept under the lane's `.claude/scratch/sim-phase1-*` paths. The one GPU
leg did not include a pre-phase still or a Vulkan boot; pixel parity remains
unconfirmed. The later phases retain their paired-still and frame-budget
finish lines.

The sweep and windowed observation above were recorded at `02e6bd3cde`.
The final integration merge includes `lead/fundamentals` at `5ce882b902`,
including its later mesh-bake changes. On that combined head, Release builds
of World and CLI passed with zero warnings/errors and the rebuilt private CLI
reproduced `input-clock30-tests.puck`; both its authority-hash sequence and
canonical export still match the pre-phase artifact byte for byte. The GPU
leg was not repeated after that later rendering merge. The integration lead
owns the current-tip windowed qualification and the cross-family code review.

Original scope and finish:

Converge duplicates 1 to 5; keep one drain operation at both scheduling
points (F16); carry one pacing policy through `WorldServerStepShell` and the
replay fast-forward (F14's first half). Files: the three hosted services
(become one `HostLoop` plus a thin hosted service per registration),
`LauncherHostLoop.cs`, `HostPacing.cs`, `WorldSimulation.cs`,
`HeadlessWorldSimulation.cs` (deleted), `WorldHostStep.cs`,
`IWorldSimulationClock.cs` (deleted), `WorldFramePresenter*.cs`,
`WorldOverlayFacts.cs`, `WorldBootComposition.cs`, `HostFrameTimeline.cs`
(tick lane and `steps.shed`), `WorldRateCommandModule.cs`.
Finish: a windowed debug World's `host.frames` standing and walking reads the
same phases as before with `simulate` intact; `puck test --reproduce` on
`worlds/debug` and the standard's test worlds matches the pre-phase hashes;
`puck sweep` headless green; a lockstep `world.at` still with identical
`[still]` hashes; a capture-held World and a render host with no simulation
both still answer `quit` and `world.status` over stdin (the F16 check).

### Phase 2: the record, still one thread

The recording sink between the hub and the two local sinks, recording between
dispatch and join and passing through otherwise; `MovedRows` copied (F02);
`WorldHostStep` split into prologue, body and epilogue with the publish hooks
and the echo tap's effects moved to the epilogue; the shell's tick budget and
`NextDueTick` on the four hook owners (F14's second half); the loop
dispatching and joining *inline* (an immediate join on the same thread).
Also here, independent of threading: `CodeDerivedTable` ties each
derivation to the generation it read, so a derivation that started under
revision R never enters the table published for R+1 (F17): `Refresh` returns
a generation object owning its own weak table, and `GetValue` inserts into
that generation only.
Files: a new `src/Puck.World.Client/WorldTickRecord.cs` (the sink, the record,
`Replay`), `WorldBootComposition.cs`, `WorldAuthorityEndpoint.cs`,
`WorldHostStep.cs`, `WorldServerStepShell.cs`, `WorldReplayTape.Drive.cs`,
`WorldPostBuildWiring.cs` (the tap records), `WorldCaptureScheduler.cs`,
`WorldTickScript.cs`, `WorldConsoleWaitGate.cs`, `WorldTestRun.cs`,
`src/Puck.Abstractions/CodeDerivedTable.cs`, the loop.
Finish: `puck test --reproduce` hashes unchanged (the record is a pure relay);
lockstep stills byte-identical to phase 1 but for the known residue; a
`world.at` line pinned inside what would have been a replay burst runs at its
tick (`[world.at] tick N ran`); a scheduled capture at a tick inside a replay
fast-forward arms with `tick=N` in its `[still]` stamp; a `world.reload` of a
document with a changed row shape, under a stdin script that edits a
selection, keeps the editor's selection consistent (the tap's effects applied
after the definition); a `CodeDerivedTable` test with a revision bump between
derive and insert reads the fresh derivation.

### Phase 3: the produce path reads no server state

Every row of section 1's table: the adjacency tick record; the binder's
join-time source table with deferred machine and instance retirement (F12);
the alpha from the join; the guard on the server's and instance host's
frame-facing seams, armed in rehearsal around produce. Still one thread.
Files: `WorldAdjacencyFields.cs`, `WorldContinuum.cs`,
`WorldScreenBinder*.cs`, `WorldMachineHost.Instances.cs`,
`WorldInstanceHost.cs`, `WorldServer.cs`, `WorldFramePresenter*.cs`,
`SdfWorldResidency.cs`, `FrameContext` filling in the loop.
Finish: a debug build of the windowed debug World under the rehearsal guard
runs the sweep's tour, a cabinet engaged and reset, a `world.instance.stop`
of a visible instance and a crossing into the garden and back with no guard
throw; a release build's `host.frames` phases unchanged from phase 2;
hashes unchanged.

### Phase 4: the worker

Withdrawn by the owner's direction: a pipelined worker costs a frame of
latency, and performance comes with quality, so the cost is removed at its
cause by the [engine job system](open-items.md#engine-job-system) (a
deterministic parallel tick and parallel view recording) instead. This phase
stays a fallback lever if parallel work cannot reach the frame-rate bar.


The dedicated worker thread; dispatch after present, join after input, the
frame never waiting; the drain skipped while in flight; the immediate join
under one-tick-per-frame and unpaced; shutdown's bounded join and the
fail-fast policy; the `Join` phase, `steps.late`, `drain.skipped`. Files:
the loop, `LauncherHostLoop.cs`, `FixedStepPump.cs`, `WorldTickRecord.cs`,
`HostFrameTimeline.cs`, `HostPacing.cs`.
Finish: a windowed debug World (Direct3D 12, 1280x900, `world.quality high`,
`world.lockstep off`) standing at the lobby and walking the gallery
(`body.pose 30 - 22 90 - -`, `player.move 1 0` for 60 ticks) reads
`host.frames` with: total frame p99 and max lower than phase 3's by at least
the tick lane's p50; the `join` phase p99 under 2 ms; `steps.late` zero
standing and under 1 % walking; the tick lane's p99 within 1 ms of a headless
`host.frames` at the same stop. Then `world.lockstep on` and a `world.at`
still matching the phase 1 still; `puck test --reproduce` unchanged; a stdin
script with `world.wait`, `world.at`, `world.screenshot` and `quit` ending
cleanly with `--exit-after-seconds`; the MCP `puck_exec` script path answering
every line, a pinned capture included; a shutdown issued while a body is in
flight and one while a capture holds, both exiting with the owed capture
refused by name.

### Phase 5: hardening

A windowed Vulkan boot of the same stops (the owner's ruling on Vulkan);
`host.frames` under a forced gen2 (`host.memory collect`) showing neither
thread waiting on the other beyond the pause; cabinets engaged, removed,
replaced and their instance stopped while visible on a screen; hot reload
under `dotnet watch` with a body edit on the presenter and on a rule host; a
crossing into the garden and back; a capture whose frame faults (a target
that throws) refused and the World continuing; a replay drive fast-forwarding
under `world.lockstep on` and off with a capture row inside the tape; a
`world.reload` while the frame is mid-produce on a slow frame
(`world.quality` forced high, large window). Each is a run that is read, not
a test.

### The riskiest phase, and what would end the approach

Phase 3 carries the correctness risk: a server read on the produce path the
audit missed. The rehearsal guard is built for exactly that, and it runs
before any thread exists. Phase 4 carries the payoff risk: the join (record
replay plus epilogue) is work moved onto the frame, and the one-step-per-
dispatch rule couples the simulation's catch-up to the frame rate.

The approach is abandoned if, after phase 4, either holds on the walking
workload above: the total frame p99 did not fall by at least the tick lane's
p50 (the overlap did not remove the tick from the frame), or the `join`
phase's p99 exceeds 2 ms once the frame-time packet's delivery work is
subtracted (the record's replay is itself a frame cost the split cannot
hide). The tick lane's p99 against headless is the second reading, the gate
cost on the worker, not the criterion. The measurements are the finish
line's `host.frames`, phase 3 beside phase 4, on a quiet machine.

If it is abandoned, the fallback is already in hand: phases 1 to 3 stand on
their own (one loop, a pure-relay record, a produce path that reads no server
state), the tick-cost lane keeps cutting the body, and the remaining frame
work is the [frame time](open-items.md#frame-time-on-the-lead-machine)
packet's.

## 8. What this cannot fix

Costs that stay on the frame thread, with the packet that owns each:

- Produce: 6 to 7 ms on Direct3D 12, 8 to 11 ms on Vulkan
  ([frame time](open-items.md#frame-time-on-the-lead-machine)).
- View recording on tick frames (`ShaderPipelineRenderNode.RenderFrame`
  2.8 ms against 0.9 between), now on the join's frame rather than the
  step's; owned by frame time and the look call on rendering portal and
  session views a frame late in [owner decisions](open-items.md#owner-decisions).
- The record's replay into the two local sinks: the delivery share of today's
  tick (about 17 % of it), which the join now pays. Folding the two mirrors
  (section 6, item 6) halves it.
- First-visit bursts, GC pauses, irradiance publication and the far cascade,
  as before.
- The body's own cost (rule evaluation 44 %, the scoped replay hash 10 %)
  moves off the frame but still bounds how far behind real time a slow
  machine runs, and under one step per dispatch a frame rate below the tick
  rate slows the World.
- One iteration of added presentation latency for a tick's visible effect
  (section 2).

## Review resolution

The one cross-family review (Codex Astra, 12 high, 6 medium) against the
first revision. Each finding was re-verified against the code before its
resolution was chosen; file and line are the citation that holds.

| Finding | Verified | Resolution |
|---|---|---|
| F01 high: publication does not establish ownership or slot reclamation | Holds; the first revision specified none. | Removed by construction: the record's writer runs only between dispatch and join and its reader only between join and dispatch, so no slot is ever read and written at once; no ring, no fold (decision 3). |
| F02 high: `WorldStateStamp.MovedRows` is borrowed | Holds (`WorldStateStamp.cs:12-13`, `WorldServer.StateStamp.cs:15`). | The record copies `MovedRows` into its own `int[]`, as it copies `EntitySnapshot.State`; every nested memory in a delivery is copied (phase 2). |
| F03 high: the fold loses document cells and crosses schema barriers | Holds (`WorldDocument.Apply.cs:965,996,1006`: document cells publish independently of `FieldsFull`). | No fold exists: every entry is replayed in delivery order before the next dispatch (decision 3). |
| F04 medium: four slots bound neither payload nor allocation | Holds for a folding ring. | One record holding one body's deliveries, grown once to its high-water mark; a body is one tick or a budgeted replay burst, so the bound is the burst (phase 2's finish measures no steady-state allocation). |
| F05 high: seat binding and roster consumers defeat the client split | Holds (`WorldSeatBindings.cs:1523-1531` reads `client.StateMirrorFor`, the presentation mirror, in the post-step). | The client is not split: `PublishSeats` runs in the epilogue on the frame thread after the record applied, so the mirror it reads is current; the primer passes through the recording sink synchronously (decision 1, section 2). |
| F06 high: `ReleaseHeld` mutates the input fold outside the capture gate | Holds (`InputRouter.cs:2060-2078`, `InputRouter.Sustain.cs:117`). | `ReleaseHeld`, `Capture` and `SnapshotForTick` all stay on the frame thread in today's order; nothing of the router crosses a thread (decision 1). |
| F07 high: a gated camera yaw changes authoritative input by schedule | Holds (`WorldSeatViewState.cs:380-409`, `WorldClient.cs:424`). | `Nudge` and `ComposeMoveFrame` both stay on the frame thread; the prologue composes intents immediately before dispatch, so the yaw a tick sees is decided exactly as today (decision 1, section 3). |
| F08 high: filtering text collection does not route bound commands, nested scripts or host operations | Holds (`WorldTickScript.cs:249-261`, `WorldViewCommandModule.cs:281`, `ConsoleControlSession.cs:104`). | No routing axis and no second drain: every dispatch path runs on the frame thread with the server idle (decision 5, section 4). |
| F09 high: two filtered drains race session state; the barrier does not see frame application | Holds (`TextCommandSource.cs:21-45,71-124`). | One drain, one arbiter, on the frame thread; a query after a mutation runs after the join that applied the mutation's tick, because the drain never runs while a body is in flight (decision 5). |
| F10 high: capture arming is a cross-owner transaction | Holds (`WorldCaptureScheduler.cs:249-277,426-532`, arming the renderer from the step). | Arming, holding, publishing and serving all stay on the frame thread; `PublishTick` runs in the epilogue with the server at the completed tick; a hold is the frame thread declining to dispatch (section 2, Holds). |
| F11 high: reload callbacks bypass the sink; saves read presentation in reverse | Holds (`WorldPostBuildWiring.cs:302-345`, `WorldSaveSnapshot.cs:21-35`). | The echo tap records each echo into the record and the epilogue runs the tap's body in order after the definition; `world.save` runs in the drain with no body in flight (section 1 table, section 4). |
| F12 high: machine worker isolation does not protect frame-held video outputs or instance lifetimes | Holds (`WorldScreenBinder.Device.Sources.cs:27-42`, `WorldMachineHost.Instances.cs:50-61,996-1010`, `WorldInstanceHost.cs:1405-1419`). | The binder resolves its source table at the join; machine and instance disposals are deferred to the following join, after the binder re-resolved (section 1 table; phase 3 and phase 5 exercise removal, replacement and stop while visible). |
| F13 high: session mirrors are not coherent frame images; a swapped adjacency record can alias arrays | Holds for the mirror (`WorldSessionMirror.cs:533-594` in place; `WorldSessionSceneEmitter.cs:693-705` per property); half-holds for adjacency, whose `Pin` already copies through the gated `CopySnapshotTo` (`WorldAdjacencyFields.cs:676-693`). | The boot mirror is delivered at the join on the frame thread, so no write overlaps a frame's reads; adjacency's tick-side state becomes one immutable record swapped by reference. The remote mirror's socket-thread hazard is pre-existing and filed in the packet. |
| F14 medium: one outer `TryStep` can advance many replay ticks | Holds (`WorldServerStepShell.cs:65-71`, `WorldReplayTape.Drive.cs:65-77`). | Phase 1 carries the pacing policy through the shell; phase 2 adds the tick budget from the hooks' `NextDueTick`, so a burst never steps past a pinned line, a wait release, a schedule step or a capture row. Check: phase 2's finish (`world.at` and a capture row inside a fast-forward). |
| F15 medium: parking needs timeout, fault wakeup and a disposal rule | Holds for a parked pump. | No parking exists; shutdown joins the body with a bound, a faulted body rethrows at the join, a hung body fails the process fast rather than disposing under it (section 5). Check: phase 4's two shutdown runs. |
| F16 medium: removing the outer drain breaks held and no-simulation loops | Holds (`FixedStepPump.cs:139-144,305-330`, `OffscreenTickHostedService.cs:222,252`). | Folded into phase 1: one drain operation at both scheduling points, with the capture-held and no-simulation runs as its check (section 6, item 3). |
| F17 medium: `CodeDerivedTable` can retain a stale derivation under the new revision | Holds (`CodeDerivedTable.cs:30-41,66-73`): a derivation begun under R inserts after a `Clear` for R+1. | Pre-existing; fixed in phase 2 by a generation object owning its own table, with a test bumping the revision between derive and insert (the one check running cannot show). |
| F18 medium: the finish lines can pass without a safe handoff or the frame-time goal | Holds. | Phase 4's finish reads total frame p99 and max, the `join` phase, `steps.late` and the tick lane; the abandonment criterion is the total-frame improvement and the join cost, not the tick lane alone; phase 3's rehearsal guard and phase 5's fault runs are the adversarial interleavings (section 7). |
