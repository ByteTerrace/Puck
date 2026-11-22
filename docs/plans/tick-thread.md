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

The work landed in three stages. The first moved no behaviour: the tick's
parts were separated and the frame thread's cheap readers fixed. The second
closed each place the frame read the authority, one structural door at a
time, while the loop still waited for every tick. The third stopped the wait,
and that is where the frame time falls. All three are built for every host
shape; [the steps](#the-steps) name what is left (the wave, and what the lead
measures).

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
   pump because both can reach the host's own tables, and with the record the
   replay tape's injection and the ordered submissions, which must be applied
   before the neighbours are sampled ([the prepared tick](#the-prepared-tick)).
2. **Execute, on the step thread.** The part that runs under
   `WorldServer.AuthorityGate`: `WorldTick.TryAdvance`, the mutation journal's
   flush, the journal fold and the tape's tick close. A wait inside the tick
   (a machine's drain, a fork's join) is waited out on the step thread, under
   the stall deadline the tick already has.
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
| The same, an earlier tick of a catch-up burst | The pump's wait allowance (`HostWaits.PumpAllowance`) | Waits, because the next tick's input needs this tick's results; past the allowance it produces and resumes the tick next iteration |
| Lockstep (`HostPacing.StepsOneTickPerFrame`), a capture's hold, the offscreen host | The tick's stall budget | Produces only once the tick is collected, as before |
| A tick the capture schedule arms a capture on | The allowance | Produces once the tick is collected, so the capture is served by the first frame after its tick |
| Headless and paced, the last tick due | None: it polls | There is no frame; the pump goes on to its pacing wait, which the finished tick wakes |
| Headless and unpaced | The stall budget | There is no frame, and the host's time is its iteration count |

[The loop beside the tick](#the-loop-beside-the-tick) records which of these
rows are built.

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

A record never holds a loan. A snapshot lends memory the next snapshot of the
same server or session sink overwrites, and one tick can deliver twice to one
sink (a definition delivery primes a snapshot, and the tick's own snapshot
follows). So the outbox copies every lent span into rented arrays when it
records, and returns them once the record is replayed. The first design
copied only when it already held an earlier record from the same lender; the
built one copies always, which is simpler and costs about 2 MB a tick on the
debug world. An optimization is open: a lender could hand over the copy only
when it is about to overwrite memory a record still names.

The outbox records while a tick of its server executes, and at any other time
when the delivering thread is not the pump inside its boundary window
(`WorldOutputHub.Records`, over `WorldLinkBoundary.DeliversHere`). That covers
the step thread during a tick. It also covers a socket task that applies a
grant or a session lever through `WorldServer.ExecuteAuthorityOperation`
between two ticks, whose delivery would otherwise reach `WorldClient` on the
socket thread while the frame is being produced. Before a pump registers, a
host boots on one thread and delivers at once; after it unregisters, only the
thread that was the pump does. A server no host admitted has no window and
defers only while its tick executes.

The pump collects (`WorldOutputHub.Collect`) at these points, and nowhere
inside produce: in `WorldServerStepShell.Prepare`, before a new tick's mailbox
drain; in `WorldServerStepShell.Collect`, before any hook reads the completed
tick; in `WorldServer.DrainAdministrative`, for a world that is not stepping;
and around the synchronous `WorldTick.Advance`. A delivery therefore reaches
`WorldClient` and a local mirror only on the pump, in the step section or in
the pacing wait's resume. The client's body journal and the state mirror's
change log, which are not synchronized and are read by the frame, rest on
that and need no gate of their own.

Three more things the authority calls are the pump's, and leave the same way:

- the completion of a submission that came through `LoopbackTransport`. Only
  that door routes its completion (`IWorldServerHost.Submit`); the public
  `WorldServer.Submit`, which socket peers and forwarded travellers use, keeps
  its completion on the thread that raises it;
- `WorldServer.EchoTap`, which feeds toasts and the editor;
- the part of `WorldServer.SaveEffectTap` that reads the live presentation
  levers and writes the file. The authority's half is captured where the rule
  fired (`WorldSaveCapture`, over `WorldSessionCapture.Capture`), so a save
  still records the state at that point of the tick. The journal compacts at
  the collect, once the file is written, and only through the entries the
  capture saw (`WorldSaveCapture.CompactSaved`), so `world.undo` keeps the
  entries made after it and a refused write compacts nothing.

Each is wrapped where it is assigned, so no call site inside the tick can
invoke it directly. The taps the replay tape attaches
(`WorldServer.MutationTap`, `WorldServer.RebuildTap`,
`WorldServer.ScreenOpTap`, `WorldServer.ServerEventTap`,
`WorldServer.LinkDeliveryTap`) stay inline: the tape belongs to the authority
and records in tick order. Narration stays inline too, since
`WorldOutputHub.Narrate` already takes its own lock for lines that socket
tasks write.

The replay hands sinks the tick's completion time. The thread that executes a
tick stamps the time it finished (`WorldExecutedTick.CompletedAt`), the
collect states it for the length of the replay (`WorldDeliveryClock.ReplayAt`),
and `WorldClient` and `WorldSessionMirror` stamp a snapshot's arrival from
`WorldDeliveryClock.Arrival` instead of reading the clock, so
`WorldSessionMirror.ResolveInterpolationAlpha` yields the fraction a frame
that applied the tick at once would. A delivery made directly arrives now.

### What a delivered document may reach

A record in the outbox must not lead back into the authority. The server
binds its live document to its own arena (`WorldStateDocumentValues.BindLive`,
`WorldStateReader.Bind`) and delivers that same object. Kept as delivered, a
state-bound value the frame reads in `WorldClient.Definition` would read the
arena the tick is writing, and no seam of `WorldServer` would be crossed, so
no check would see it.

The document a local sink holds is therefore bound to that sink's own values,
never to the server's arena. The client already keeps those values for an
authority in another process: a delivery that changes only values leaves the
declaration fixed, and the snapshot that follows carries the cells that moved
(`WorldStateStamp.ArenaValues`, `WorldDocumentStateView.ApplyFieldCells`). The
local client takes the same path. Each client-side sink holds only what
`WorldDocumentStateView.Adopt` returns. Adopt binds every state-bound value
again (`WorldStateDocumentValues.BindLive`) to a `WorldLiveValues` of the
sink's own, over the arena its snapshot cells fill, read at its delivered
tick. A mirror whose deliveries arrive on another thread passes its follow
gate, which every bound read takes. No client document is registered with
`WorldStateReader.Bind`. The bind memo answers a row delivered unchanged with
the copy it made before (`IWorldDocumentRewriter.Reuse`), so adoption costs
what the delivery changed, and reference comparisons still see only changed
rows. This is the last cut of
[the presentation view](runtime-and-delivery.md#the-presentation-view), taken
for the local client's state values, and step 5 built it.

### From the client to the authority: the boundary window and the link

The pump may touch the authority in one window: inside the loop's control
phase and step section, while no wave is in flight. Console verbs, the input
snapshot's handlers, prepare and collect all run there, as they do now. The
window is an explicit scope the loop opens and closes on the pump, held in a
thread-static, and the pump is registered once before the loop's first
iteration, so the first control phase is inside the rule like every other.
It is `WorldLinkBoundary`, one for each `WorldInstanceHost`, which hands it to
every world it admits (`WorldServer.Boundary`) and to its step thread. A tick
handed to the step thread closes the window to the thread that handed it over
until that thread collects it (`WorldLinkBoundary.HandedOver`,
`WorldLinkBoundary.Collected`), wherever in the loop the collect falls, so a
poll that finds the tick still executing reads nothing either.

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
  `WorldServer.ThrowIfTickPending` carries the window
  (`WorldLinkBoundary.Refusal`), and every member the tick mutates passes it
  or its window-only half (`WorldServer.ThrowIfOutsideWindow`, for a member
  the stepping machinery reads while a tick is held across wait slices):
  `WorldServer.Grants`, `WorldServer.Configuration`, `WorldServer.Tick`,
  `WorldServer.SolidField`, `WorldServer.Input`,
  `WorldServer.CompletedEngineTicks`, `WorldServer.DeliveryTime`,
  `WorldServer.DocumentVersion`, `WorldServer.NextInputTick`,
  `WorldServer.SolidRevision`, `WorldServer.Search`,
  `WorldServer.JournalLength` and the rest the review listed, and the gate
  behind `WorldServer.ExecuteAuthorityOperation`. The throw does not depend on
  timing: produce is outside the window always, so a reader throws on its
  first frame whether or not a tick was running, and the exception's stack
  names it.

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
- A world another process steps delivers on its socket task
  (`WorldRemoteAuthority`'s observation loop), outside the outbox, into a
  `WorldSessionMirror`. The mirror is written for that: its tables publish
  through volatile writes and whole images, and its dense sets are read under
  its write gate. Its presentation state mirror has one writer,
  `WorldSessionMirror.FollowState`, which does not write from inside a fork's
  partition (`JobPool.InPartition`), so a delivery that lands while a frame's
  fork runs waits for the next follow. What is left is a per-slot read (a
  pose, a fact) that can see a delivery half applied, as it could before.
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
| `WorldScreenBinder.MachinesOf` and its callers | A world's `IWorldMachineHost`, for its presentation clock and a screen's video output, on every frame | Each running world's `WorldMachinePresentation`, captured whole at the collect (`IWorldMachineHost.CapturePresentation`) into the feed's `WorldMachineTable`; the clock is `WorldMachinePresentation.PresentedTicks` |
| `WorldScreenBinder`'s boot definition and shared-scene proof | `WorldServer.Definition` and the instance's server, per frame | `WorldClient.Definition`, and a proof the collect checks and publishes with the session (`WorldObservationAdmission`'s `ProofHolds`) |
| The binder's sessions | The observation sessions the instance host admits and releases | The feed's `WorldObservationTable`: a `WorldObservedSession` for each, with the `WorldObservationAdmission` the composition root builds (its token, tier, proof, window and disclosure projection) |
| The binder's session and infinity feeds | `WorldServer.Envelope`, configured from the frame and read by the tick's capacity check | A copy-on-write `WorldRenderEnvelope`: the frame replaces the configuration whole and the tick reads the one it finds |
| Machine audio and the machine console's facades | The machine host, from the audio director and from the binder | The audio reads `WorldMachinePresentation` from the table; the facades moved to the console modules, which run in the window |
| The binder's backend export | A Direct3D 12 type named in the binder | `IGpuSurfaceExportProvider`, which the composition root supplies |
| `WorldRenderRoot`'s device-profile step | `WorldServer.Definition` on the first frame | `WorldClient.Definition` |
| `PresentationReadings`' `seat.<n>.place` | `WorldServer.Events` while the HUD binds | A value per seat, published at collect |
| `WorldHudBindingResolver` | `WorldPopulation` counts and `WorldHistory`'s cursor | Values published at collect |
| `WorldIterationClock` | The server's document version after each frame | `WorldClient.DocumentVersion` |
| `WorldCaptureScheduler`, when a frame serves a capture | The definition, the completed tick and the solid field | The delivered tick and definition; a still's state hashes are taken at the next window |
| `WorldDiagnostics` | The server, when a report polls from the frame | The copies `WorldDiagnostics.BeginReport` takes inside the window |
| `IWorldAdjacencySource`, pinned by `WorldFramePresenter`, `WorldAdjacencySceneEmitter` and `WorldContinuum` | Tick-side state the tick rewrites | One immutable record the tick publishes by a reference write, as [the presentation view](runtime-and-delivery.md#the-presentation-view) already plans |
| `WorldSeatViewInput` | The routed document through the instance host, whose fallback was the boot server's definition, while input is captured | The routed endpoint's delivered document (`WorldSeatAuthorityRouter.DefinitionOf`) |
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
| A screen's machine clock | Four fields the machine host writes one after another as the tick advances it | `WorldMachinePresentation.PresentedTicks`, on a value the host publishes whole into the feed's `WorldMachineTable`; never a reference to the host |

Step 8's first slice built the rows that read the feed. The composition
root's collect, `WorldPresentationCollect`, runs at the seat post-step, after
every instance has stepped and inside the window, and fills
`WorldPresentationFeed` with three immutable values: the tick record
(`WorldTickRecord`: the completed boot tick, the installed document version,
body counts, the history window, seat places and authored commands), the
machine table and the diagnostic copy (`WorldDiagnosticCopy`). The HUD binding
resolver, the iteration clock, the diagnostic overlay, the seat view input and
the pointer and overlay feeds moved into `Puck.World.Presentation`, whose
architecture profile refuses a reference to the server or the machines. The
screen binder reads its machine output and clock from the feed, and the render
root its device profile from `WorldClient.Definition`.

Step 8b moved the screen binder, the render root, the audio director, the
device profile state and the inspector into `Puck.World.Presentation` as
well, so the same profile refuses them a reference to the server or the
machines. Where that guarantee stops is the closures the composition root
wires into them: the disclosure projection of a `WorldObservationAdmission`,
which the tick runs inside the envelope's capacity check; the source tuning;
the export provider; the inspector's reload error; and the binder's reader
of the seats' remote routes (`WorldScreenBinder.RemoteSeatEndpoints`). Those
are found by reading the wiring, and step 9's window covers a `WorldServer`
member read through one.

Two readers are found by their effect and not by a server reference.

- **A seat's look.** `WorldClient.AdvanceSeatViews` integrates stick and gyro
  look once a host step, in prepare, and `WorldSeatViewState.PresentedLook`
  takes back the part of that step the frame has not reached. With the tick
  in flight, the frame would present the new step against the previous tick's
  interpolation and then step backward when the tick arrives. So a seat's
  step is held as pending only while a tick that will carry it is in flight:
  the host step being prepared hands over a tick of the local world the
  seat is routed to (`WorldSeatViewState.CompleteTick`, asked through
  `IWorldEmbodiedSeats.AdvanceSeatViews`). It is presented when that tick is
  collected (`IWorldEmbodiedSeats.PresentSeatLook`, from
  `WorldInstanceHost.TryStepAuthority`), and at the end of the host step if
  the world took no tick after all. In every other case the step is presented
  at once, as before: the routed world is paused or stopped, it is not due in
  this host step, or its clock is another process's
  (`WorldAuthorityEndpoint.ClockOwnedHere` is false). A world that steps less
  often than the host therefore never accumulates look steps.
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
| Addon guests (`IWorldAddonHost`) | Inline; a guest mounted on the main thread ticks on the step thread, as the debug world's guests do |
| `WorldServer.Profiles`, `WorldServer.Neighbours`, `WorldServer.RebuildDocuments`, `WorldServer.TransferForwarder` | Inline; the pump uses them only inside the window |
| `WorldServer.Envelope` | Reads the published record |
| `HostFrameWork` measurements | The step thread binds its own; collect folds its parts into the iteration (`HostFrameWork.Fold`) |
| `WorldNarrationScope` | Flows with the execution context the lane runs each unit in (`JobLane`); a wave of several ticks sets it for each |

One thing a tick needs is not the authority's: its neighbours. Resolving a
neighbour goes through the host (`WorldInstanceHost.TryResolveObservedProjection`),
which reads and writes the host's tables, can start a world instance, and
attaches or disposes an observation, whose first delivery is made straight to
the sink. The host's tables are the pump's. So the resolution, the start, the
attach and the pin run in prepare, on the pump (`WorldAdjacencyFields.Prepare`,
from `WorldTick.PrepareAdvance`), after the previous collect. Prepare hands
the tick one immutable array of projections. The tick reads that array, and
so does the frame, which is the reader the table above already moves
([the prepared tick](#the-prepared-tick)).

The job pool expects the step thread. Each `JobClass` holds its own fork, so
the frame's fork and the tick's run side by side and both are given workers,
and the pool's worker count leaves a processor each for the frame thread, the
step thread and the audio thread (`JobWorkers.MachineWorkers`).

The frame's fork puts one condition on this design. Its partitions read
`WorldStateMirror` without a lock, so the mirror's install, refresh and apply
must not run while a frame fork is in flight. They do not move to the step
thread: a delivery reaches the client's mirror only through the outbox's
replay, which runs on the frame thread in a collect, and a collect runs in
the step section, after produce or inside the pacing wait, never inside
produce, where the frame's fork is opened and joined. Nothing checks that at
the replay: it rests on a fork joining in the call that opened it
(`JobPool.Fork`). A frame fork held open across iterations would need the
replay to refuse while the frame class has a fork open, which is not built.
A session's state mirror, which a socket task's delivery can reach, is
guarded at its one writer ([above](#the-determinism-invariant)).

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
  already open (`FixedStepPump.UnbegunTicks`). The frame's own step count
  (`HostIteration.Steps`) is the steps collected since the frame before, and
  the reading counts the iterations that produced while a step was in
  progress (`HostFrameTimeline.NoteProducedBesideStep`).

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

## The step thread as built

Every authority tick a host steps, the boot world's included, executes on one
step thread. Stage one built that with the loop waiting for each tick;
[the loop beside the tick](#the-loop-beside-the-tick) records where it no
longer waits.

- **Three parts.** `WorldServerStepShell.Prepare` closes the tick's input on
  the pump ([the prepared tick](#the-prepared-tick)).
  `WorldServerStepShell.Execute` takes the authority gate and runs the tick
  over that input. `WorldServerStepShell.Collect` publishes the completed
  tick to the caller's hooks on the pump. `WorldServerStepShell.TryStep` runs
  them in order and takes the host's `WorldStepThread` to execute on. Without
  one (a law, a replay's shadow server) all three run on the calling thread.
- **The lane.** `JobLane`, in `Puck.Hosting.Jobs`, is a thread that runs one
  unit at a time for one owner, which posts a unit and collects it. Its
  thread starts with the first unit and idles as a consumer between units. It
  runs each unit in the execution context the unit was posted from, so every
  value that flows with a logical call (`WorldNarrationScope`, the boot and
  projection work attribution) reads on the step thread as it did on the
  pump. What a unit throws is rethrown to the owner when it collects.
- **The host.** `WorldInstanceHost.TryStepAuthority` is the one door to the
  step thread. `WorldHostStep` steps the boot world through it,
  `WorldInstanceHost.TryStepInstances` every other instance, and the silo's
  rows go the same way. Disposal stops the step thread first. If a tick is
  still executing when the stop's deadline passes, the host says so and
  leaves its worlds to the process's exit, as
  [the teardown rule](engine-job-system.md#teardown) requires.
- **The unit is one slice of one tick, not yet a wave.** A tick whose wait
  outlasts its slice still answers pending, on the step thread, and releases
  the gate. The pump runs its control phase and hands the next slice over, as
  [an incomplete tick](engine-job-system.md#an-incomplete-tick) describes.
  The wave, when it is built, replaces the slice.
- **Published host values.** `WorldHostStep.RatePerSecond` and
  `WorldHostStep.StepInterrupted` are published each time the boundary window
  closes with no tick in flight (`WorldLinkBoundary.PublishOnClose`), so the
  loop, which reads them outside the window, reads the values of the boundary
  it last stood at; the console's control phase closes its window before the
  pump compares the rate. The frame's values are collected the same way, by
  the same hook, when no step is in progress (`WorldSimulation`). The silo's
  rate is a value its host keeps, and reads no row.

### The prepared tick

Step 6 moved more than the adjacency record into prepare. A tick's neighbour
projections must be sampled after its ordered submissions are applied, and
the sampling reaches the host's tables, so both run on the pump, with the
replay drive's injection ahead of them. `WorldServerStepShell.Prepare` does,
in this order: the mailbox drain; then, under the authority gate, the replay
tape's injection (`WorldReplayTape.InjectDriveTick`) and
`WorldTick.PrepareAdvance`, which applies the ordered submissions and samples
the projections into an immutable array. The step thread's execute then runs
the tick over that array (`WorldTick.TryAdvance`). The description of prepare
and execute in [three parts of a tick](#three-parts-of-a-tick) is read with
this: the injection and the ordered drain belong to prepare.

That split leaves a span the single-threaded step never had. After the pump
leaves the gate and before the step thread takes it, the gate is free, the
tick's ordered submissions are applied and its neighbours are sampled. A
submission another thread applied there (a socket task's grant, a session
lever) would land after the sampling and before the tick, a place no replay
can put it: a replay injects a tick's recorded entries before that tick's
prepare.

So a prepared tick is a held tick. `WorldTick.HoldPrepared` marks the tick
held for the preparing thread, and `WorldTick.TryAdvance` releases the hold
once the executing thread has the gate. Over the span, a thread other than
the owner that enters the gate waits for the boundary without the gate
(`WorldTick.AwaitsBoundary`), and a submission from a thread that cannot wait,
because it already holds another authority's gate, queues for the boundary or
is refused with the reason (`WorldTick.BlockedReason`), exactly as for a tick
held across wait slices. `WorldServerStepShell.Prepare` is the only producer
of a `WorldPreparedTick`, and it always holds, so no executor can run a tick
whose span was open. Where the guarantee stops: a caller that takes the gate
with a plain `lock` and never asks whether a tick is held (`WorldTick.EnqueueIntent`)
still enters; an intent queued there is taken by this tick or the next, as it
always could be.

Stage three keeps the hold and widens its use. A wave prepares all its ticks
before any executes, so every tick of the wave is held from its prepare, and
a tick's submission into a later tick's authority queues behind that tick's
closed input. That is the order [waves](#waves) already states.

### Two things the later stages keep

Two things in a tick assumed that the thread running it was the pump. Both
still mean the pump, and both hold now that the loop no longer waits.

- **A held tick's owner is the thread that handed it over.** A tick held
  across wait slices names an owner: the one thread that never waits for the
  tick's boundary, because it brings the boundary by handing over the next
  slice (`WorldTick.AwaitsBoundary`, read by `WorldServer.EnterAtTickBoundary`).
  If the step thread became the owner, a console line the pump runs against a
  held tick would wait up to the synchronous budget for a boundary only the
  pump can bring. The owner is `WorldTick.SliceOwner`, and that one
  expression is used both where the held-tick record is written and where an
  entering thread is compared with it. Only `WorldServerStepShell.Execute`
  sets whom a thread executes for (`WorldTick.ExecuteFor`), from a
  `WorldPreparedTick` that only `WorldServerStepShell.Prepare` mints with the
  preparing thread's identity, so no executor can become an owner by running
  a slice. The guarantee stops at a caller that runs `WorldTick.TryAdvance`
  itself on another thread; the synchronous doors that do
  (`WorldServer.Advance`) run on their caller's own thread and own what they
  hold. Beside the tick the pump is still the thread that hands a tick over
  and the thread that runs console lines, so it stays the owner. A thread
  other than the pump that enters the gate while a tick runs waits on the
  gate's lock as before.
- **The pump's wait for the step thread is its own work.** It goes through
  `HostWaits.OnDelegatedWork`, which is neither clipped to the pump's
  allowance (`HostWaits.PumpAllowance`) nor charged to it. Charged, every
  tick would spend the allowance the frame's waits are sliced from, and
  frames would answer waiting more often. The form takes the worker thread's
  `WaitConsumer`, which only that thread can mint, so a wait on another
  subsystem cannot borrow it by accident; `JobLane.Collect` is its one
  caller. Its bound is the stall budget (`WaitDeadline.SynchronousBudget`),
  counted from the hand-off. A slice's own waits end sooner: by the slice
  deadline the pump computed from its allowance, or by the tick's stall
  deadline, which freezes the tick. So a slice that has not answered within
  the budget is computing, not waiting. The tick's own waits now run on the
  step thread, which is not the pump, so they are no longer charged to the
  allowance either. The waits in [the pump's waits](#the-pumps-waits) that
  take the allowance use the same form with it as their deadline, and stay
  uncharged.

### The loop beside the tick

Stage three is built for every host that paces to the wall clock, with a
frame or without: the windowed World and the headless one. There the pump no
longer waits for the last tick due. A host that steps one tick per frame
(lockstep, the offscreen host, an unpaced headless run) still waits for each
tick, since its frame shows exactly the tick it stepped.

- **The pump polls.** `FixedStepPump.Advance` hands the last step due to the
  simulation with a deadline already passed
  (`IFixedStepSimulation.TryStep(in FixedStepContext, in CommandSnapshot, WaitDeadline)`).
  The step thread executes the tick while the pump goes on to produce its
  frame and to its pacing wait. An earlier step of a burst is waited for, up
  to the pump's wait allowance, since the next step's input needs its
  results; so is a step the pump finds still open when another is due behind
  it, and the next begins only once it is collected. `FixedStepPump.TryStep`,
  the one-tick-per-frame path, waits for its step up to the stall budget.
- **The frame is produced beside the step.** `HostLoop` produces while a step
  is in progress unless it steps one tick per frame. The frame shows the last
  tick the pump collected: what the step writes reaches the client only at a
  collect, and the pump reads no authority outside its window.
- **Three places resume the open step.** The poll after produce, the pacing
  wait's wake (`JobLane.Finished`, through
  `IFixedStepSimulation.WakeOnStepProgress`, served from `HostWaits.Pace` by
  `HostWaits.Serve`, in the headless loop and in the windowed host's timer
  wait), and the next iteration's step section. Each calls
  `FixedStepPump.Resume` or `FixedStepPump.Advance`, which collects what
  finished and hands over what follows in the same host step. Only the step
  section starts a new host step, after the iteration's input capture and
  control phase, so no resume closes input. A step is counted in the
  iteration that collected it, and the collect's time is charged to
  `simulate`. The swap chain's own wait serves no wake, so a windowed host
  the swap chain paces resumes after produce and in its next step section
  only.
- **A tick that will arm a scheduled capture is waited for.** The simulation
  says so before the step begins (`IFixedStepSimulation.NextStepOwesFrame`,
  from `WorldCaptureScheduler.ArmsAt`), the pump waits for that step up to its
  allowance, and the step then ends the burst as before
  (`IFixedStepSimulation.AwaitsFrame`), so the first frame after the tick
  serves the capture.
- **The run ends on a boundary.** A step still open when the loop ends is
  waited for and completed (`FixedStepPump.Settle`) before the host tears
  down, so every step that began ran its whole order of events.
- **The order of events is the single-threaded loop's.** A host step is still
  a sequence of single ticks, the boot world's and then each instance's in
  name order, and the pump prepares each one only after it has collected the
  one before, with that tick's hooks, portal scan and screen-session settle
  in between. Resuming changes when the pump does that work, never its
  order, so no state hash moves (`TickBesideLoopLawTests` steps one input
  through a waiting host and through three collect schedules of a host
  beside its tick, and compares the state hash). The wave of
  [waves](#waves), which executes every instance's tick with nothing from the
  pump between them, is not built: it reorders five things, and the serial
  hand-off costs one visit of the pump for each tick. A host whose frames the
  swap chain paces gets two visits a frame, so with several worlds stepping
  the pump waits in its step section for the ticks the frame did not cover.
  That is when the wave replaces it.
- **The pump reads no authority outside its window.** Every guarded member
  of `WorldServer` refuses the pump there, and refuses the thread that handed
  a tick over until it has collected it
  ([the boundary window](#from-the-client-to-the-authority-the-boundary-window-and-the-link)).
  The host's published values serve the loop's own readers
  (`WorldHostStep.RatePerSecond`, `WorldHostStep.StepInterrupted`).

### A slice that outlasts the stall budget

A slice still executing when the pump's wait passes its bound stays in
flight. `WorldServerStepShell.TryStep` answers `WorldStepThread.ExecuteWait`
as the pending dependency, the host narrates it once, and the next call
collects the same slice before anything else is prepared. The loop keeps
pacing, runs no console line (the step is pending and not interrupted) and
reads the host's published values, never the authority. Before this change a
tick that computed without end held the pump with it.

### State bound to the thread

A tick reaches state that belongs to the thread running it. Each is listed
with what happens to it now that the thread is the step thread.

| State | Ruling |
|---|---|
| The wait door's pump registration (`HostWaits.BeginPumpIteration`) | The step thread is a consumer, never the pump, so nothing clips or charges the tick's waits; their slice deadline is the bound |
| The frame work binding (`HostFrameWork.Bind`) | The step thread binds its own measurement, and the pump folds it into its iteration at collect (`HostFrameWork.Fold`) |
| The timeline's tick lane (`HostFrameTimeline.NoteTick`) | Fed from the time the step thread spent executing (`HostFrameWork.NoteExecuted`), summed by `FixedStepPump` over every slice of a step; a simulation that runs its tick on the pump reports the pump's time |
| The count of authority gates a thread holds, in `WorldServer.EnterAtTickBoundary` | Per thread by design. The hand-off refuses a caller that holds a gate, since the tick takes the gate on the step thread |
| `WorldForwardingScope`'s depth | Opens and closes inside one tick |
| Scratch a thread reuses (`RuleExpressions`, `FixedStaticContactField`, `SdfFieldEvaluator`) | The step thread grows its own copy; the pump's stays for console work |
| `ShardedWorkCounterSet`'s shards | Summed over threads on read |
| A tick fork's owner (`JobPool.Fork`) | The step thread opens and resumes its tick's fork; the pool refuses a resume from another thread |
| The frame thread's allocated bytes in `host.frames` | No longer include the tick's allocations, which no reading reports yet; the lane that builds the wave adds the step thread's beside the tick lane |

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
  that. The hand-off is a small lane in `Puck.Hosting.Jobs`, `JobLane`, that
  the machine worker could later sit on; that merge is not part of this
  change.
- **`HostWaits`.** The pump's wait for a wave is a door wait with a reason,
  in the form that is not charged to the pump's allowance
  (`HostWaits.OnDelegatedWork`). The step thread idles as a consumer
  (`HostWaits.ClaimConsumer`), and the pacing wait's wake is
  `HostWaits.Serve`.
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

**Stage one: no behaviour moves.** Built: steps 1 to 3.
[The step thread as built](#the-step-thread-as-built) records what stands and
what stages two and three must keep.

**Stage two: the doors.**

4. **The outbox**, at the last hop, with the three callbacks and the save
   effect's split. Built (`OutboxLawTests`). Check: a law delivers twice to one session sink in a tick
   and reads both records intact at the collect; the scenarios that fire cues
   keep their hashes.
5. **A delivered document bound to its sink's values.** Built
   ([what a delivered document may reach](#what-a-delivered-document-may-reach)).
   Check: `DeliveredStateConformanceLawTests`, over a fixture and over the
   debug world; `puck references WorldStateReader.Bind` finds only
   `WorldDocument.AdoptDefinition`.
6. **Adjacency in prepare**, with the tick's immutable record. Built
   ([the prepared tick](#the-prepared-tick)): `WorldTick.PrepareAdvance` runs
   on the pump and hands the step thread its projections. Check: a law steps a
   world whose neighbour restarts and finds no call into `WorldInstanceHost`
   from inside the tick; the crossing scenarios pass `puck test --reproduce`.
7. **The link.** Built (`WorldLinkBoundary`). Queued submissions mint when the window opens; `Query` and
   `SubmitSession` refuse outside it; the roster reads published profiles.
   Check: a law joins a controller from a window event while a tick is held
   and seats it in its preferred slot.
8. **Presentation out of the composition root**, into a project that cannot
   reference the server or the machines, with the published tick record, the
   machine presentation table and the diagnostic copy. Built: the project,
   the feed and its collect, and the readers
   [the table](#frame-thread-readers-and-where-each-moves) marks. Check: the
   project builds under its architecture profile, which is the proof that no
   frame type in it holds the authority.

   8b. **The binder, the render root and the audio director** read the feed
   or the client from inside the presentation project. Built. Check: the
   project builds under its profile with them in it; step 9's window then
   throws on a read that reaches the authority through a wired closure.
9. **The window and the seam check**, on every member the tick mutates.
   Built ([the boundary window](#from-the-client-to-the-authority-the-boundary-window-and-the-link)).
   The window replaced three stand-ins: the outbox's test for the collecting
   thread, the refusal of the thread that handed a tick over, which was a
   mark on the tick, and `WorldHostStep`'s rate and stall flag, which now
   publish as the window closes. Check: `TickBesideLoopLawTests` reads
   `WorldServer.Population` and five more members from a registered pump
   outside the window, which throws, inside it, which reads, and from another
   thread, which the gate orders; the windowed debug world walks its gallery,
   a portal crossing and a machine screen on both backends with no throw.

**Stage three: the wait ends.** Built for every wall-clock host
([the loop beside the tick](#the-loop-beside-the-tick)).

10. **The loop stops waiting for the last due tick.** Built: `HostLoop`
    produces while a step is in progress outside lockstep, resumes an open
    host step after produce, waits on a tick a scheduled capture arms, and
    counts a step in the iteration that collected it; `FixedStepPump` takes
    the next due step after a resumed one in the step section; the seat's
    look step is held while its tick is in flight; a replayed snapshot
    arrives stamped with its tick's completion time. The held tick's owner
    and the uncharged wait hold as
    [built](#two-things-the-later-stages-keep). Check: `host.frames` shows
    iterations that produced beside a step and the tick lane's cost apart
    from `simulate`; `TickBesideLoopLawTests` reaches the waiting host's
    state hash under three collect schedules; `puck test --reproduce` passes
    at one and four workers.

    Not built, and owned by the lane that builds the wave: the two waves in
    place of the serial instance loop, with the unit handed to the step
    thread becoming the wave and the slice protocol going with the pump's
    wait (the scenarios the second wave reorders are re-recorded then); and
    `host.frames` reporting the step thread's allocated bytes beside the tick
    lane. The path on which the pump waits for every tick stays for the hosts
    that step one tick per frame, which show exactly the tick they stepped.
11. **The pacing wait's wake.** Built: a finished tick wakes `HostWaits.Pace`,
    which resumes the open host step, in the headless loop and in the
    windowed host's timer wait. The swap chain's own wait is not serviced
    until [C0.6](engine-job-system.md#the-steps) puts it through the door, so
    a host the swap chain paces resumes after produce and in its next step
    section. Check: with a tick longer than produce, a host step's ticks
    complete within one iteration, read from `host.frames`' steps per
    iteration.
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
  hand-off to the step thread's first instruction: `host.frames`' `simulate`
  line less its tick lane, on iterations that stepped, less what prepare and
  collect cost before. It is expected to be tens of microseconds a tick, by
  reasoning about a monitor's wake; no reading has been taken.
- **A windowed boot**, with the tick on the step thread. A tick's
  deliveries reach `WorldClient` at the pump's collect while the pump waits.
  The debug world boots windowed on each backend, the gallery's screens and
  a portal crossing draw, and `host.frames` shows the tick lane beside
  `simulate`. Expected: no difference from the build before.

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
