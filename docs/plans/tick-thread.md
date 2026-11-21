# The tick on its own thread

This memo answers [the job system's open question 7](engine-job-system.md#open-questions)
and designs item 1 of [the CPU frame](open-items.md#the-cpu-frame): the
in-process authority steps on a thread of its own, and the frame presents the
latest completed tick. It is a design. Another family reviews it before any
code is written, and [the steps](#the-steps) are what is built afterwards.

## Summary

Today one thread does everything. `HostLoop.Run` reads input, drains the
console, steps the world (`FixedStepPump.Advance`) and then produces the frame.
A frame therefore costs the tick plus the produce, and a slow frame owes more
steps, which makes the next frame slower.

The proposal keeps that thread as the owner of everything a player or an
operator touches (the window, input, the console, the client's state, the
frame) and moves one thing off it: the execution of each world's tick. The
loop thread, called the pump here, closes a tick's input, hands the tick to a
step thread, and goes on to produce a frame from the last tick it collected.
When the tick has finished, the pump collects it: it applies what the tick
delivered to the client, runs the hooks that were waiting for the tick, and
only then closes the next tick's input.

Three properties follow.

- **Simulation state cannot depend on thread timing.** The step thread reads
  only input the pump closed before the hand-off. The pump reads the authority
  only between a collect and the next hand-off. Each tick still sees exactly
  the input and the order of events it sees today.
- **A frame costs the longer of the tick and the produce.** The pump never
  waits for a tick it can present without. Where the frame must show a
  particular tick (lockstep, a capture, an offscreen or headless host), the
  pump waits for it through the wait door, on the same path.
- **Tick-integrated motion reaches the display in the same interpolation
  phase as today.** A delivery is stamped with the time its tick completed,
  so a frame interpolates toward a tick exactly as far as it does now. Look,
  camera and cursor read input at present time, as they do now.

The old path, in which the pump executes the tick, is deleted. No toggle
selects it.

## What the code does today

- **One loop.** Every host shape runs `HostLoop.Run`. An iteration captures
  window input, runs the control phase (`FixedStepPump.BeginIteration`, which
  drains the console through `HostConsoleDrain.Collect`), steps, produces and
  paces.
- **One step, in a fixed order.** `FixedStepPump` drains the console, takes
  the tick's input (`InputRouter.SnapshotForTick`), applies it
  (`CommandRegistry.ApplySnapshot`) and calls `IFixedStepSimulation.TryStep`,
  in that order, before every step.
- **The World's step is already resumable.** `WorldHostStep.TryStep` runs in
  phases (prepare, the boot world, the other world instances) and returns
  `false` when a dependency outlasts its wait slice. `FixedStepPump` then
  keeps the tick's input and clock position (`FixedStepPump.StepPending`),
  the loop runs no console line (`FixedStepPump.AtTickBoundary` is false) and
  produces no frame, and the next iteration resumes the same tick. The
  [control plane's design](engine-job-system.md#an-incomplete-tick) built this
  for a tick that waits on a machine or a fork.
- **Every authority tick goes through one function.** The boot world
  (`WorldHostStep`) and every other world instance
  (`WorldInstanceHost.TryStepInstances`) step through
  `WorldServerStepShell.TryStep`. It drains the server's mailbox, takes
  `WorldServer.AuthorityGate`, advances the tick (`WorldTick.TryAdvance`),
  folds the journal, closes the replay tape's tick, and publishes the tick to
  the caller's hook.
- **A tick delivers to the client while it runs.** `WorldOutputHub` calls
  every attached `IClientSink` synchronously inside the tick, and lends each a
  snapshot whose memory the next tick overwrites. `WorldClient` is one of
  those sinks, so the tick writes the state the frame reads.
- **The authority already has a gate for other threads.** A socket task
  enters through `WorldServer.ExecuteAuthorityOperation` or
  `WorldServer.RunOnTickThreadAsync`, and `WorldServer.EnterAtTickBoundary`
  makes a thread wait for a held tick to settle. Nothing makes the pump use
  that gate, because the pump has always been the thread that steps.
- **A client of another authority already works the other way.**
  `WorldAuthorityEndpoint` reaches an authority as a submission door
  (`IServerLink`) and a mirror of its deliveries (`WorldSessionMirror`), and
  the mirror is written for a delivering thread that is not the reading one.

## The design

### Three parts of a tick

Each authority tick has three parts, and the design places each on a thread.

1. **Prepare, on the pump.** The work that closes the tick's input. For the
   host this is what `WorldHostStep` does before the boot world steps: the
   source watch and reload polls, seat route changes, seat views and seat
   intents (`WorldInstanceHost.PrepareBootSeatIntents`), and the transfer
   drain (`WorldInstanceHost.DrainPendingTransfers`). For each world it is the
   server's mailbox drain, which stays on the pump because a mailbox entry can
   reach the host's own tables.
2. **Execute, on the step thread.** The part `WorldServerStepShell.TryStep`
   runs under `WorldServer.AuthorityGate` today: the replay tape's injection,
   `WorldTick.TryAdvance`, the mutation journal's flush, the journal fold and
   the tape's tick close. A wait inside the tick (a machine's drain, a fork's
   join) is waited out on the step thread, under the stall deadline the tick
   already has.
3. **Collect, on the pump.** The work that follows a completed tick: replay
   what the tick delivered ([the outbox](#from-the-authority-to-the-client-the-hubs-outbox)),
   publish the tick to its hooks (`WorldConsoleWaitGate.PublishTick`,
   `WorldCaptureScheduler.PublishTick`, `WorldTickScript.PublishTick`,
   `WorldTestRun.PublishTick`), then the host acts that read the settled state
   (`WorldInstanceHost.ScanBootBoundaryTriggers`,
   `WorldInstanceHost.SettleBootScreenSessions`).

The three parts are separate functions of `WorldServerStepShell`. A caller
that has no host and no frame (a law test, the shadow server of a replay
verification) runs them in order on its own thread.

### Waves

The pump hands the step thread a *wave*: a list of world ticks whose inputs
are all closed, executed in order with nothing from the pump between them. One
host step is two waves, which keeps today's order of events.

- **The first wave is the boot world's tick.** Its collect runs the hooks
  above. The test runner depends on that position: it submits a scenario's
  input in `WorldTickScript.PublishTick`, after the boot tick and before the
  other world instances step.
- **The second wave is one tick of every other world instance that is due.**
  The pump composes each instance's seat intents first
  (`WorldClient.SubmitAuthorityIntents`), as `TryStepInstances` does before
  each step now. After the wave it collects the instances in name order, each
  followed by its portal scan and screen-session settle. An instance that owes
  a further tick in the same host step (a rate above the boot world's) takes
  another wave, so its next intents are composed after its last tick was
  collected, as now.

Within the second wave this changes two things about order, both
deterministic. An instance that an earlier instance's settle stops in the
same host step has already taken its tick. An instance that a settle starts
takes its first tick in the next host step. Instances do not observe one
another inside a host step, so nothing else moves.

A replay drive that fast-forwards steps several boot ticks in one host step
today. Each of those is its own wave, and the pump waits for it, so a
fast-forward costs the frame what it costs now.

The step thread executes a wave's ticks serially, in the order given. It is
one thread for the whole host, not one per world.

An exception a tick throws is caught on the step thread and rethrown on the
pump at collect, so it stops the host through the path it takes today. A tick
that stalls freezes its authority under the stall deadline it already has
(`WorldTick.ExecutionInterrupted`), and the pump learns that from the wave's
result instead of reading the server. Disposal waits for a wave in flight
under [the teardown rule](engine-job-system.md#teardown): if the wave does not
finish in time, the host keeps the servers it still uses and says so, and
frees nothing a running tick holds.

### The pump's waits

The pump asks for a tick through `IFixedStepSimulation.TryStep`, which gains a
deadline. The answer `false` means what it means today: the tick is not
complete, keep its input, ask again.

| The host | The pump's deadline | What the frame does meanwhile |
|---|---|---|
| Paced, producing frames, the last tick due this iteration | None: it polls | Produces, showing the last collected tick |
| The same, an earlier tick of a catch-up burst | The pump's wait allowance (`HostWaits.PumpAllowance`) | Waits, because the next tick's input needs this tick's results |
| Lockstep (`HostPacing.StepsOneTickPerFrame`), a capture's hold, the offscreen host | The allowance | Produces only once the tick is collected, as today |
| Headless | The allowance | There is no frame |

`HostLoop` polls again after produce, so a wave that finished during the
frame is collected and the next wave handed over before the pacing wait. A
later step wakes the pacing wait itself when a wave finishes
([step 6](#the-steps)).

A burst no longer costs the frame every tick it owes. With `k` ticks due, the
frame waits for `k - 1` and runs beside the last. The rule that sheds steps
once a burst has spent one tick period of wall time stays.

### From the authority to the client: the hub's outbox

While a server's tick executes, its `WorldOutputHub` records each delivery
instead of making it, in order. The collect replays the record on the pump.
The borrowed snapshot stays valid because a server executes one tick per
wave, and the next tick cannot start before the pump has collected this one.

A sink receives on the step thread only if it declares that it may, with a
marker interface. The federation and peer sinks, which copy into a wire
queue, declare it. `WorldClient`, `WorldSessionMirror` and every sink that
declares nothing are deferred, which is the safe default: their code runs on
the thread it runs on today.

Three more things the authority calls leave the tick the same way, because
their callees are the pump's:

- the completion of a submission that came through `LoopbackTransport`;
- `WorldServer.EchoTap`, which feeds toasts and the editor;
- `WorldServer.SaveEffectTap`, which reads the live presentation levers.

Each is wrapped where it is assigned, so no call site inside the tick can
invoke it directly. The taps the replay tape attaches
(`WorldServer.MutationTap`, `WorldServer.RebuildTap`,
`WorldServer.ScreenOpTap`, `WorldServer.ServerEventTap`,
`WorldServer.LinkDeliveryTap`) stay inline: the tape belongs to the authority
and records in tick order. Narration stays inline too, since
`WorldOutputHub.Narrate` already takes its own lock for lines that socket
tasks write.

The replay hands sinks the tick's completion time. `WorldClient` and
`WorldSessionMirror` stamp a snapshot's arrival with it instead of reading the
clock, and `WorldSessionMirror.ResolveInterpolationAlpha` then yields the
fraction it yields today.

### From the client to the authority: the boundary window and the link

The pump may touch the authority in one window: inside the loop's control
phase and step section, while no wave is in flight. Console verbs, the input
snapshot's handlers, prepare and collect all run there, as they do now.

Outside the window the pump has one door, the link. A submission made through
`LoopbackTransport` outside the window is queued in the transport and
submitted, in order, when the window next opens. Today such a submission
applies at once, between two ticks; queued, it still applies between two
ticks. Which callers submit from a window event or from produce was not read
for this memo. The queue makes each of them correct without a list, and step
2 counts them.

Any other access from the pump outside the window throws. `WorldServer`
already guards its state seams with `WorldServer.ThrowIfTickPending`
(`WorldServer.Definition`, `WorldServer.Population`, `WorldServer.Machines`,
`WorldServer.Engagement`, `WorldServer.Events` and the gate behind
`WorldServer.ExecuteAuthorityOperation`). That check gains the window, and
the seams that lack it gain the check (`WorldServer.Grants`,
`WorldServer.Configuration`, `WorldServer.Tick`, `WorldServer.SolidField`).
The pump is the thread `HostWaits.BeginPumpIteration` registered, so the check
costs one thread-static read, and a server with no pump (a law test, a silo
row) is unaffected.

The throw does not depend on timing. A frame-thread reader that touches the
authority during produce throws on its first frame, whether or not a tick
happened to be running, because produce is outside the window always.

## The determinism invariant

**Statement.** For every world and every tick `N`, the tick executes from
exactly the input the pump closed for `N`, and the pump closes the input for
`N + 1` only after it has collected `N`. The sequence of events each world
sees (prepare, tick, hooks, host acts, console lines, next prepare) is the
sequence of today's single-threaded loop. No thread's timing chooses an input
or an order, so the same document and input give the same state.

**What enforces it.**

- *The step thread cannot read open input.* It is handed a wave only by the
  pump, and the hand-off is the one place a wave starts. Everything the tick
  reads as input (the intent queue `WorldTick.EnqueueIntent` fills, the
  ordered submissions `WorldTick.Submit` fills, the mailbox) was written
  before the hand-off, and the link queues anything later.
- *The pump cannot read a tick in flight.* The boundary window is the single
  door. `WorldServer`'s seams refuse the pump outside it, deterministically.
- *The authority cannot write the client's state from the step thread.* The
  hub defers every sink that has not declared itself safe, and the three
  callbacks above are wrapped at their one assignment.
- *Collect is ordered by name, not by completion.* Which instance finished
  first is never read.

**Where the guarantee stops.**

- A reference to an object the authority owns, taken inside the window and
  read outside it (a `WorldBody`, a `WorldPopulation`, a machine host),
  passes no seam. The readers the code search found are listed
  [below](#frame-thread-readers-and-where-each-moves), and the search is the
  only cover for one it missed.
- A service the composition root injects into the server and also uses on the
  pump is outside the hub. Those are named in
  [what the tick calls](#what-the-tick-calls-outside-the-authority).
- The check covers the pump. Another thread that reads a server's property
  without `WorldServer.ExecuteAuthorityOperation` was unsafe before this
  change and still is.
- Presentation is outside the contract, as [rule 4](../../AGENTS.md#core-rules)
  says: which frame first shows a tick depends on timing.

## Frame-thread readers and where each moves

Found with `puck references` over `src/Puck.World/Puck.World.csproj` for
`WorldServer`, `WorldInstanceHost`, `WorldInstance`, `WorldPopulation`,
`IWorldMachineHost`, `IWorldGrantsView`, `IWorldAdjacencySource`,
`WorldBody`, `WorldEngagement` and `WorldReplayTape`. The console's command
modules hold most of the references, and they run in the control phase,
inside the window. `Puck.World.Client` cannot name a server type at all
(`build/Architecture.props`); what reaches it is a delegate or an interface
the composition root wires. The table lists each holder that runs outside
the window.

| Reader | What it reads today | Where it moves |
|---|---|---|
| `HostLoop`, through `WorldHostStep.RatePerSecond` and `WorldHostStep.StepInterrupted` | `WorldServer.Configuration` and the tick's stall flag, on every iteration | Values `WorldHostStep` publishes at each collect. `HostLoop` reads the rate once an iteration instead of three times |
| `WorldScreenBinder.MachinesOf` and its callers | A world's `IWorldMachineHost`, for `IWorldMachineHost.PresentedTicks` and a screen's video output, on every frame | The binder resolves each screen's output and clock reader when a wave is collected and holds the table; a machine host's disposal follows that resolve |
| `WorldScreenBinder`'s boot definition and shared-scene proof | `WorldServer.Definition` and the instance's server, per frame | The client's delivered definition, and the observation's own identity |
| The binder's session and infinity feeds | `WorldServer.Envelope`, configured from the frame and read by the tick's capacity check | The envelope publishes its configuration as one immutable record |
| `WorldRenderRoot`'s device-profile step | `WorldServer.Definition` on the first frame | `WorldClient.Definition` |
| `PresentationReadings`' `seat.<n>.place` | `WorldServer.Events` while the HUD binds | A value per seat, published at collect |
| `WorldHudBindingResolver` | `WorldPopulation` counts and `WorldHistory`'s cursor | Values published at collect |
| `WorldIterationClock` | The server's document version after each frame | `WorldClient.DocumentVersion` |
| `WorldCaptureScheduler`, when a frame serves a capture | The definition, the completed tick and the solid field | The delivered tick and definition; a still's state hashes are taken at the next window |
| `WorldDiagnostics` | The server, when a report polls from the frame | The copies `WorldDiagnostics.BeginReport` takes inside the window |
| `IWorldAdjacencySource`, pinned by `WorldFramePresenter`, `WorldAdjacencySceneEmitter` and `WorldContinuum` | Tick-side state the tick rewrites | One immutable record the tick publishes by a reference write, as [the presentation view](runtime-and-delivery.md#the-presentation-view) already plans |
| `WorldSeatViewInput` | `WorldInstanceHost.ResolveRoutedDefinition`, whose fallback is the boot server's definition, while input is captured | The boot endpoint's mirrored definition |
| `WorldEditorSeats`' edit probe | The server and its grants | Evaluated at collect and held per seat |
| The seat post-step (`WorldSeatContextSync.Publish`, haptics, key lights, the wheel) | Grants and the definition's screens | Nothing: it runs at collect, inside the window |

Two readers are found by their effect and not by a server reference.

- **A seat's look.** `WorldClient.AdvanceSeatViews` integrates stick and gyro
  look once a tick, in prepare, and `WorldSeatViewState.PresentedLook` takes
  back the part of that step the frame has not reached. With the tick in
  flight, the frame would present the new step against the previous tick's
  interpolation and then step backward when the tick arrives. The seat view
  therefore holds a tick's step as pending until the tick that carried it is
  collected for the authority the seat is routed to.
- **Render poses.** `WorldClient.UpdateRenderPoses` scans
  `WorldClient.EntityCapacity` slots every frame and interpolates every
  active body, at rest or not. The pose table already keeps the dense list of
  bodies the last snapshot carried (`WorldPoseTable.Seen`). A delivery
  records which of those moved (the two tick poses differ, or a correction is
  still easing), and the frame interpolates that subset; a body at rest keeps
  the pose its delivery wrote. The frame's cost follows moving bodies, not
  capacity.

## What the tick calls outside the authority

The step thread now runs code the pump used to run. Each thing the tick
reaches outside the server is one of these.

| The tick reaches | Ruling |
|---|---|
| Client sinks, submission completions, `WorldServer.EchoTap`, `WorldServer.SaveEffectTap` | Deferred to collect by the hub |
| Narration | Inline; the hub locks, and its sinks already receive from socket tasks |
| The replay tape's taps, `WorldHistory`, the mutation journal | Inline; they are the authority's |
| `WorldSeatAuthorityRouter` | Inline; its table is compare-and-swap and its change edge is delivered on the pump (`WorldSeatAuthorityRouter.RouteChanged`) |
| Machines (`IWorldMachineHost`) | Inline; machines run on their own workers already |
| Addon guests (`IWorldAddonHost`) | Inline; step 2 confirms on the headless boot that a guest mounted on the main thread ticks on the step thread |
| `WorldServer.Profiles`, `WorldServer.Neighbours`, `WorldServer.RebuildDocuments`, `WorldServer.TransferForwarder` | Inline; the pump uses them only inside the window |
| `WorldServer.Envelope` | Reads the published record |
| `HostFrameWork` measurements | The step thread binds its own; collect folds its parts into the iteration |
| `WorldNarrationScope` | Set by the step thread for each tick of a wave |

One consequence belongs to the job system. `JobPool` runs one fork across its
workers at a time, and a fork requested meanwhile runs inline on its caller.
The tick's forks and the frame's forks used to alternate on one thread; now
they overlap, so one of them runs serially whenever they meet. The pool must
serve two forking threads before the [frame fork](open-items.md#the-cpu-frame)
and the parallel tick both pay. That is the job system's step, not this one.

## Input, and the time from input to display

**Where input is sampled.** Unchanged. The window's events reach
`InputRouter` as they arrive, stamped on the input clock. The pump takes a
tick's `CommandSnapshot` (`InputRouter.SnapshotForTick`) and applies it
(`CommandRegistry.ApplySnapshot`) at the hand-off, so input captured in an
iteration is in the tick handed over in that iteration, as now.

**How commands reach the step thread.** They do not cross as commands. The
snapshot's handlers run on the pump and leave their results in the
authority's queues before the hand-off: seat state becomes intents
(`WorldClient.SubmitAuthorityIntents`, `LoopbackTransport.SubmitIntent`,
`WorldTick.EnqueueIntent`), and a simulation-routed verb becomes an envelope
(`WorldTick.Submit`). The replay tape's taps fire on the pump at the same
points as today. The step thread reads the closed queues.

**Tick-integrated motion.** A frame draws a body between its last two tick
poses, at the fraction of a tick period elapsed since the later one arrived
(`WorldClient.InterpolationAlpha`). Today a tick arrives during its own
frame's simulate phase, so that frame draws almost none of it and the next
frame draws it part-way. With the tick beside the frame, the frame that
handed the tick over draws the previous tick, and the next frame applies the
new one stamped with its completion time, so it draws it the same part of
the way. The difference from today is the sliver the hand-off frame used to
draw, which is the time from the tick's end to that frame's produce, over a
tick period.

A tick longer than a frame arrives a frame later than a shorter one would.
The interleaved loop had no such frame to compare with: it stalled the frame
for the whole tick.

**Look, camera and cursor.** Pointer look moves the seat's live look when the
event arrives and shows at once, as now. Stick and gyro look integrate once a
tick and are presented with the tick that carried them
([above](#frame-thread-readers-and-where-each-moves)). The cursor is drawn
from presentation state.

**The budget.** On [the benchmark's latency column](benchmark.md#presentation-timing-and-admission-stage-8),
at an equal frame rate, between-tick pointer look reads no later than it does
on the interleaved loop, and tick-integrated motion reads no more than one
frame period later. The lead takes both readings, before and after, in a
quiet window ([verification](#what-the-lead-measures)). A reading past the
budget reopens one choice, which this memo leaves closed: letting the pump
wait a bounded slice for a tick that is about to finish.

## Console, pause, lockstep, waits and the benchmark

Nothing reaches the step thread except a wave, so each of these reaches the
authority the way it does now: on the pump, inside the window.

- **Console verbs and MCP `puck_exec`.** Both are sessions of
  `TextCommandSource`, drained in the control phase. The loop already drains
  no line while a step is pending (`FixedStepPump.AtTickBoundary`), and a
  step in flight is a pending step, so a verb never runs beside a tick. A
  line released by tick `N` runs before tick `N + 1` is handed over, because
  the hand-off happens only after the control phase that follows the collect.
  Under a paced, frame-producing host a line can wait up to one more
  iteration than today when a tick is in flight as the iteration starts.
- **Mutations.** A verb's submission goes through `LoopbackTransport` inside
  the window, and applies inline or at the next tick as its kind does now.
  Its completion, if the tick fires it, is replayed at collect, so the
  session's read-after-write barrier releases at the same tick.
- **`world.rate pause` and `--paused`.** `WorldInstanceHost.TryPause` runs in
  the window. The next prepare finds the boot world not due
  (`WorldInstanceHost.ShouldStepBoot`), hands over no boot tick, and runs the
  administrative drain on the pump (`WorldServer.DrainAdministrative`), whose
  deliveries are made directly since no tick is executing.
- **Lockstep.** The pump waits for each tick through the wait door and
  produces only once it is collected, so a frame still shows exactly its
  tick and `world.at` scripts land on the ticks they name.
- **`world.wait`.** `WorldConsoleWaitGate.PublishTick` runs at collect, on
  the pump, where the gate's sessions live.
- **The benchmark.** `world.benchmark run` arms its recording and its route in
  the control phase. The route's seat program runs from
  `WorldTickScript.PublishTick`, at collect, and reads only what the seat's
  authority delivered, which collect has just applied.
- **`host.frames`.** The iteration's `simulate` phase becomes the pump's
  share: prepare and collect. The tick's own cost is the step thread's, on
  the timeline's separate tick lane (`HostFrameTimeline.NoteTick`), and each
  iteration reports whether a tick was in flight while it produced.

## A hub of a dozen live worlds

Every world instance beside the boot world is already a tick in the second
wave. A dozen worlds in view add a dozen ticks to that wave and nothing to
the frame except their collects, which apply deliveries the client asked
for.

The step thread executes the wave serially today. The job memo's step T4
forks it: the step thread becomes the fork's parent, and each world's tick is
a partition. One rule must come with that. A world reads its neighbours
through mirrors that the neighbour's tick writes
(`WorldInstanceHost`'s observation sessions), and in a serial wave a later
world sees an earlier world's delivery from the same wave. Under a fork that
would depend on timing. So T4 defers mirror deliveries to collect as well,
and every world in a wave reads its neighbours as of the previous collect.
This memo's outbox is the mechanism; T4 changes which sinks use it.

The silo steps its rows through `WorldInstanceHost` too, so its rows take the
same waves on the same thread, and T4 serves both hosts.

## Systems considered

- **The pending-step protocol** (`FixedStepPump.StepPending`,
  `IFixedStepSimulation.TryStep`, `WorldHostStep.TryStep`). Reused whole: a
  tick in flight is a pending step. The one rule that changes is that a
  pending step no longer withholds the frame outside lockstep.
- **The authority gate** (`WorldServer.AuthorityGate`,
  `WorldServer.EnterAtTickBoundary`). Kept for other threads. It was not
  made the pump's door: a pump that waits on the gate turns a frame-thread
  read into a silent stall for the rest of the tick, where the window's check
  names the reader.
- **The server's mailbox** (`WorldServer.RunOnTickThreadAsync`). Considered
  as the way a console verb reaches the step thread. Rejected: verbs read and
  write through dozens of server members synchronously, and running them on
  the pump inside the window needs none of them rewritten.
- **`WorldOutputHub`.** Extended with the outbox. A copying mailbox sink
  between the hub and `WorldClient` was considered and rejected: a snapshot
  lends memory at three depths, and deferring the fan-out lends it for
  longer instead of copying it.
- **`WorldSessionMirror` and `PublishBuffer<T>`.** The mirror tolerates a
  delivering thread. It is still deferred here, so that a camera reading a
  body through a mirror and a scene drawing it from `WorldClient` see one
  tick.
- **`JobPool`.** A fork joins in its caller and one fork runs at a time, so
  it cannot run a tick beside the frame. The step thread is the parent the
  pool's forks need.
- **`QueuedWorkerLifecycle<TWorkItem>`.** The machines' worker: a queue, a
  window of pending steps with producer backpressure, and barrier items. The
  step thread takes one wave at a time from one producer and needs none of
  that. The hand-off is written as a small lane in `Puck.Hosting.Jobs` that
  the machine worker could later sit on; that merge is not part of this
  change.
- **`HostWaits`.** The pump's wait for a wave is a door wait with a reason,
  the step thread idles as a consumer (`HostWaits.ClaimConsumer`), and the
  pacing wait's wake is `HostWaits.Serve`.
- **`HostFrameTimeline`'s tick lane.** Built ahead of this change for it.
- **The presentation view's debug guard**
  ([the plan](runtime-and-delivery.md#the-presentation-view)). Replaced by
  the window's check, which runs in every build and does not depend on a
  thread being absent.

The job memo's other two arrangements are not taken. Stepping only unwatched
worlds elsewhere leaves the boot world's tick in the frame, which is most of
the cost. Keeping the tick on the frame thread fixes nothing.

## The steps

Each step lands on its own and carries a check that reads load-independent
evidence.

1. **Three parts and the outbox, still on one thread.** `WorldServerStepShell`
   splits into prepare, execute and collect; the hub defers and replays;
   deliveries carry the completion time. Check: `puck test --reproduce` over
   the scenarios the change reaches, and the same state hashes as the base,
   since this step claims to change nothing.
2. **The step thread and the waves.** `WorldInstanceHost` owns the thread;
   `WorldHostStep` hands over the two waves; the rate and stall flag are
   published; the window's check and the link's queue go in. The path on
   which the pump executes a tick is deleted. Check: a law holds the step
   thread inside a tick and reads `WorldServer.Population` from a registered
   pump, which throws; the headless debug world boots and answers
   `body.where`; `world.counters` counts the submissions the link queued
   outside the window; `puck test --reproduce` passes at `--jobs 1` and at
   other worker counts.
3. **The loop.** `HostLoop` produces while a step is in flight outside
   lockstep, polls after produce and reads the rate once; `FixedStepPump`
   takes the next due step after a resumed one. Check: `host.frames` on the
   headless boot shows the tick lane's cost apart from `simulate`.
4. **The readers.** Each row of [the table](#frame-thread-readers-and-where-each-moves).
   Check: the windowed debug world plays the benchmark route with no throw
   from the window's check (the lead's run).
5. **The seat look and the moving set.** Check: `world.counters` counts the
   poses a frame interpolated, and the count is the moving bodies; under a
   held stick the presented yaw never steps backward (the lead's run).
6. **The pacing wait's wake.** A finished wave wakes `HostWaits.Pace`, which
   resumes the step. Check: with a tick longer than produce, a host step with
   two waves completes within one iteration, read from `host.frames`' steps
   per iteration. The swap chain's own wait is not serviced until
   [C0.6](engine-job-system.md#the-steps) puts it through the door.
7. **The documents.** The hosting reference, the rendering register's
   pipelining decision and the `puck-world` skill describe the new loop; open
   question 7 and item 1 of the CPU frame are deleted.

### What the lead measures

These need a quiet machine and a window, which a lane does not have.

- **Frame time.** The benchmark route on the debug world before and after:
  `host.frames`' interval and its `simulate` and tick-lane lines. Expected:
  the interval falls by about the tick's cost on iterations that stepped,
  and `steps.shed` does not rise.
- **Input to display.** The benchmark's latency column, both rows, before and
  after, against [the budget](#input-and-the-time-from-input-to-display).
- **The hand-off's own cost.** The time from the pump's hand-off to the step
  thread's first instruction, read from the tick lane against the wave's
  execute time. It is expected to be tens of microseconds, by reasoning about
  an event wake; no reading has been taken.

---

[Plans](README.md) · [Open items](open-items.md#the-cpu-frame) ·
[The engine job system](engine-job-system.md)
