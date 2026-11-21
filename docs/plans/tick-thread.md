# The tick on its own thread

This memo answers [the job system's open question 7](engine-job-system.md#open-questions)
and designs item 1 of [the CPU frame](open-items.md#the-cpu-frame): the
in-process authority steps on a thread of its own, and the frame presents the
latest completed tick. Another family reviewed its first draft and raised
fifteen findings. This revision answers each
([the review's findings](#the-reviews-findings)), and the findings changed the
build order: the frame cannot run beside the tick until the frame reads no
live authority state, and the review showed how much it reads today.
[The steps](#the-steps) are what is built.

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

The work lands in three stages. The first moves no behaviour: the tick's
parts are separated and the frame thread's cheap readers are fixed. The second
closes each place the frame reads the authority, one structural door at a
time, while the loop still waits for every tick. The third stops the wait,
and that is where the frame time falls.

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
   server's mailbox drain and the tick's adjacency record, which stay on the
   pump because both can reach the host's own tables.
2. **Execute, on the step thread.** The part `WorldServerStepShell.TryStep`
   runs under `WorldServer.AuthorityGate` today: the replay tape's injection,
   `WorldTick.TryAdvance`, the mutation journal's flush, the journal fold and
   the tape's tick close. A wait inside the tick (a machine's drain, a fork's
   join) is waited out on the step thread, under the stall deadline the tick
   already has.
3. **Collect, on the pump.** The work that follows a completed tick: replay
   what the tick delivered ([the outbox](#from-the-authority-to-the-client-the-outbox)),
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

The two waves are kept, and not merged into one, because one wave would move
every scenario's input by a tick: the test runner's lines would reach the
sibling worlds a host step later than they do now. Two waves keep every
script's order, at the cost of a second hand-off in a host step.

Within the second wave five things about order change. Each is deterministic
on one step thread, and each moves state hashes for the scenarios it reaches,
which are re-recorded in the step that makes the change.

- An instance that an earlier instance's settle stops in the same host step
  has already taken its tick.
- An instance that a settle starts takes its first tick in the next host
  step.
- A later instance no longer sees an earlier instance's delivery from the
  same wave. Its adjacency contacts and link liveness
  (`WorldTick.ObserveAdjacencyDeliveries`) read its neighbours as of the
  previous collect.
- Input a portal forwards from one instance to another
  (`WorldPortalInputProducer.Forward`, from the settle after the source's
  tick) arrives a host step later, since both ticks execute before the
  source is collected.
- An instance that takes several ticks in a host step takes them breadth
  first, a wave each, where today it takes them all before the next instance
  steps. The session resolver mints its generation ids in that new order.

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
| A tick the capture schedule arms a capture on | The allowance | Produces once the tick is collected, so the capture is served on the frame it is served on today |
| Headless | The allowance | There is no frame |

`HostLoop` polls again after produce, and a later step wakes the pacing wait
when a wave finishes. Both polls only resume a host step that is already
open: they collect its first wave and hand over its second. Neither starts a
new host step. A new host step closes input, so it starts only in the
iteration's step section, after that iteration's input capture and control
phase. Otherwise which tick saw an input or a console line would depend on
whether the previous tick finished before or after produce.

The capture row needs the scheduler to say, before the hand-off, that a tick
will arm a scheduled capture. Without it the pump would not wait, the capture
would arm a frame late, and the next frame would hold its clock for it.

A burst no longer costs the frame every tick it owes. With `k` ticks due, the
frame waits for `k - 1` and runs beside the last. The rule that sheds steps
once a burst has spent one tick period of wall time stays.

### From the authority to the client: the outbox

A delivery has two halves, and they belong on different threads.

- **Composing it is the authority's work.** The hub's fan-out picks what each
  subscriber may see. `WorldSessionSink` builds a recipient's disclosure from
  the server as it stands at that moment (`WorldServer.DeliveryTime`,
  `WorldServer.DocumentVersion`, the recipient's view) into scratch it reuses.
  The local presentation's cues are attached only while the tick is delivering
  (`WorldTick.PresentLocalCues`, `WorldTick.PresentCues`), and the tick clears
  them straight afterwards. All of that stays inline, on the thread that runs
  the tick, where the state it reads is the state the delivery describes.
- **Receiving it is the client's work.** What the composing half hands to the
  receiving sink (`WorldClient`, a `WorldSessionMirror`) is recorded in an
  outbox instead of being delivered, and the collect replays the outbox on the
  pump, in order.

So the outbox sits at the last hop: between the hub or a `WorldSessionSink`
and the sink it finally delivers to. A sink receives on the delivering thread
only if it declares that it may, with a marker interface. The federation and
peer sinks, which copy into a wire queue, declare it. Every sink that declares
nothing is deferred, which is the safe default.

A record holds what its delivery lent. A snapshot lends memory the next
snapshot of the same server or session sink overwrites. One tick can deliver
twice to one sink (a definition delivery primes a snapshot, and the tick's own
snapshot follows), so the outbox copies a record's lent spans when it already
holds an earlier one from the same lender, and otherwise holds the loan until
the collect.

The outbox records whenever the delivering thread is not the pump inside the
boundary window. That covers the step thread during a tick. It also covers a
socket task that applies a grant or a session lever through
`WorldServer.ExecuteAuthorityOperation` between two waves, whose delivery
would otherwise reach `WorldClient` on the socket thread while the frame is
being produced. The pump drains the outbox when the window opens as well as at
a collect.

Three more things the authority calls are the pump's, and leave the same way:

- the completion of a submission that came through `LoopbackTransport`;
- `WorldServer.EchoTap`, which feeds toasts and the editor;
- the part of `WorldServer.SaveEffectTap` that reads the live presentation
  levers and writes the file. The part that reads the authority and compacts
  the journal (`WorldSessionCapture.Capture`, `WorldServer.Compact`) stays
  where the rule fired, so a save still captures the state at that point of
  the tick and `world.undo` keeps the entries made after it.

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

### What a delivered document may reach

A record in the outbox must not lead back into the authority. Today it does.
The server binds its live document to its own arena
(`WorldStateDocumentValues.BindLive`, `WorldStateReader.Bind`) and delivers
that same object, so a state-bound value the frame reads in
`WorldClient.Definition` reads the arena the tick is writing, and
`WorldClient.DeliverPresentation` even carries the binding onto the document
it builds. No seam of `WorldServer` is crossed, so no check sees it.

The document a local sink receives is therefore bound to that sink's own
values, never to the server's arena. The client already keeps those values for
an authority in another process: a delivery that changes only values leaves
the declaration fixed, and the snapshot that follows carries the cells that
moved (`WorldStateStamp.ArenaValues`, `WorldDocumentStateView.ApplyFieldCells`).
The local client takes the same path. The server keeps its bound document to
itself and hands a sink the document rebound to the sink's replica, and the
rebinding copies only the paths to the values it replaces, as `BindLive` does.
This is the last cut of [the presentation view](runtime-and-delivery.md#the-presentation-view),
taken for the local client's state values. Step 5 builds it, and its check is
the conformance law that plan already owes: the local view and a view rebuilt
from the same deliveries answer every state read identically.

### From the client to the authority: the boundary window and the link

The pump may touch the authority in one window: inside the loop's control
phase and step section, while no wave is in flight. Console verbs, the input
snapshot's handlers, prepare and collect all run there, as they do now. The
window is an explicit scope the loop opens and closes on the pump, held in a
thread-static, and the pump is registered once before the loop's first
iteration, so the first control phase is inside the rule like every other.

Outside the window the pump has one door, the link, and the link is narrow.

- **A submission that needs no answer is queued.** `LoopbackTransport` holds
  it before it mints the envelope, so the sequence number and the replay
  tape's tap both happen when the window next opens, in order, on the thread
  that owns the tape's open tick.
- **A call whose caller reads the answer before it returns is refused.**
  `LoopbackTransport.Query` and `LoopbackTransport.SubmitSession` complete
  inside the call today, and their callers depend on it:
  `PlayerRoster.ResolveSlot` asks the authority for a device's preferred
  profile while input is captured, and the roster's join and leave paths
  change the client's seat table only if the reply said yes. Queued, the
  first would seat a controller in the wrong slot and the second would split
  the client's roster from the server's. Outside the window both throw, and
  their callers move: the roster reads the profile catalog and the controller
  preferences from values the authority publishes at each collect, and a join
  or a leave asked for by a window event is run in the next control phase.

Any other access from the pump outside the window is closed off in two ways,
and the first is preferred wherever it can be had.

- **Types the frame cannot name.** `Puck.World.Client` cannot reference
  `Puck.World.Server` (`build/Architecture.props`). The presentation types
  that read the authority today all sit in the composition root, `Puck.World`,
  which may name both. They move into a presentation project under the same
  rule, which also forbids `Puck.World.Machines`. A field of type
  `WorldServer`, `WorldPopulation`, `WorldBody` or a machine host in
  presentation code is then a compile error, and the reference that survives
  the window cannot be written. What presentation reads instead is published:
  a tick's values as an immutable record written at the collect, and screens'
  machine outputs as a table of video outputs and clock values, not a
  reference to the machine host.
- **A check at the server's seams, for what stays in the composition root.**
  `WorldServer` already guards some of its state with
  `WorldServer.ThrowIfTickPending`. That check gains the window, and every
  member the tick mutates gains the check: `WorldServer.Grants`,
  `WorldServer.Configuration`, `WorldServer.Tick`, `WorldServer.SolidField`,
  `WorldServer.Input`, `WorldServer.CompletedEngineTicks`,
  `WorldServer.DeliveryTime`, `WorldServer.DocumentVersion`,
  `WorldServer.NextInputTick`, `WorldServer.SolidRevision`,
  `WorldServer.Search`, `WorldServer.JournalLength` and the rest the review
  listed. The throw does not depend on timing: produce is outside the window
  always, so a reader throws on its first frame whether or not a tick was
  running.

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
  ordered submissions `WorldTick.Submit` fills, the mailbox, the tick's
  adjacency record) was written before the hand-off, and the link queues
  anything later.
- *A new host step starts only after the iteration's input capture and
  control phase.* The polls after produce and inside the pacing wait resume an
  open host step and never close input.
- *The frame cannot name the authority.* Presentation code lives where a
  server type is a compile error, and reads what the authority published.
  What still lives in the composition root passes a seam of `WorldServer`
  that refuses the pump outside the window, deterministically.
- *A delivery cannot lead back into the authority.* The outbox records at the
  last hop, and a delivered document is bound to the receiving sink's own
  values.
- *The authority cannot write the client's state from another thread.* The
  outbox defers every sink that has not declared itself safe, whenever the
  delivering thread is not the pump inside the window, and the three
  callbacks above are wrapped at their one assignment.
- *Collect is ordered by name, not by completion.* Which instance finished
  first is never read.

**Where the guarantee stops.**

- The composition root can still take a reference inside the window and read
  it outside (a closure it wires into presentation). Moving the presentation
  types out leaves that wiring as the only place it can happen, and the seam
  check covers a `WorldServer` member read through it. A closure over an
  object the server handed out is covered only by reading the wiring.
- A service the composition root injects into the server and also uses on the
  pump is outside the outbox. Those are named in
  [what the tick calls](#what-the-tick-calls-outside-the-authority).
- The seam check covers the pump. Another thread that reads a server's
  property without `WorldServer.ExecuteAuthorityOperation` was unsafe before
  this change and still is.
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

The review found readers this table's first draft missed. Each reads the
authority through a closure, which the reference search does not follow.

| Reader | What it reads today | Where it moves |
|---|---|---|
| A state-bound value in `WorldClient.Definition`, and every `WorldStateReader` read of that document | The server's arena, through the document's live binding | The sink's own values ([above](#what-a-delivered-document-may-reach)) |
| Authored commands (`CommandRegistry.AuthoredCommandsFor`), asked on every button press and by `WorldOverlayFeed` | `WorldServer.Configuration`, through the source `WorldPostBuildWiring` installs | A command set published at each collect and at each install outside a tick |
| `HostLog`'s tick anchor, on every line any thread writes | `WorldServer.NextInputTick` | The completed boot tick, published at each collect as one atomic value |
| `WorldTickScript.CompletedTick`, read by the frame recording as an iteration opens and by the `benchmark.tick` reading | `WorldServer.NextInputTick` | The same published tick |
| `WorldDiagnosticOverlay.Snapshot`, on every frame a diagnostic view is on | `WorldDiagnostics.Revision`, `WorldDiagnostics.Tick` and `WorldDiagnostics.DrawnReady`, and a report it starts from the frame, which copies the population and the definition | The inputs are copied at the collect while a diagnostic view is on, and the overlay starts and polls its analysis over that copy |
| A screen's machine clock (`IWorldMachineHost.PresentedTicks`) | Four fields the machine host writes one after another as the tick advances it | A value record the host publishes whole, held in the binder's table; never a reference to the host |

Two readers are found by their effect and not by a server reference.

- **A seat's look.** `WorldClient.AdvanceSeatViews` integrates stick and gyro
  look once a host step, in prepare, and `WorldSeatViewState.PresentedLook`
  takes back the part of that step the frame has not reached. With the tick
  in flight, the frame would present the new step against the previous tick's
  interpolation and then step backward when the tick arrives. So a seat's
  step is held as pending only while a tick that will carry it is in flight:
  the wave handed over in this host step holds a tick of the local world the
  seat is routed to. It is presented when that tick is collected. In every
  other case the step is presented at once, as now: the routed world is
  paused or stopped, it is not due in this host step, or its clock is another
  process's (`WorldAuthorityEndpoint.ClockOwnedHere` is false). A world that
  steps less often than the host therefore never accumulates look steps.
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
| Addon guests (`IWorldAddonHost`) | Inline; step 3 confirms on the headless boot that a guest mounted on the main thread ticks on the step thread |
| `WorldServer.Profiles`, `WorldServer.Neighbours`, `WorldServer.RebuildDocuments`, `WorldServer.TransferForwarder` | Inline; the pump uses them only inside the window |
| `WorldServer.Envelope` | Reads the published record |
| `HostFrameWork` measurements | The step thread binds its own; collect folds its parts into the iteration |
| `WorldNarrationScope` | Set by the step thread for each tick of a wave |

One thing the tick reaches is not the authority's and is not safe where it
is. `WorldAdjacencyFields.BeginTick` runs inside the tick and resolves a
neighbour through the host (`WorldInstanceHost.TryResolveObservedProjection`).
That reads and writes the host's tables, can start a world instance, and
attaches or disposes an observation, whose first delivery is made straight to
the sink. The host's tables are the pump's. So the resolution, the start, the
attach and the pin all move into prepare, on the pump, after the previous
wave's collect has replayed the mirrors' deliveries. Prepare then publishes
one immutable adjacency record for the tick. The tick reads that record, and
so does the frame, which is the reader the table above already moves.

The job pool expects the step thread. Each `JobClass` holds its own fork, so
the frame's fork and the tick's run side by side and both are given workers,
and the pool's worker count leaves a processor each for the frame thread, the
step thread and the audio thread (`JobWorkers.MachineWorkers`).

The frame's fork puts one condition on this design. Its partitions read
`WorldStateMirror` without a lock, so the mirror's install, refresh and apply
must not run while a frame fork is in flight. They do not move to the step
thread: a delivery reaches the mirror only through the outbox's replay, which
runs on the frame thread in a collect, and a collect runs in the step
section, after produce or inside the pacing wait, never inside produce, where
the frame's fork is opened and joined. The replay is the one place a delivery
writes the mirror, so the rule is checked there: it refuses to run while the
frame class has a fork open. A frame fork held open across iterations would
therefore hold the collect until it joins, and needs no double buffer.

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

**Outputs with nothing to interpolate.** A tick also delivers things a frame
shows whole: a state cell a HUD binds without easing, a body's facts and the
animation they trigger, a spawn or a teleport, a cue, a seat's reconciled
input answer, a machine's video frame. Today the frame that stepped a tick
shows these. With the tick beside the frame they show one frame later, a
full frame period, even when the tick and the produce together fit in one
frame. That is the cost of never waiting, and it is the larger of the two
latencies this design adds.

**Look, camera and cursor.** Pointer look moves the seat's live look when the
event arrives and shows at once, as now. Stick and gyro look integrate once a
tick and are presented with the tick that carried them
([above](#frame-thread-readers-and-where-each-moves)). The cursor is drawn
from presentation state.

**The budget.** On [the benchmark's latency column](benchmark.md#presentation-timing-and-admission-stage-8),
at an equal frame rate, between-tick pointer look reads no later than it does
on the interleaved loop, and tick-integrated motion and the outputs with
nothing to interpolate read no more than one frame period later. The lead takes both readings, before and after, in a
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
  share: prepare and collect. The tick's own cost is timed on the step
  thread and reported on the timeline's separate tick lane
  (`HostFrameTimeline.NoteTick`); timed on the pump it would be the collect's
  few microseconds. A step is counted in the iteration that collected it,
  wherever in the iteration that was, so an iteration that finished a step
  after produce is not read as a stall by the benchmark. The hint that the
  next iteration will step (`HostIteration.StepsNext`) leaves out the step
  already open. Each iteration reports whether a tick was in flight while it
  produced.

## A hub of a dozen live worlds

Every world instance beside the boot world is already a tick in the second
wave. A dozen worlds in view add a dozen ticks to that wave and nothing to
the frame except their collects, which apply deliveries the client asked
for.

The step thread executes the wave serially today. The job memo's step T4
forks it: the step thread becomes the fork's parent, and each world's tick is
a partition. The rule a fork needs is already in place by then. A world reads
its neighbours through mirrors that the neighbour's tick delivers to
(`WorldInstanceHost`'s observation sessions), and the outbox defers those
deliveries to the collect, so every world in a wave reads its neighbours as
of the previous collect whichever tick finishes first.

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
- **`WorldOutputHub`.** Kept as the place deliveries are composed, with the
  outbox at its last hop. A mailbox sink that copies every delivery was
  considered and rejected: a snapshot lends memory at three depths, and the
  outbox holds the loan until the collect, copying only when one lender
  delivers twice in a tick.
- **`WorldSessionMirror` and `PublishBuffer<T>`.** The mirror tolerates a
  delivering thread. It is still deferred here, so that a camera reading a
  body through a mirror and a scene drawing it from `WorldClient` see one
  tick.
- **`JobPool`.** A fork joins in its caller, so the pool cannot run a whole
  tick beside the frame. The step thread is the parent the tick's forks
  need, and the pool already gives the tick a class of its own
  (`JobClass.Tick`).
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

Three stages. Every step lands on its own and carries a check that reads
load-independent evidence. Through the first two stages the pump waits for
every wave, so the loop behaves as it does today and each step is safe alone.
The steps of the second stage do not depend on one another and can run in
separate lanes.

**Stage one: no behaviour moves.**

1. **The frame's cheap readers.** `WorldClient.UpdateRenderPoses` interpolates
   the moving set, and `HostLoop` resolves the step rate once an iteration.
   Check: `world.counters` reads the poses a frame interpolated beside the
   active bodies, and at rest the first is zero.
2. **Published host values.** `WorldHostStep` publishes the completed boot
   tick, the step rate and the stall flag at each collect, and `HostLog`'s
   anchor, `WorldTickScript` and `HostLoop` read those. Check: `puck
   references` finds no reader of `WorldServer.NextInputTick` or
   `WorldServer.Configuration` in a closure the frame or another thread calls.
3. **Three parts and the step thread, with the pump waiting.**
   `WorldServerStepShell` splits into prepare, execute and collect.
   `WorldInstanceHost` owns the step thread and hands it each execute, and the
   pump waits for it exactly as long as it runs the tick today, so nothing
   overlaps. The timeline's tick lane is measured on the step thread. Check:
   the headless debug world boots and answers `body.where`; `puck test
   --reproduce` passes at `--jobs 1` and at other worker counts with the
   base's state hashes, since this step claims to change nothing.

**Stage two: the doors.**

4. **The outbox**, at the last hop, with the three callbacks and the save
   effect's split. Check: a law delivers twice to one session sink in a tick
   and reads both records intact at the collect; the scenarios that fire cues
   keep their hashes.
5. **A delivered document bound to its sink's values.** Check: the
   conformance law of [the presentation view](runtime-and-delivery.md#the-presentation-view)
   over the debug world, and `puck references` finds no `WorldStateReader.Bind`
   outside the server.
6. **Adjacency in prepare**, with the tick's immutable record. Check: a law
   steps a world whose neighbour restarts and finds no call into
   `WorldInstanceHost` from inside the tick; the crossing scenarios pass
   `puck test --reproduce`.
7. **The link.** Queued submissions mint when the window opens; `Query` and
   `SubmitSession` refuse outside it; the roster reads published profiles.
   Check: a law joins a controller from a window event while a tick is held
   and seats it in its preferred slot.
8. **Presentation out of the composition root**, into a project that cannot
   reference the server or the machines, with the published tick record, the
   machine presentation table and the diagnostic copy. Check: the project
   builds under its architecture profile, which is the proof that no frame
   type holds the authority.
9. **The window and the seam check**, on every member the tick mutates.
   Check: a law reads `WorldServer.Population` from a registered pump outside
   the window, which throws; the windowed debug world plays the benchmark
   route with no throw (the lead's run).

**Stage three: the wait ends.**

10. **The loop stops waiting for the last due tick.** `HostLoop` produces
    while a step is in flight outside lockstep, polls after produce only to
    resume an open host step, waits on a tick a scheduled capture arms, and
    counts a step in the iteration that collected it; `FixedStepPump` takes
    the next due step after a resumed one in the step section; the two waves
    replace the serial instance loop; the seat's look step is held while its
    tick is in flight. The path on which the pump waits for every tick is
    deleted. Check: `host.frames` shows iterations that produced while a tick
    was in flight and the tick lane's cost apart from `simulate`; `puck test
    --reproduce` passes at every worker count, with the scenarios the second
    wave reorders re-recorded.
11. **The pacing wait's wake.** A finished wave wakes `HostWaits.Pace`, which
    resumes the open host step. Check: with a tick longer than produce, a
    host step's two waves complete within one iteration, read from
    `host.frames`' steps per iteration. The swap chain's own wait is not
    serviced until [C0.6](engine-job-system.md#the-steps) puts it through the
    door.
12. **The documents.** The hosting reference, the rendering register's
    pipelining decision and the `puck-world` skill describe the new loop; open
    question 7 and item 1 of the CPU frame are deleted.

### What the lead measures

These need a quiet machine and a window, which a lane does not have.

- **Frame time**, after stage three. The benchmark route on the debug world
  before and after: `host.frames`' interval and its `simulate` and tick-lane
  lines. Expected: the interval falls by about the tick's cost on iterations
  that stepped, and `steps.shed` does not rise.
- **Input to display**, after stage three. The benchmark's latency column,
  both rows, before and after, against
  [the budget](#input-and-the-time-from-input-to-display).
- **The hand-off's own cost**, after step 3. The time from the pump's
  hand-off to the step thread's first instruction, read from the tick lane
  against the wave's execute time. It is expected to be tens of microseconds,
  by reasoning about an event wake; no reading has been taken.

## The review's findings

The review argued from source. The code behind H1, H3, H7 and L2 was
reopened before its ruling. H2, H4, H5 and the mediums were accepted on the
code the review quoted, so the step that builds each one reads that code
first and corrects this table if it differs.

| Finding | Ruling |
|---|---|
| **H1** [high] A delivered document is the server's own, bound to its live arena, so the frame reads the arena the tick writes. | Accepted. `WorldDocument.DeliverPending` hands sinks the document `WorldStateReader.Bind` bound, and `WorldClient.DeliverPresentation` copies the binding. A local sink now receives a document bound to its own values ([what a delivered document may reach](#what-a-delivered-document-may-reach)); step 5. |
| **H2** [high] The adjacency source changes the host's tables, starts instances and attaches observations from inside the tick. | Accepted. Resolution, start, attach and pin move to prepare, and the tick and the frame both read one immutable record; step 6. |
| **H3** [high] An outbox at the hub loses a tick's cues, replays session sinks against later state, and lets a second delivery overwrite the first's scratch. | Accepted. Composing stays inline and the outbox sits at the last hop, copying a lent span only when it holds an earlier one from the same lender; step 4. |
| **H4** [high] Queuing the link breaks callers that read a reply before the call returns, and a tap fired before the queue races the tape. | Accepted. Only submissions that need no answer are queued, before the envelope is minted; `Query` and `SubmitSession` refuse outside the window and their callers move; step 7. |
| **H5** [high] Readers the table missed: authored commands, the log's tick anchor, the tick script's tick, the diagnostic overlay, the machine clock. | Accepted, each added to [the table](#frame-thread-readers-and-where-each-moves). The first draft's claim that the diagnostic report copies inside the window was wrong for the overlay; steps 2 and 8. |
| **H6** [high] A poll after produce that starts a new host step closes input without that iteration's capture or console drain. | Accepted. Those polls only resume an open host step ([the pump's waits](#the-pumps-waits)); step 10. |
| **H7** [high] Holding the look step until a collect freezes look while paused, on a slower world and on another process's clock. | Accepted. The step is held only while a local tick that carries it is in flight; step 10. |
| **H8** [high] A lost wake and one thread too many once two classes fork at once. | Accepted, and owned by the lane that owns `JobPool`. |
| **M1** [medium] The seam check misses members and cannot see a reference kept past the window. | Accepted, and the order of preference reversed: presentation moves where a server type cannot be named, and the check covers what remains; steps 8 and 9. The review's third proposal, a `ref struct` window that hands out `ref struct` views of the population, is not taken: every console verb reads the population through the same members, and moving the frame's types out removes the readers the views would guard. |
| **M2** [medium] A deferred save captures end-of-tick state and compacts entries made after it; a socket task's inline delivery reaches the client between waves. | Accepted on both. The save's capture and compaction stay at the rule; the outbox records whenever the deliverer is not the pump inside the window; step 4. |
| **M3** [medium] The second wave moves more than the draft said. | Accepted. The five changes are listed in [waves](#waves), and the false sentence is gone. |
| **M4** [medium] Steps collected after produce are counted in no iteration, the next-step hint reads an accumulator the open step has not left, and the tick's cost is timed on the pump. | Accepted. A step is counted, and `simulate` charged, in the iteration that collected it; the hint subtracts the open step; the tick lane is timed on the step thread; steps 3 and 10. |
| **M5** [medium] Outputs with nothing to interpolate arrive a frame later, and a scheduled capture arms a frame late. | Accepted. [The budget](#input-and-the-time-from-input-to-display) now states the frame, and the pump waits on a tick the schedule arms a capture on. |
| **L1** [low] The first control phase drains before the pump is registered, and the registration lasts the whole loop. | Accepted. The pump is registered before the loop, and the window is its own scope. |
| **L2** [low] The draft's account of `JobPool` was out of date. | Accepted and rewritten. |

---

[Plans](README.md) · [Open items](open-items.md#the-cpu-frame) ·
[The engine job system](engine-job-system.md)
