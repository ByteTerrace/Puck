# The simulation thread

Design memo for the [simulation tick off the frame thread](open-items.md#simulation-tick-off-the-frame-thread)
packet, which the owner queued. It settles the shape from the code and hands
specialist lanes a phased build. The pre-phase file and line citations below
refer to commit `8075db0d56`; Phase 1's delivery notes name the current seams.
A builder re-reads the cited source before editing. Phase 1 rechecked the
citations against that source, including the full cited ranges, rather than
treating their line numbers as current after the moves.

The goal is the [frame-rate bar](../decisions/rendering.md#the-frame-rate-bar):
on the RTX 4070 every frame under 16.7 ms at the highest settings. Today a
windowed iteration that carries a 30 Hz tick pays the tick (6 to 12 ms) and the
frame's produce (6 to 7 ms on Direct3D 12) on one thread, so one iteration in
two or three misses the blank
([frame time](open-items.md#frame-time-on-the-lead-machine)).

## Decisions

1. **One simulation thread owns the pump and everything the pump steps.** The
   lead's shape holds: `FixedStepPump`, `WorldHostStep`, `WorldServer`,
   `WorldInstanceHost`, the replay tape, the console drain, the capture
   scheduler's arming and every machine's segment submission move to a thread
   of their own, started by the same `LauncherHostLoop.RunPump`
   (`src/Puck.Launcher/LauncherHostLoop.cs:84`) the host loops already run on.
2. **The handoff is the existing client sink, not a new snapshot ring.** The
   frame thread's consumer is `WorldClient`, whose state is incremental
   (previous and current poses, correction easers, the state mirror's rows,
   `WorldClient.cs:883-1038`) and which receives six delivery kinds, not one.
   So `LoopbackTransport.Bind` (`src/Puck.World.Protocol/Protocol/LoopbackTransport.cs:214`)
   binds a mailbox: the simulation thread records each tick's deliveries into
   a slot of its own buffers, and the frame thread applies the pending slots,
   in order, into the real `WorldClient` at frame start. A snapshot a slow
   frame never saw is folded into the next with `EntityContinuity.Then`
   (`src/Puck.World.Protocol/Protocol/WorldSnapshot.cs:56`), the primitive the
   engine already has for "two pose changes before any client saw either".
   Overturned: a "latest-complete" read of immutable snapshots, which would
   drop definition and state deliveries that are not idempotent.
3. **The frame derives its clock and alpha from what it applied, never from
   the pump.** `WorldClient.Tick`/`EngineTick` (`WorldClient.cs:132-135`)
   replace `IWorldSimulationClock` and every `m_server.NextInputTick` read on
   the frame path; the interpolation fraction is the wall time since the
   applied tick's publication over its step period, the formula
   `WorldSessionMirror.ResolveInterpolationAlpha` already uses for session
   views (`src/Puck.World.Protocol/Protocol/WorldSessionMirror.cs:896`). One
   formula for the boot world and every session view.
4. **Input capture stays on the frame thread and changes nothing.** The
   router's two capture doors already take `m_captureGate`
   (`src/Puck.Commands/InputRouter.Capture.cs:176,229`) and the drain merges
   by `(captureTick, sequence)` under the same gate (`InputRouter.cs:2271`).
   The tick's inputs and their order are what they are today.
5. **The console drain runs between steps on the simulation thread**, where
   `world.at` and `world.wait` exactness live (`WorldTickScript.cs:10-15`).
   A verb that touches presentation declares `CommandRouting.Presentation`
   on the one routing axis the registry already dispatches by
   (`src/Puck.Commands/CommandRouting.cs:15`), and the frame drain runs it.
   This adds no second queue and no second dispatcher: `TextCommandSource`
   stays the one ingress (`TextCommandSource.cs:10-14`), stdin and the MCP
   control endpoint included (`src/Puck.World/WorldControlCommandModule.cs:10`).
6. **Offscreen, `world.lockstep` and unpaced headless step inline on the
   thread that produces frames**, through the same pump, mailbox and loop. The
   pump has one owner at any instant; the owner changes only at a tick
   boundary (the lockstep handoff below).
7. **Backpressure never blocks the simulation thread and never spins the
   frame thread.** A full mailbox folds (decision 2) and counts; a late
   simulation leaves the frame holding the last applied tick with alpha
   clamped at one. Shed steps are counted where the pump sheds them today.
8. **The three host loops converge into one before any thread is added**
   (the owner's duplication ruling, AGENTS.md). The lockstep branch inside the
   windowed loop is the offscreen loop written a second time; the thread is
   built once, on the one loop.

## 1. Thread ownership

### Today

Every boot shape drives one pump on one thread. The windowed loop
(`src/Puck.Launcher/LauncherWindowHostedService.cs:395-720`) polls the window,
captures input (`:452-567`), drains the console (`:574`), runs
`FixedStepPump.Advance` or the lockstep `TryStep`/`Hold` (`:600-618`), builds a
`FrameContext` from the pump's accumulator (`:671-682`) and produces the frame
(`:684`). The headless loop (`HeadlessTickHostedService.cs:151-230`) and the
offscreen loop (`OffscreenTickHostedService.cs:236-330`) repeat the drain, the
flush, the exit deadline, the snapshot-input captures and the step with their
own pacing. `WorldHostStep.Step` (`src/Puck.World/WorldHostStep.cs:107-159`) is
the one step sequence, wrapped twice: `WorldSimulation` (windowed,
`WorldSimulation.cs:16`) and `HeadlessWorldSimulation`
(`HeadlessWorldSimulation.cs:17`), which differ only in their post-step.

Inside the step, the server delivers the tick's snapshot synchronously to
every typed subscriber (`WorldTick.Step.cs:244-256`, `WorldOutputHub.cs:315`)
from one borrowed scratch array the next tick overwrites
(`WorldDocument.Apply.cs:900-905`). The boot world has two subscribers: the
presentation `WorldClient` (bound at `WorldBootComposition.cs:329`) and the boot
authority endpoint's own `WorldSessionMirror`
(`WorldAuthorityEndpoint.cs:121-125`), which the seat intent projection reads
(`WorldClient.cs:1158-1236`, `:421-426` for the camera-frame yaw).

### After

| Thread | Owns and touches |
|---|---|
| Simulation | `FixedStepPump`; `WorldHostStep`; `WorldServer` and the arena; `WorldInstanceHost` and every instance; `WorldReplayTape` (`WorldReplayTape.cs:64-66` already assumes one thread); `TextCommandSource.Collect` and every `Immediate` and `Simulation` verb; `WorldTickScript`, `WorldConsoleWaitGate`, `WorldTestRun`; the capture scheduler's arming, `HoldsClock` and `PublishTick`; `WorldMachineHost.Advance` and queued segment submission (`WorldMachineHost.Instances.cs:462`); `WorldPeerHost.DrainPending` (`WorldPeerHost.cs:1980`); the seat half of `WorldClient`: `AdvanceSeatViews`, `SubmitAuthorityIntents`, roster and seat-binding sync (`WorldSimulation.cs:68-87`); the mailbox's write side. |
| Frame | The window and swap chain; input capture into the router; `WorldClient`'s presentation half (poses, easers, `WorldStateMirror`, delivered definition); `WorldFramePresenter`, residencies, GPU uploads and present; `WorldScreenBinder`; the audio director's frame feed; the overlay and HUD; `Presentation`-routed verbs; capture serving (`FrameCaptureController`); the mailbox's read side; `SettleOwedFrames` at shutdown. |
| Already their own | The audio render thread (`WorldAudioRenderService.cs:179`); queued machine workers (`QueuedMachineHost.cs:11`, one per cabinet); socket readers and the accept loop (`WorldPeerHost.cs:206,2039`), which reach the server only through `ExecuteAuthorityOperation` (`WorldServer.cs:465-495`) and the tick-thread drain; extension workers; the hot-reload agent (`CodeRevision.cs:80-83`); the code warmer. |

Hot reload needs no new seam: `CodeRevision.Applied` fires on the agent's
thread and every cache compares `Current` on its own thread
(`CodeRevision.cs:29-34`); `Program.cs:27` only narrates. Audio needs none
either: the server's music taps (`WorldPostBuildWiring.cs:500-520`) will fire
from the simulation thread into a director that already gates every entry
(`WorldAudioDirector.cs:180-205,1393-1551`) and feeds a separate render thread.

### Cross-thread reads that must move behind the handoff

Found by walking what the frame path reads from simulation state today.
Each either moves into the mailbox's tick record, becomes a published
(volatile) value, or takes the authority gate.

| Read today | Where | After |
|---|---|---|
| `IWorldSimulationClock.Tick` = `server.NextInputTick - 1` | `WorldFramePresenter.cs:119,690,957,1227,2113`; `.Convergence.cs:117` (`TickedSeconds`) | `m_client.Tick`; delete the interface (decision 3). |
| `m_server.NextInputTick` | `WorldOverlayFacts.cs:26,64` | `client.Tick`/`EngineTick`. |
| `FrameContext.AccumulatorTicks/ElapsedTicks/DeltaTicks` from the pump | `LauncherWindowHostedService.cs:671-682`; `SdfWorldResidency.cs:817-822` | Filled from the mailbox's applied tick record (decision 3); `FrameContext` keeps its shape. |
| `WorldSimulation.PublishSeats` reads `m_composer.ActiveLayoutName` | `WorldSimulation.cs:84`; set per frame at `WorldViewComposer.cs:423` | A volatile published string (reference writes are atomic; add the `Volatile` pair). |
| `PublishSeats` writes `WorldPerceptionAnchor`; the presenter reads it per frame | `WorldSimulation.cs:79-86`; `WorldFramePresenter.cs:1727` | The anchor's per-seat values ride the tick record. |
| `WorldOverlayFacts` reads the router's held tables through `Func<InputRouter>` | `WorldOverlayFacts.cs:39`; the tables are pump-thread only (`InputRouter.cs:2012`) | Held-channel facts ride the tick record. |
| `WorldStateMirror` refresh at the tick, apply per frame, no lock | `WorldStateMirror.cs:44-45` | Both on the frame thread by construction: refresh happens when the frame applies the tick record. Nothing changes inside the mirror. |
| `IWorldAdjacencySource` is server-side `WorldAdjacencyFields`, read per frame | `WorldAdjacencyFields.cs:505-540`; `WorldContinuum.cs:34-36`; `WorldFramePresenter.cs:1140-1148` | Audit: its pin state is written on delivery (simulation thread) and read per frame. The handle's fields become one published record swapped by reference, as `WorldSessionMirror.Document` is (`WorldSessionMirror.cs:283`). |
| `WorldCaptureScheduler`: armed, held and published on the pump; served from the frame | `WorldCaptureScheduler.cs:117,219,345,1116`; "every member runs on the host pump" | One gate inside the scheduler; the hold protocol crosses the thread (section 2). |
| `HostFrameTimeline` written and read on one thread | `HostFrameTimeline.cs:24-28` | A tick lane written by the simulation thread (section 5). |
| `WorldClient`'s seat half reads the endpoint mirror, not the pose arrays | `WorldClient.cs:409-426` (`endpoint.TryEntityPose`) | Holds. Split the file so the seat half (`AdvanceSeatViews`, `SubmitAuthorityIntents`, `ComposeMoveFrame`, roster submission bookkeeping) is visibly simulation-owned and never touches `m_renderPosition`, `m_stateMirror` or `m_definition`; `ResolveSeatKit` must read the endpoint's definition. |
| `PresentPacingControl.TickLocked` | `PresentPacingControl.cs:52` (already volatile) | Holds; becomes the lockstep handoff's flag. |
| `WorldSeatViewState` mutated by input, read by rendering | `WorldSeatViewState.cs:9-11,21` (already gated) | Holds. |

## 2. The handoff

### What the presenter consumes

The frame reads `RenderTicks`, `InterpolationAlpha`, `DeltaTicks` and
`FrameDeltaTicks` from `FrameContext` (`src/Puck.Hosting/FrameContext.cs:30-62`),
then `WorldClient.UpdateRenderPoses(alpha)` and `StateMirror.Apply(fraction)`
(`WorldFramePresenter.Convergence.cs:74-78`), the delivered definition and
state mirror throughout, and the tick for camera clocks and the binder.

### The tick record

The mailbox is a ring of `Depth = 4` slots. Each slot owns its buffers
(`EntitySnapshot[capacity]`, `long[]` state, `FieldCellDelta[]`,
`WorldInputPublication[]`), grown once to the population's capacity and never
reallocated in steady state. A slot holds one tick's deliveries in order:

- `Snapshot`: the copied `WorldSnapshot` (the hub's borrowed scratch is copied
  here and nowhere else; `EntitySnapshot.State` is sliced from the slot's
  `long[]`).
- `Definition`, `State`, `Composition`, `SessionLever`, `Answer`: recorded as
  they arrive on the sink, each a reference to an already immutable object
  (`WorldDefinition`, `WorldStateStamp`, `WorldComposition`) or a small struct.
- The clock: `Tick`, `EngineTick`, `StepTicks`, `ElapsedTicks` and the
  publication timestamp (`Stopwatch.GetTimestamp()` at the end of the step).
- The seat facts the frame reads (section 1): perception anchor, held-channel
  facts, the seats' presented look.

The simulation thread writes a slot, then publishes it with one
`Volatile.Write` of the ring's head. The frame thread, at frame start, applies
every slot from its tail to the head in order, calling exactly the
`IClientSink` methods the hub would have called, so `WorldClient`'s code does
not change. Steady state allocates nothing on either side.

### Alpha

`alpha = clamp((now - publishedAt) / stepPeriod, 0, 1)` (decision 3), pinned to
one under lockstep, offscreen and a converging capture, as `PinsPresentation`
pins it today (`WorldFramePresenter.Convergence.cs:74`). `RenderTicks`
becomes `ElapsedTicks + alpha * StepTicks`, rounded down.

### When the frame is slow

If the frame thread has not applied a slot by the time the ring is full, the
simulation thread folds the oldest unapplied slot into the one after it: the
later snapshot wins per entity with `Continuity = older.Then(later)`,
`FieldCells` concatenate (a `FieldsFull` later image replaces), `Inputs` take
the later publication, and every non-snapshot delivery of the older slot is
kept ahead of the later slot's. The fold is counted (`frames.folded-ticks`).
The simulation thread never waits for the frame thread.

### When the simulation is slow

`FixedStepPump.Advance` already bounds a catch-up burst by one step's period of
wall time and sheds the rest (`FixedStepPump.cs:248-259`), so a tick that costs
more than its period runs one step per call and the World runs slower than
real time. The frame interpolates to the last applied tick and holds there
(alpha clamps at one). The shed count becomes a counter (section 5).

### Pacing the simulation thread

The headless loop's waitable-timer pacing (`HeadlessTickHostedService.cs:210-230`,
`LauncherHostLoop.WaitUntil`) paces the simulation thread: one `Advance` per
period, with the same catch-up grid. The frame thread paces on the display as
now. Neither thread samples the other's clock.

### Garbage collections

Workstation concurrent GC suspends every managed thread for a pause, so a
10 to 12 ms gen0 lands on both threads at once; nothing here changes that
(the young-object work is the frame-time packet's). What the split must
guarantee is that neither side waits on the other across a pause: the ring
depth of four ticks (133 ms at 30 Hz) covers a pause on the frame side, the
burst bound covers one on the simulation side, and no lock is held across a
step except the authority gate the step already holds
(`WorldServerStepShell.cs:49-57`).

### Captures and holds

A capture armed at tick N owes a frame of tick N before N+1 steps. Today the
pump asks `HoldsClock` before each step and the same thread produces the
frame (`FixedStepPump.cs:187-190,305-311`). After the split the pump still
asks; a held step parks the simulation thread on a mailbox event the frame
thread sets after it presents a frame whose applied tick is N (or the
scheduler's hold budget expires and refuses the capture by name, as now).
`AwaitsFrame` ends a burst in the same way. The contract is unchanged; only
the waiter is on another thread.

### The lockstep handoff

`world.lockstep on` (`WorldLockstepCommandModule.cs:7-10`) and the offscreen
host mean one tick per produced frame with the frame producing the tick it
just stepped. The pump's owner becomes the frame thread: on seeing
`TickLocked` flip, the simulation thread finishes its current `Advance`, marks
the pump parked, and waits; the frame thread, seeing the pump parked, runs
`TryStep`/`Hold` inline before producing (the offscreen loop's body,
`OffscreenTickHostedService.cs:250-258`) and drains the mailbox immediately
after the step. Flipping back releases the simulation thread at a frame
boundary. The offscreen host simply starts parked and never releases. The
pump itself stays single-owner and unlocked.

## 3. Determinism

What a tick computes depends on its `CommandSnapshot` and the server state;
neither changes. The order is: drain (`beforeStep`), `SnapshotForTick`,
`ApplySnapshot`, `Step` (`FixedStepPump.cs:332-360`), every step, on the
simulation thread. Inputs enter the router under `m_captureGate` with a
`CaptureTick` stamped by the shared input clock and a sequence number
(`InputRouter.Capture.cs:176-183,229-235`), and the drain takes everything
with `CaptureTick < windowEndTick` in `(captureTick, sequence)` order
(`InputRouter.Capture.cs:82-105`, `InputRouter.cs:2223-2274`). Moving the
drain to another thread moves no stamp and no comparison. The windows are
`[CaptureOriginTicks + elapsed - step, CaptureOriginTicks + elapsed)`
(`FixedStepPump.cs:340-346`), pump-owned and unchanged.

The replay tape records the server's input stream at the loopback taps
(`LoopbackTransport.cs:17-25`, `WorldReplayTape.Capture.cs:6-8`) and closes
each tick inside the step (`WorldServerStepShell.cs:56`); every tap and
`NoteTick` stays on the pump's thread, which is what the tape requires
(`WorldReplayTape.cs:64-66`). State hashes therefore do not move: the same
document and the same captured input give the same snapshots and the same
steps. A live run's wall-clock arrival still decides which tick a physical
press lands in, exactly as today; a replay restores the recorded stream.

One-tick-per-frame shapes:

- `puck test --reproduce` boots `--headless true --unpaced true`
  (`TestCommand.Leg.cs:111-112`): the headless host, one `TryStep` per
  iteration, no frame thread, no mailbox reader. Nothing in this design runs
  there except the converged loop (phase 1), which must keep `TryStep`
  per iteration.
- `world.lockstep`, offscreen and the sweep's tours step inline on the frame
  thread (section 2), one tick per produced frame, mailbox drained after the
  step, alpha pinned to one.
- `world.at` lines run in the drain between the tick they name and the next
  (`WorldTickScript.cs:10-15`); the drain is on the thread that steps, in every
  shape, so the tick is exact whether the line arrived over stdin or the MCP
  control attachment.

## 4. Commands

- **Simulation-routed verbs** (`body.*`, `player.*`, `world.row.set`,
  `world.grant`, `world.rate pause/resume`, `world.instance.*`) are injected
  into the router (`CommandRegistry.cs:1738`) and fold into the next tick's
  snapshot; unchanged.
- **Immediate verbs that read or write server state** (`world.status`,
  `body.where`, `world.grants`, `world.why`, `world.save`, `world.history`,
  `replay.*`, `world.wait`, `world.at`) run in the drain on the simulation
  thread, between steps, where they run today. The read-after-write barrier
  (`TextCommandSource.cs:49-120`) is unchanged.
- **`world.reload` and live edits**: `WorldSourceWatch.Poll` runs at the top
  of the step (`WorldHostStep.cs:108`) and submits the ordinary reload line;
  the swap applies at the tick boundary and reaches the frame as a
  `Definition` delivery in the tick record. Unchanged in substance.
- **Crossings between worlds and instance steps** are inside `WorldHostStep`
  (`:137-151`) on the simulation thread. Session views of another world read
  their endpoint's `WorldSessionMirror`, which is already a cross-thread
  mirror (`WorldSessionMirror.cs:515`).
- **Socket, federation and extension work** enters through
  `ExecuteAuthorityOperation` or the tick-thread drain, as now.
- **Presentation verbs** declare `CommandRouting.Presentation` and run in the
  frame thread's drain, before the frame's mailbox apply. The families:
  `view.*` and `world.view.*` (composer, cameras, pick, panes), `host.frames`,
  `world.gpu-timing`, `world.counters gpu`, `world.shaders.reload`,
  `world.quality` and the render levers, `world.debug-view`, `capture.*`,
  `world.screenshot`'s arming, `gpu.*`, `backend`, `console` display verbs,
  and `host.memory`. A presentation verb that also reads server state does so
  through `ExecuteAuthorityOperation`, the door every off-thread caller uses.
  The drain is the same `Collect` with a routing filter; a line's answer
  settles through its session as a Simulation line's does.
- **Hot reload** needs no marshalling (section 1).

## 5. Shutdown, exceptions, diagnostics

**Shutdown.** The simulation thread is a hosted service's pump
(`LauncherHostLoop.RunPump`), so an exception faults the service and stops the
host with the exception in hand, as a loop fault does today
(`LauncherHostLoop.cs:79-83`). Stop order: the host cancels; the frame loop
finishes its frame and signals the simulation thread; the simulation thread
finishes its step, drains nothing further and exits; the frame thread applies
the last tick record, calls `SettleOwedFrames` (owed captures refused by name,
`FixedStepContext.cs:43-47`), then disposes the render root. A parked pump
(lockstep) is released by the stop signal. `StopAsync` joins the simulation
thread with a bound and reports a thread that did not return.

**Device loss** is recovered on the frame thread only (`LauncherWindowHostedService.cs:700-740`);
the simulation keeps ticking and the mailbox folds. Today recovery also
stalled the tick; the clamp absorbed it. After the split it stalls nothing.

**Diagnostics.** `HostFrameTimeline` gains a tick lane written by the
simulation thread: per tick, its step cost, the steps the pump shed, folded
tick records, and the parked time of a held step. `host.frames` prints the
frame iterations as now (produce, begin, present, wait) with `simulate` read
from the tick lane instead of a frame phase, then the tick lane's avg, p50,
p99 and max and the counters: `steps.shed`, `ticks.folded`, `ticks.held`.
`world.rate` adds `shed` to its read-back. A diagnostic read of a ring
another thread writes may see one torn iteration; that is accepted for a
timing read, not for a counter (counters are `Interlocked`).

## 6. Duplicated mechanisms on this path

Named under the owner's duplication ruling; the plan converges each on one
rather than building the thread beside them.

1. **Three host loops**, each re-implementing drain, flush, exit deadline,
   snapshot-input captures, step and hold: `LauncherWindowHostedService.cs:395-720`,
   `HeadlessTickHostedService.cs:151-230`, `OffscreenTickHostedService.cs:236-330`;
   the windowed loop's lockstep branch (`:600-618`) is the offscreen body a
   fourth time. One loop with a pacing policy (`HostPacing`,
   `src/Puck.Hosting/HostPacing.cs`, extended from two values to the shapes:
   wall-clock threaded, wall-clock inline, one tick per frame, unpaced).
2. **Two step shells** over `WorldHostStep`: `WorldSimulation.cs:16` and
   `HeadlessWorldSimulation.cs:17`, differing in `afterInstances` only. One
   `WorldSimulation` taking its post-step.
3. **Two console drain implementations per windowed iteration**: the loop's own
   `m_textSource.Collect()` (`LauncherWindowHostedService.cs:574`) and the pump's
   `beforeStep` drain (`FixedStepPump.cs:150-154`). One shared operation must
   serve both scheduling points: before input snapshots between ticks, and
   iterations with no due step, a held capture or no simulation. Deleting the
   iteration scheduling point strands quit, recovery and presentation work
   while `Hold` or a host with no pump runs (memo review F16).
4. **Three simulation clocks the frame reads**: `IWorldSimulationClock.Tick`
   (`WorldHostStep.cs:55`), `WorldClient.Tick` (`WorldClient.cs:132`), and
   `WorldOverlayFacts.CompletedTick` (`WorldOverlayFacts.cs:62-69`). One:
   the client's.
5. **Two interpolation fractions**: `FrameContext.InterpolationAlpha`
   (`FrameContext.cs:52`, the accumulator) and
   `WorldSessionMirror.ResolveInterpolationAlpha` (`:896`, wall arrival),
   with a pinned copy in `WorldAdjacencyFields.cs:526`. One: the arrival form.
6. **Two mirrors of the boot world's snapshot**, both subscribed to the hub and
   both copying every entry per tick: `WorldClient` (presentation) and the
   boot endpoint's `WorldSessionMirror` (routing and intents,
   `WorldAuthorityEndpoint.cs:121`). The split makes the duplication
   legible (one per thread); folding them is larger than this packet and is
   filed in the [refactoring campaign](open-items.md#refactoring-campaign).
7. **Three per-sink redaction scratch buffers**: `WorldOutputHub.cs:71`,
   `WorldSessionSink.cs:29`, `WorldFederationProjectionSink.cs:42`. Filed with
   the above; the mailbox adds none (it copies the delivered, already redacted
   snapshot).

## 7. Phased build

Each phase is a branch that builds and runs on its own. Finish lines name
what to run; test requirements are lifted (AGENTS.md), so each phase is
verified by building and running what it touched.

### Phase 1: one host loop, no thread

**Delivered by `sim-phase1`.** `Puck.Launcher.HostLoop` owns the iteration,
input contributions, console scheduling, stepping, holds, deadlines and pacing.
The three hosted services only start it with their presentation adapter;
`WindowHostPresentation` owns window input, rendering and display waits, and
`OffscreenHostPresentation` owns offscreen rendering. All simulation still
runs inline on the existing host pump thread. `WallClockThreaded` names a
reserved policy; this phase starts no simulation thread.

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
it. Resetting the frame sample window does not reset that counter. Folded and
parked tick metrics remain for the phases that introduce a mailbox and thread.

Verification and windowed measurements are recorded in the delivery commits.
The four focused debug sources are `controls-dash-tests.puck`,
`input-clock30-tests.puck`, `relative-input-tests.puck` and `noclip-tests.puck`:
nine scenarios reproduce, with byte-identical authority hash sequences and
canonical state exports before and after. The standard package has no
`*-tests.puck` source. A headless stdin script lands movement at ticks 10 and
30 and reads tick 31 hash `d8484552ca622a93`, then tick 32 hash
`b30470686bd27afd`, before and after.

Original scope: converge duplicates 1 to 5. Files: `src/Puck.Launcher/LauncherWindowHostedService.cs`,
`HeadlessTickHostedService.cs`, `OffscreenTickHostedService.cs` (become one
`HostLoop` plus a thin hosted service per registration), `LauncherHostLoop.cs`,
`src/Puck.Hosting/HostPacing.cs`, `src/Puck.World/WorldSimulation.cs`,
`HeadlessWorldSimulation.cs` (deleted), `WorldHostStep.cs`,
`src/Puck.World.Client/IWorldSimulationClock.cs` (deleted),
`WorldFramePresenter*.cs`, `src/Puck.World/WorldOverlayFacts.cs`,
`WorldBootComposition.cs`, `HostFrameTimeline.cs` (tick lane and `steps.shed`,
the packet's open low), `WorldRateCommandModule.cs`.
Finish: a windowed debug World's `host.frames` standing and walking reads the
same phases as before with `simulate` intact; `puck test --reproduce` on
`worlds/debug` and the standard's test worlds matches the pre-phase hashes;
`puck sweep` headless green; a lockstep `world.at` still with identical
`[still]` hashes before and after.

### Phase 2: the mailbox, still one thread

Introduce the mailbox behind `LoopbackTransport.Bind` and drain it on the same
thread right after `Advance`. Files: a new `src/Puck.World.Client/WorldClientMailbox.cs`
(the `IClientSink` with its slots and the fold), `WorldBootComposition.cs:329`,
the loop, `FrameContext` filling. Finish: hashes unchanged (the mailbox is a
pure relay on one thread); lockstep stills byte-identical to phase 1 but for
the known residue; `frames.folded-ticks` reads zero; a run with the ring
forced to depth one (a diagnostic lever) shows the fold producing no streak on
a teleporting body.

### Phase 3: ownership moves

Mark the presentation verbs `CommandRouting.Presentation` and add the frame
drain; move every read in section 1's table behind the handoff; split
`WorldClient`'s seat half into its own file; gate `WorldCaptureScheduler`;
publish `ActiveLayoutName`. Still one thread. Files: `src/Puck.Commands/CommandRouting.cs`,
`CommandRegistry.cs`, `TextCommandSource.cs`, the verb modules named in
section 4, `src/Puck.World.Client/WorldClient*.cs`, `WorldContinuum.cs`,
`src/Puck.World.Server/WorldAdjacencyFields.cs`,
`src/Puck.World.Console/WorldCaptureScheduler.cs`, `WorldViewComposer.cs`,
`WorldSimulation.cs`. Finish: every verb family in section 4 answers as
before over stdin and over `puck_exec`; `world.view.pick` still answers under
pause; hashes unchanged.

### Phase 4: the thread

Start the simulation thread; the frame applies the mailbox; alpha from
arrival; the lockstep handoff and capture holds; shutdown order. Files: the
loop, `FixedStepPump.cs` (the parked state), `WorldClientMailbox.cs`,
`HostFrameTimeline.cs`, `WorldLockstepCommandModule.cs`. Finish: a windowed
debug World (Direct3D 12, 1280x900, `world.quality high`, `world.lockstep
off`) standing at the lobby and walking the gallery (`body.pose 30 - 22 90 - -`,
`player.move 1 0` for 60 ticks) reads `host.frames` with no iteration over
16.7 ms attributable to `simulate` and the tick lane's p99 within 1 ms of the
headless tick cost; `world.lockstep on` then a `world.at` still matches the
phase 1 still; `puck test --reproduce` unchanged; a stdin script with
`world.wait`, `world.at` and `quit` ends cleanly with
`--exit-after-seconds`; the MCP `puck_exec` script path answers every line.

### Phase 5: hardening

A windowed Vulkan boot of the same stops (the owner's ruling on Vulkan);
`host.frames` under a forced gen2 (`host.memory collect`) showing neither
thread waiting on the other beyond the pause; cabinets engaged under lockstep
and free-running; hot reload under `dotnet watch` with a body edit on the
presenter and on a rule host; a crossing into the garden and back.

### The riskiest phase, and what would end the approach

Phase 4's lockstep handoff and capture holds carry the risk: two owners of
one pump across a mode flip, and a hold that waits on the other thread. The
plan-B inside the approach is to leave the pump on the simulation thread
under lockstep too and make the frame thread wait on a tick-complete event
per frame; the semantics (one tick per presented frame, the frame shows the
tick it waited for) are the same, with the simulation thread as the only
owner ever. The approach itself is abandoned only if, after phase 4, the
standing tick lane's p99 exceeds the headless tick cost by more than the
margin above, meaning the gates the frame path needs cost more than the
frame gained; the measurement is the finish line's `host.frames` beside a
headless `host.frames` of the same stop.

## 8. What this cannot fix

Costs that stay on the frame thread, with the packet that owns each:

- Produce: 6 to 7 ms on Direct3D 12, 8 to 11 ms on Vulkan
  ([frame time](open-items.md#frame-time-on-the-lead-machine)).
- View recording on tick frames: the session and window views whose
  residencies change with the tick record on tick frames only
  (`ShaderPipelineRenderNode.RenderFrame` 2.8 ms against 0.9 between; the
  [simulation tick packet](open-items.md#simulation-tick-off-the-frame-thread)'s
  measured facts, owned by [frame time](open-items.md#frame-time-on-the-lead-machine)
  and the look call on rendering portal and session views a frame late in
  [owner decisions](open-items.md#owner-decisions)).
- First-visit bursts: window feeds' first binding, `WriteOwedMeshes`,
  `PackDynamicTransforms`, the exposure meter's trace
  ([frame time](open-items.md#frame-time-on-the-lead-machine),
  [residency and streaming](open-items.md#residency-and-streaming)).
- GC pauses suspend both threads: gen0 10 to 12 ms with 4 MB promoted, the
  glass resize's gen2 ([frame time](open-items.md#frame-time-on-the-lead-machine)).
- Irradiance publication and the far cascade
  ([lighting and GI](open-items.md#lighting-and-gi)).
- The tick's own cost (rule evaluation 44 %, snapshot delivery 17 %, the
  scoped replay hash 10 %) moves off the frame but still bounds how far
  behind real time a slow machine runs
  ([frame time](open-items.md#frame-time-on-the-lead-machine),
  [test runner and replay](open-items.md#test-runner-and-replay)).
