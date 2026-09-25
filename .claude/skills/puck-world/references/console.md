# The console — verbs, routing, and the stdin contract

The console is the control plane: process stdin drives verbs, results echo
on stdout, refusals and server narration on stderr, all mirrored onto the
in-game panel (`Puck.Hosting`'s `ConsoleTape` — a MIRROR only; nothing that
draws can take the control plane away). Every capability is a verb; `help` is
generated from the registered commands. Infrastructure lives in
`src/Puck.Commands/` (`CommandRegistry.cs`, `CommandDefinition.cs`,
`CommandRouting.cs`, `TextCommandSource.cs`, `WireArgs.cs`); the modules
live in `src/Puck.World/*CommandModule.cs` and, for the server-only doors,
`src/Puck.World.Console/*CommandModule.cs` (see
the project table in the main `SKILL.md`).

## Contents

- Command modules
- Routing — the determinism class
- Output contract
- The stdin drain barrier and `world.wait`
- The mirror
- Screenshots
- `.puck`-booted worlds: `world.reload` recompiles, `world.save` refuses the source
- The document has ONE door — do not add a per-section verb
- Grammar conventions for new verbs

## Command modules

A module implements `Puck.Commands.ICommandModule` — one
`GetCommands() → IEnumerable<CommandDefinition>`. Convention: state as
constructor parameters (never `IServiceProvider`), verb logic inline; when a
module hits the analyzer complexity ceiling, carve by SUBJECT into more
modules, never into shell+static-logic. Registration is `services.AddSingleton<ICommandModule,
X>()` in `WorldBootComposition`; `CommandRegistry` aggregates all modules and
observers at construction and throws on any duplicate name/alias (including
its built-ins `help`, `wire.ack`, `wire.errors`).

Two definition factories, plus two `Puck.World` wrappers over them. A sweep that
stops at the two factories MISSES most registration sites — the wrappers carry
~69 of them.

- `CommandDefinition.Verb(...)` — bare no-arg verb (the bound-input shape).
  Takes `valueKind`, so a bound row may carry a constant (F1..F4 hand
  `player.claim` its slot as an `Axis1D` value).
- `CommandDefinition.WithWireArgs(name, description, handler, bindability,
  map, routing, ackOnly, valueKind)` — THE argument-bearing shape; handler is
  `Func<CommandContext, WireArgs, CommandResult>`. `WireArgs` is a zero-copy
  `ref struct` (`Count`, indexer, `Is`, `Tail`, `TryInt`, `TryFloat`,
  `Echo`). `valueKind` defaults to `Digital`; a BINDABLE arg-taking verb whose
  rows carry a constant must declare the kind those rows dispatch
  (`Axis1D`), or `BindingVocabularyCheck` sees a mismatch — see the
  recompose trap below.
- `WorldCommandDefinition.Simulation(name, description, handler)` — an
  unbindable Simulation-routed wire verb.
- `WorldCommandDefinition.Row<T>(name, description, info, toMutation, link)` —
  a whole-row document upsert: inline-JSON parse plus submission. The general
  `world.row.set`/`world.row.remove` door generalizes exactly this shape.

`Bindability` is required (`Unspecified` throws at construction). The
description IS the help text — `help` prints `name - description` for every
registered command, which is why descriptions here are long. Both factories take
an `audience`: `CommandAudience.Operator` makes the registry refuse the verb for
every principal but the console, before the handler runs, on every dispatch path
(`CommandAudienceLawTests`). The evaluation diagnostics — `world.rule.trace`,
`world.rule.failures`, `world.search`, `world.decisions`, `world.responses`,
`world.rules`, `world.verdicts` — are operator verbs, because each prints values
the rules computed, which may derive from state a seat is not shown.
`world.affordances` publishes each verb's `audience`, and
`ScheduledStepVocabularyLawTests` pins the live host's operator set to exactly
that list, so a diagnostic added in any module (the `Puck.World`-resident ones
included) must declare its audience or fail the law.

## Routing — the determinism class

`CommandRouting` has exactly two members:

- `Immediate` — runs inline when submitted; never enters the simulation
  (read-backs, graphics toggles, console editing).
- `Simulation` — injected into the per-tick `CommandSnapshot`, tick-aligned,
  applied like any other deterministic input; the handler actually runs when
  the snapshot applies, not at the text-submit call. This routing class alone
  says nothing about ordered-domain timing or replay capture. A handler can
  then perform a synchronous ordered submission, enqueue a tick-boundary
  operation, or call a side path that is refused while recording.

A fast text path serves `WithWireArgs`+`Immediate` lines with no quotes/`@`
(zero-copy tokenization, principal stamped `Principal.Console`);
everything else takes the full parse. Simulation lines are excluded from
the fast path by construction.

## Output contract

Handlers return data (`CommandResult`), never write streams. The host sink
in `Program.cs` splits: `IsError` → stderr, otherwise → stdout; then the
mirror records the line. Engine narration (`[world.addon: …]`,
`[unified-overlay] …`, `[world.mutation: …]`, boot origin lines) goes
straight to stderr, and so does every host log line (`Program.cs` sets the
console logger's `LogToStandardErrorThreshold` to `Trace`, as the silo does),
so stdout carries only answers a script parses. **Capture both streams or you read half the
conversation.** Echo format is one bracketed assertable line:
`[verb: field=x field=y]`; refusals share the shape and set `IsError`. The
pervasive convention: a no-arg invocation of a lever verb echoes the
current value.

Every result and every narration reaches its stream as one record
(`Puck.Commands.ConsoleRecord`, written by `BufferedConsoleOutput` and
`WorldConsoleNarrationSink`): its first line starts at column zero and each
further line of the same record is indented two spaces. `world.state`'s
dump, `world.symmetry <a> <b>`, `world.rule.trace`'s read-back, `help`, and
the lines of `world.counters` and `pipeline.inspect` are one record each,
so an unindented line always opens a new answer and two identical one-line
answers stay two. A reader matching a continuation line exactly includes the
indent.

Three echo models — do not conflate them:

1. **Session/query verbs** format their result lines from the completion
   payload the callback receives — never from a live read after the call.
2. **Lever verbs** (`world.shadows`, `world.ao`, …) submit a fire-and-forget
   `WorldSessionLever` (no completion) and echo a live read of the settings
   service — valid only because loopback delivers synchronously.
3. **Mutation verbs** return `CommandResult.None` with NO synchronous echo;
   the accept/reject narration arrives at the tick boundary through
   `WorldServer.EchoTap` (stderr + toast + mirror), and a rejection of a
   local console connection's line increments `wire.errors` via
   `NoteDeferredRejection` (`WorldDeferredVerbAnswers`); a remote peer's
   refusal and an unregistered rebuild (the silo's reload) do not. A mutation the World
   makes itself (principal `world`: a deal or response sweep's, a rule's
   document-row effect) was submitted by no session and raises no echo — it
   narrates on stderr alone. A verb that submits
   through the registering `Submit(link, mutation, echoes, verb)` overload
   (`WorldDeferredVerbEchoes`; the `world.row.set`/`world.row.remove`/`world.assign`/
   `world.state.transform`/`world.state.act` family) also gets a per-verb
   `[<verb>: …]` line the tick-boundary drain settles — stderr on rejection
   (beside the verb-agnostic `[world.mutation rejected: …]`), stdout on
   acceptance — so a canary can account either outcome against the
   submitting verb rather than only a refusal. A verdict already known when
   the handler returns (an ingress or codec refusal, a rebuild or undo whose
   correlation cannot register) is the line's own result, because
   `CommandResult.Settling` returns a settled settlement's verdict: it answers
   on the line and counts in `wire.errors` like any refused line, and never
   also reaches the per-verb echo. An entry pushed out of the full table
   (`WorldDeferredVerbEchoes.Capacity` pending) settles its session as an
   unknown outcome and prints and counts nothing; the table remembers its last
   `EvictedMemory` evicted lines, so the authority's later verdict still prints
   the per-verb line and counts by its real outcome. A line forgotten past that
   memory answers `[<verb>: unanswered — …]` on stderr and counts once, and its
   verdict, arriving later, answers nothing.
   `WorldDeferredVerbAnswers` (`Puck.World.Console`) is the one host wiring
   for all of this: `WorldPostBuildWiring` and the silo both attach it, and the
   World echo tap passes each echo to its `Answer`. A verb that submits through
   the plain `Submit(link, mutation)` overload (`world.generate`,
   `world.state.cell.set`, `world.state.cell.remove`) registers no verb of its
   own: its console link registers the line, so a refusal counts, but its only
   observable answer is the universal `[world.mutation: … applied]`/
   `[world.mutation rejected: … — …]` narration, always stderr regardless of
   outcome — `puck canary`'s runner accounts these by correlating that
   narration to the verb's own `Describe()` prefix (`WorldServer.Describe.cs`
   — unique per `WorldMutation` case, e.g. `Generate '`), never by a
   `[<verb>: …]` line the console layer never prints. Adding the echoes
   registration to a currently-unregistered verb is the more direct fix; the
   narration-correlation reading is the fallback for when that command
   module is out of reach. The two readings are not layered — registering a
   verb that stays listed as narrated in the runner (`CanaryCommand`'s
   `NarratedMutationVerbs`) still reads the narrated way, so a verb moved to
   registration must also move out of that table. The narrated reading counts
   a mutation KIND, not a caller: anything else in the world that composes the
   same `WorldMutation` case — a rule firing's own export, an addon guest —
   narrates under the same `Describe()` prefix and is counted with the
   script's calls. That direction is a count mismatch (a red the runner
   reports as `accounted <verb>: N response(s) for M authored occurrence(s)`),
   never a silent green, so a script for a world whose rules write the same
   kind must either author the extra occurrences or use a registered verb.

`world.gravity` is the gravity decision's Immediate read-back: it echoes the
authored solver, uniform acceleration, shared constant/softening, explicit-mass
sources, point/planet presets with their deterministically derived masses, and
bounded local areas in their compiled priority/authored-order fold (bound,
directional/radial effect, Combine/Replace, and static/attached ride), plus the
last solver and area structural work counters. `world.budget` repeats the static
source count, declared areas per target, last exact/approximate evaluations, and
last area checks/matches so gravity authoring does not create a silent per-tick
price. `world.navigation` and `world.budget` are authoritative-core verbs: both
remain registered headless, with budget naming the absent renderer while still
reporting simulation costs. `world.flock` reads each kit's local-perception
profile, movement-domain binding, optional cohesion/alignment expressions, and
last-step candidate/neighbor/sight, movement-check/refusal, and affinity
evaluation/failure counters; `world.budget` repeats the scratch capacities and
charged affinity work, included in the shared rule-work ceiling. Navigation also reports shared destination slots, actual/budgeted
expansions, extracted paths, and capacity refusals. A bounded sample is not globally nearest
neighbors, and a headless work count is not an FPS measurement. None of these
verbs mutates the field.

Body command targets use the world's authored local-seat prefix, not the host's
four-seat ceiling. A zero-seat world can address peer body 0 through the same
designation and control verbs as any other active peer.
`body.designate` returns the target read-back under its own verb prefix;
`body.targets` keeps its query prefix, so command accounting cannot conflate them.

`world.transfer <source-instance> body:<index> <destination>` addresses any
zero-based body index, including creature and network-peer slots. Bare numbers
are one-based local seats (1..4), and `party` selects the active local-seat cohort.
Parsing enforces the global body ceiling; the transfer drain checks the named
source's actual capacity, occupancy, and the stamped caller's Drive grant. Use
explicit body targets when verifying onward transfers of remote travelers.

`wire.ack [on|quiet]`: quiet drops SUCCESSFUL echoes of verbs registered
`ackOnly: true` (flood-friendly); errors and answer-bearing verbs always
echo. Corollary contract: a `WithWireArgs` handler MUST set `IsError: true`
on every failure — that is what makes quiet safe. `wire.errors [reset]`
reports `[wire.errors: N rejected]`.

## The stdin drain barrier and `world.wait`

Silo row retirement disposes its `TextCommandSession`, refusing work still
queued behind commands or waits. The stdin router uses `SiloConsoleRouting.TryEnqueue`
so a concurrent retirement is a row refusal, not an exception that kills the
reader thread. Already injected simulation work keeps its completion semantics.
Clock regression invalidates every old wait deadline, even one that expired
before the next command-pump drain observed it.

The barrier lives in `TextCommandSource.Collect` (`Puck.Commands`) and is
PER-SESSION, not registry-wide: while a session's own Simulation submission is
pending (`TextCommandSession.HasPendingSimulationSubmission`, over the
`TextSubmissionBarrier` that rides that submission's snapshot entry), a
following line from THAT session that does NOT route to Simulation is held — so
a scripted write-then-read pair (`world.row.set kits …` then `world.status`)
needs no polling, and another seat's ready text keeps draining meanwhile.
Further Simulation lines keep draining FIFO into the same pending snapshot.
Blank lines and `#` comments
are skipped, so piped scripts can be self-documenting. **The barrier holds
`Immediate` lines and queued host operations** — it fences reads behind writes; it does not delay
Simulation traffic. It releases whether or not the submission's handler
dispatched or threw, so a throwing verb cannot strand a session's stdin.

`world.wait <ticks>` (`WorldWaitCommandModule` + `WorldConsoleWaitGate`) is
the explicit wait: Immediate, 1..144000 ticks, clocked by completed host-work
ticks through `WorldConsoleWaitGate.PublishTick`, independent of replay
rewinds and wall time. It holds only `CommandContext.TextSession`; each
session has its own deadline even when sessions share the same row clock.
Do not wire this gate to the source-wide `ITextCommandHoldGate`.
Direct registry submissions without a text session are refused. Echo:
`[world.wait: N ticks from T — releasing at tick R]`. Being Immediate, the
barrier holds `world.wait` itself until a preceding mutation lands, so its
countdown starts from a tick that already contains it. Use it for
read-after-write across ticks (e.g. asserting motion after input).

**The release is exact.** `FixedStepPump` runs the host's console drain
(`TextCommandSource.Collect`, then the buffered-output flush) before EVERY
step, including each step of a catch-up burst (`FixedStepPump.CreateHosted`
wires it for the windowed, headless, and offscreen hosts alike). The line after
a wait releasing at R therefore runs after tick R completes and before tick
R+1 steps: an `Immediate` read observes the state at R, and a `Simulation`
line applies in tick R+1, because the administrative stdin session is
`DueNextTick` (its Simulation lines are due in the next step that snapshots
input, not at the wall-clock capture stamp). The same schedule of waits gives
the same ticks on a loaded machine; the sim is still never console-paced.
`WorldWaitTickExactnessLawTests` pins this against the real verb and pump.

First step (`FixedStepPump.CreateHosted`): the pump takes no step until
either `StandardInputBacklog` is released or
`TextCommandSource.AdministrativeSessionAwaitsStep` is true after a drain.
`StandardInputReaderService` claims the backlog at construction and releases it
at EOF, on a read failure, for an interactive terminal, or when its first read
would wait on a pipe that has never delivered a byte (an idle IDE or supervisor
pipe, a companion authority's empty stdin). An empty pipe after the first byte
releases nothing: a writer pausing between chunks looks identical to one that
has finished. The session awaits a step when its `world.wait` hold stands or its
next queued line is held behind a pending Simulation submission; since the
session is FIFO, every line ahead of that point has run. So a piped script's
lines up to its first wait (or first read-after-write) run before tick 1 however
the writer chunks the pipe. The backlog release is sampled before the drain, so
a release that lands mid-drain waits one frame instead of stepping without the
lines queued with it (`FixedStepPumpStepGateTests`). Once one step has run the
gate latches open. Whole steps due while it is shut are discarded, not owed. A
script with no wait and no read-after-write, piped through a pipe that stays
open, never steps until the pipe closes; a writer whose first byte arrives after
the World's first stdin read is treated as silent. A line that arrives after
stepping has begun lands in whatever tick drains it, so put a `world.wait` in
front of anything whose tick matters.

`quit` (`TerminalCommandModule`; `SiloStdinRouter` routes it as
a process verb) is Immediate, so the barrier and `world.wait` hold it behind
everything its session queued earlier. Once it runs, `mayStep` refuses further
steps and the host loop exits on `TerminalControl.TryConsumeExit`. Echo:
`[quit: exiting]`. End a script with `quit` to make the run last exactly as
long as the script; `--exit-after-seconds` remains the wall-clock stop.

## The tape

Each local seat owns a `ConsoleTape` (`Puck.Hosting`; `ICommandObserver`,
64-line ring), `ConsoleLineEditor`, history, and principal-bound
`TextCommandSession`. `ConsoleInputSink` (`Puck.Launcher`) routes a text
device to its roster seat and feeds only that seat's editor while its session
is open. Backtick belongs to the terminal's always-active binding plane, not
to any world page, so a page or chord override cannot remove the way out.
`console [on|off]` is a terminal verb, like `quit`; a seated activation targets
its invoking slot. Administrative stdin remains the separate
`Principal.Console` ingress and must name its target explicitly:
`console [on|off] <player>` (or `console <player>` to toggle). Administrative
exchanges and deferred edit verdicts live on their own operator tape and mirror
onto the currently displayed seat-one tape until presentation grows a separate
operator panel.

`world.binding-bar [on|off|auto] [player]` is the binding bar's parallel live
control and read-back. `on`/`off` force a side, `auto` returns to the authored
enabled/rest policy, and every form reports the resolved per-seat policy — its
`text on|off` switch, authored `slots`/`banks` counts, the resolved (world-or-
player) `hideUnbound`/`stacked` preferences, current hidden state and reason,
and layout values (`scale` reflects a player's own override when set) —
included. Visibility is the only side the verb overrides; every other field is
authored only (`bindingBar.*`, see [documents.md](documents.md)).

## Screenshots

`world.screenshot <path.png>` (`WorldUiCommandModule`): Immediate; requests
capture on the render host's OUTERMOST decorator, so it lands on the next
COMPOSED frame — world plus overlay, what the player actually sees (the
overlay node reads back its own render target; if the overlay drew nothing
the request forwards to the inner producer, which serves it a frame LATER).
Creates the parent directory; errors loudly when the render host has not
produced a first frame yet. The cheap pixel assertion for scripted
verification.

**It arms work; it does not do it — and the echo says so.** Three lines carry
the whole truth, and a script reading only one of them reads a half-answer:

- stdout, at arming: `[world.screenshot: pending <path> — lands on the next
  composed frame]`. No file is promised yet. Let rendering progress with
  `world.wait`, then confirm the completion before reading it.
- stderr, when the frame lands: `[capture] unified overlay -> <path>` (the
  overlay decorator served it) or `[debug] captured frame N -> <path>` (the
  engine node beneath it did). THIS is the line that says a file exists.
- stderr, at shutdown: `[world.screenshot] WARNING: a capture of <path> was
  still pending when the run ended … NO FILE WAS WRITTEN`
  (`WorldPostBuildWiring`'s `ApplicationStopped` drain).

Arming a second capture while one is still pending is REFUSED by name
(`SdfWorldRender.PendingCapturePath`) and counts in `wire.errors`: the render
chain admits one pending request at a time. A mutation barrier orders command
application, but does not itself prove the capture has completed; use the
capture outcome before reusing a path or claiming its bytes exist.

Host integrations arm captures through `TextCommandSession.InvokeAsync` when
they must follow the session's queued edits and waits. Await the returned
`FrameCaptureRequest.Completion` off the pump. Success means the writer closed
the PNG; failure carries an exception, including disposal before service.
`PendingCapturePath == null` is never proof of a successful capture, and a
tick wait alone does not prove a particular request finished. Cancelling an
await does not cancel the accepted capture or release its output path for reuse.

### `world.sdf.dump` — the packed program

`world.sdf.dump <path>` copies the initialized renderer's live packed program
to little-endian uint32 words, replacing the destination. It excludes capacity
headroom, dynamic transforms and the frame grid; use the current `SdfProgram`
layout to inspect it. It is a CPU-side diagnostic copy, not a GPU readback or
loadable asset, and leaves simulation and rendering unchanged.

## `.puck`-booted worlds: `world.reload` recompiles, `world.save` refuses the source

A world booted from `.puck` source (`--world <x>.puck`, compiled by
`src/Puck.World/PuckWorldLoader.cs`) reloads from that source: `world.reload`
re-reads the current origin and recompiles it, CAS-pinning the document it
lowers to, so an edit that lowers identically keeps the pin. `world.load` and
`world.reload` read through the boot's own door,
`WorldDefinitionLoader.TryLoadFileForAdmission`, with the host's machine
catalog and the running instance's identity, so the document's boot draws
refill exactly as the boot drew them before it is admitted and embedded. `world.save` with
no argument, or to any `.puck` target, is refused by name because canonical
JSON would overwrite the source; name a JSON path instead. The artist loop for
a `.puck`-booted world is: edit the `.puck` file, then `world.reload`.

## The document has ONE door — do not add a per-section verb

`world.row.set <path> <json>` and `world.row.remove <path> <key>`
(`WorldRowCommandModule`) are the whole document-mutation surface. `<path>` is a
dotted document member path in the document's own camelCase JSON names — `kits`,
`placements`, `hud.panels`, `views.layouts`, `views.seatRig`. An unknown path
refuses by name and enumerates its siblings.

**Adding a section means adding a ROW to `BuildSections`, never a verb pair.**
That table carries the only three facts the document model cannot supply: whether
the section is a keyed list, which member is its key, and its
upsert/remove `WorldMutation` pair. These two generic verbs replace separate per-section
verbs; re-growing individual per-section verbs is the regression this design
exists to prevent. `puck schema` documents payload shapes — cite it, but there is
deliberately NO runtime schema validation (owner deferred the gate; validation
stays at the full-document revalidation on apply).

Same rule for per-field convenience: a BESPOKE per-section verb that reads a row,
changes one field, and submits the whole row back is a stale read against the
same batch's own composing writes — a defect class, not a shortcut. The general
literal field/list doors below do exactly this shape, safely, because they share
ONE window guard (`WorldRowStepWindowGuard`) that refuses a second read-modify-
whole-row-write against the same row inside one tick window rather than letting
the later one silently revert the earlier; a bespoke verb reinventing the shape
without that guard is the regression this rule still targets.

**`creation.sculpt(s)` is not a second door.** `creation.sculpts` lists the
registered code-authored generators (`Puck.World.Authoring.Sculpting.CreationSculptRegistry`
— see that project's README); `creation.sculpt <name>` runs the named one's
patch against a working copy of the live document, echoes that plan
(`[creation.sculpt: planned=<name> <path>=<verdict> …]`), and composes each
distinct row it touched through the row door's own section table
(`WorldRowCommandModule.TryComposeEditedRow`/`TryComposeRemove`, one
`WorldMutation` per row, stamped with `context.Principal` — a seat's
sculpt lands as that seat and is refused where that seat lacks `Mutate` over
the section), claims each row in the shared `WorldRowStepWindowGuard`, and
submits over the link like any buffered mutation — the same whole-document
revalidation, tick-boundary apply, recorded tape entry, and deferred
`[creation.sculpt: …]` verdict echo. It never re-dispatches text lines: a
nested `CommandRegistry.Submit` would stamp the shared injection sink's Console
identity over the issuer. All-or-nothing: a row that fails to compose, a row
already claimed this tick window (`row '<identity>' already has an edit
buffered this tick — fence with world.wait`), or a patch fault refuses by
name and submits nothing. No `--write`: writing to disk stays `world.save`.
Laws: `WorldSculptCommandModuleLawTests`.

### Field and list-element doors — one level inside a row

Four more verbs, all in `WorldRowCommandModule`, address ONE FIELD or ONE LIST
ELEMENT inside a row rather than the whole thing — the same section table
`world.row.set`/`.remove` resolve against, one level deeper, through a shared
path grammar (`WorldRowFieldPath`, `internal` — no `InternalsVisibleTo`
needed, since every consumer lives in this same project):

- **Path grammar**: dot-separated segments, each an optional trailing bracketed
  selector — a zero-based list INDEX (`shapes[3]`) or a name/id-addressed
  SELECTOR (`shapes[name=forearmL]`, `shapes[id=42]`) naming any member the
  list's element type carries, not only `name`/`id`. A selector matching no
  element or more than one is refused BY NAME, listing every element's own
  candidate value for that field.
- **`world.row.set <path> <key> <fieldPath> <json>`** (keyed) / `world.row.set
  <path> <fieldPath> <json>` (keyless) — the LITERAL sibling of the whole-row
  form, sharing its verb name: the SECOND token's own shape discriminates them
  (a bare key/field path never starts with `{`/`[`, the only way a whole-row
  payload — always a record — can start). Reads the row, replaces one field's
  value in place (a NAME segment creates an absent optional member; an
  INDEX/SELECTOR segment must already exist), and resubmits the whole row
  through the SAME `Upsert` the whole-row form uses — so the spliced value
  crosses the row's own `JsonTypeInfo` exactly once, at reparse, which is where
  a bindable field's declared shape (`[x,y,z]` or a `"state.row.key"` string for
  a `DocumentVector3`; JSON `null` clears a nullable field) is actually
  validated. A `"state.<row>[.<key>]"` string written into a bindable field
  keeps the binding: the compose boundary resolves a submitted row's references
  against the current definition's state (`WorldServer.TryCompose` rehydrates
  the candidate when the mutation carries one; the `creations` arm resolves a
  private copy of the row before canonicalizing it), so the installed row
  carries the reference and re-resolves on every later write to that state
  row. A reference naming no declared cell is refused at apply by name
  (`must name a declared state cell`), and the row is unchanged.
- **`world.row.add <path> <key> <listPath> <json> [after=<selector>]`** (keyed)
  / `world.row.add <path> <listPath> <json> [after=<selector>]` (keyless) —
  inserts one element into a list field. `<listPath>` is the dotted/bracketed
  path TO the list itself (`document.shapes`, `document.shapes[name=
  forearmL].swings`); omitting `after=` appends, `after=<n>` inserts after that
  index, `after=<field>=<value>` inserts after the selected element.
- **`world.row.remove <path> <key> <listPath> <selector>`** (keyed) /
  `world.row.remove <path> <listPath> <selector>` (keyless) — the list-element
  sibling of the whole-row `world.row.remove`, discriminated by ARGUMENT COUNT
  (3 or 4, never 2) rather than payload shape, since neither form here carries
  JSON. `<selector>` is a bare index or `field=value`.
- **`world.row <path> <key> [<fieldPath>]`** (keyed) / `world.row <path>
  [<fieldPath>]` (keyless) — Immediate read-back of a row or one field as
  canonical JSON; a `<fieldPath>` ending at a bare list field LISTS it instead
  — one `[world.row <index>: <name-or-id> <compact-json>]` line per element,
  headed by a `[world.row: … N element(s)]` line. The whole-row echo omits the
  section's `DropOnEdit` members (a creation's `hash`), so it is exactly what
  the whole-row `world.row.set` accepts back with a field changed; read the
  digest itself by field path (`world.row creations moth hash`).
- **`world.row.step`**'s own `<path>` (`<section>.<key>.<field>` or
  `<section>.<field>`) resolves the identical `[n]`/`[field=value]` grammar for
  its numeric/boolean/enum delta (`WorldRowFieldStepper`, over
  `WorldRowFieldPath`).

Every one of these four (plus `world.row.step`) claims its addressed row in the
shared `WorldRowStepWindowGuard` for the current tick window before submitting
— a second edit to the SAME row (by any of the five) inside one window is
refused by name, naming the row, rather than silently reverting the earlier one.
A section whose row carries its own derived self-digest alongside its content
(`creations`' `WorldPrototype.HashRaw`, recomputed from the SAME embedded
document a field/list edit just changed) is marked `DropOnEdit: ["hash"]` in
`BuildSections` — stripped before resubmission so the reparsed row reads as
self-consistent (an absent hash) rather than resubmitting a digest that no
longer matches its own content.

## Grammar conventions for new verbs

- `family.verb` dotted names (`world.*`, `player.*`, `screen.*`,
  `profile.*`, `storage.*`, `capture.*`, `replay.*`,
  `audio.*`); names case-insensitive on the full parse, ordinal
  on the fast path.
- Row-valued mutation verbs take ONE inline-JSON argument in the exact wire
  shape of the document section row, reconstructed from the raw text
  (quotes survive) and parsed via `WorldJsonPayload.TryParse` — a parse
  error echoes inline and submits nothing.
- **A stepped twin is not a verb.** `.next`/`.prev`/`.up`/`.down` fold onto the
  verb they step: keep it `Bindable`, declare `valueKind: Axis1D`, give the bound
  rows an `Axis(±1f)` constant, and read `context.Value` when
  `context.Origin == CommandOrigin.Binding`. Do NOT use `context.Source` as the
  discriminator: presentation-driven and synthesized bindings have no physical
  source. Do NOT discriminate on `context.Value.Kind` either — that is only
  coincidentally reliable while everything declares `Digital`.
- **The recompose trap.** A binding whose dispatched value kind disagrees with its
  command's declared `ValueKind` is only NARRATED by the boot sweep, but
  `WorldSeatBindings.RecomposeSeat` REJECTS the whole seat document and keeps the
  prior mapping — so every later `player.bind`, profile load or context regroup is
  silently discarded. Boot narration is NOT proof a binding change is safe: force a
  recompose (`player.bind 1 keyboard.p player.wheel.ring value:1`) and assert stderr carries no
  `recompose rejected` line.
- `player.bind` can carry a constant for a command destination with `value:<v>`
  (validated against the destination's declared kind; mutually exclusive with
  `scale:`). It can only address the default group's resting page or a
  `(group, chord)` row — never a named sub-page, which is a known open gap.
- No-arg → echo current value. Refusal → same bracketed shape + `IsError`.
- Choose routing by determinism class, not convenience: anything that
  touches sim state is `Simulation`; a read-back is `Immediate`. Routing
  describes when the command handler runs. For example,
  `world.row.set addons`/`world.row.remove addons` — the door that mounts,
  unmounts, reloads, enables, and disables an addon — is Simulation-routed
  and buffers a `WorldPendingOp.Mutate` for the tick boundary, exactly like any
  other `world.row.set`/`.remove`. The drain barrier makes a following
  `world.addons` read wait for settled state.
- New decision surface ⇒ read-back verb in the same change.
- A `.puck` world is authored and checked offline (`puck format`/`puck
  lint`/`puck compile`, `puck-dsl`) and booted via `--world <x>.puck`;
  `world.row.set`/`.remove` and the field/list-element doors above remain the
  LIVE runtime mutation surface for an already-booted session — complementary
  doors, not competing ones. What a rule's own effect sugar compiles to is
  [mutations.md](mutations.md)'s to state.

`world.decisions` is an Immediate, no-argument, headless-safe read-back of
world-rule choice policies and their active bindings. It reports the selected
option, selected body incarnation, last raw score, remaining engine-tick timers,
reconsiderations, and local random draws. It echoes neighbor policies and last-pass
image points, grid builds, inspected/scored candidates, sight tests, and limited
queries. `world.rules` routes decision rules to this richer echo;
`world.budget` includes candidate inspections/gates, expanded score programs, and
the greatest effect branch, plus shared pose-image and grid copy/group visits.
It separately echoes the per-tick pose, distinct range-scale rebuild, and sorted
grid-point ceilings without cadence discounts. These structural units do not
claim a CPU-time or sorting-comparison bound.

Discrete state commands: `world.state.transform <transform-json>` and
`world.state.act <phase-row> <sequence> <transform-json>` are Simulation-routed,
stamp the caller, and register deferred refusal echoes. `world.topologies`
and `world.state.observe` are Immediate read-backs. The latter uses a stamped
query and returns observation JSON. `world.observe <principal>` is the third: an
Immediate composition of `WorldStateDisclosure.Compose` for an EXPLICITLY named
principal (`PrincipalTokens.TryParse`'s token grammar, the same
`world.grant`/`world.why` take), so the operator's console can inspect what
seat1 sees and what seat2 sees without submitting as either — the read-back
side of a hidden-hand table (see `games/poker.puck`). Any other principal may
name only itself; naming another is refused by name.

**Disclosure chokepoint.** A seat's own console session reaches every verb, so a
read-back of state values never reads `WorldServer.Definition` for it directly.
`IWorldConsoleAuthority.ReadView` (reached through `TryResolveReadView`) mints a
`WorldStateReadView`: the live document as `WorldStateDisclosure.Disclose` shows it
to the acting principal, and the whole document for the console — its placements
included, each dealt child re-dealt from the rows the principal may read, and each
responsive placement whose `respond` conditions read a row withholding a cell from
the principal handed its `holding` mask without the entries that read a withheld
cell, so it shows the first entry left, or its authored prototype. `world.state`,
`world.row`, `world.tabletop`, `world.hud.template` and `world.placements` read through it; `world.match`
refuses a row the view withholds anything from. A direct read of a withheld cell is
refused by name — `[world.state: 'vault'.'$value' is not disclosed to seat2 — its
visibility withholds it]` — alike for a withheld key and an absent one, so a refusal
never says which keys exist; a listing shows the row's `hidden` policy (nothing, a
count, or one placeholder per cell) and none of a restricted row's draw or ring
bookkeeping. `ConsoleDisclosureLawTests` sweeps every console-module verb as a
non-reader for three sentinel values and carries the mutation proof (an authority
minting the operator's view leaks through several verbs at once). A new read-back
of state values reads through the view; one in `Puck.World` builds it with
`WorldStateReadView.Of(server, context.Principal)`.

**Local presentation is not filtered.** The local HUD, view bindings and
`world.hud`'s echo of them resolve against the live document for the one shared
screen: the author chooses what that screen shows, and a binding to a restricted
row shows it to everyone in the room. The disclosure boundary for a remote peer is
the wire projection (`WorldProjection.Compose` → `WorldStateDisclosure.Disclose`, which
carries the dealt and responsive placements too), and for a local seat's console it is
the read view above; `LineupDealLawTests` holds both for lineup's hidden galleries and
`PlacementResponseDisclosureLawTests` for a gate swapped on a hidden slot. On the
federation lanes a traveler's route answer and its `ObserveTraveler` stream compose for
the traveler's own peer principal (`WorldFederationCodec.EncodeRoute` derives it from the
route's entity), while the plain `Observe` lane authenticates a source namespace, names no
body, and composes for the public observer
(`FederationTransferLawTests.ARemoteTravelerIsHandedItsOwnHiddenStateAndNoneOfAnothers`).
Operators and limits live in the Schema README's discrete-state section rather
than a second command vocabulary here.

`pipeline.*` (`WorldPipelineCommandModule`) is core-registered in rendered and
headless hosts. `pipeline.load <name> <source> [camera]` upserts a
`views.pipelines` row through normal authority and validation. A rendered host
reconciles accepted rows, creates instances, and schedules complete pipeline
compilation in the background. A refused mutation must never create a GPU
instance. `pipeline.reload`, `pipeline.watch`, `pipeline.time`, `pipeline.step`,
`pipeline.reset`, `pipeline.sentinels`, `pipeline.output`, `pipeline.capture` and
`pipeline.status` control presentation or report state; controls that need a renderer refuse
when none exists. `pipeline.set` (a field-by-field merge; `null` restores the
source default), `pipeline.output` and `pipeline.time … scale` are session
previews on `WorldPipelineRuntime.Entry`; a move of the row's revision
discards them. `pipeline.commit <name>` (Simulation-routed) submits
`CommitViewPipeline` built by `Entry.TryPrepareCommit`, which takes only that
entry's preview. `pipeline.overrides <name>` (Immediate, headless too) prints
`<pass>.<field> committed=… pending=…` lines, then the time scale and output,
with the row revision and installed source identity on the first line. `pipeline.wait <name> compiled|installed|captured [seconds]`
`pipeline.wait <name> submitted|counted <frames> [seconds]` and
`pipeline.wait <name> resized <width> <height> [seconds]` hold only the issuing
session (`TextCommandSession.HoldWhile`) until the phase or a presentation-time
deadline; the hold predicate reports exactly one
`[pipeline: <name> wait <phase> reached|failed: …|unsupported: …|timed out …]`
line on stderr, so a script never polls. Use it, not `world.wait`, before a
pipeline capture: `world.wait` counts simulation ticks, not compilation or
submitted frames. `counted` waits for completed per-pass work counts (relative
to the last reset, like `submitted`); `pipeline.inspect` prints them, one
`work <pass> executed: …` line per pass (`GpuWorkReport`), and the first line carries
`work submission=S revision=R` for a canary's `response` extraction, after
`owned=`, `steady=`, `peak=` and `budget=` byte fields. `pipeline.budget
<name> [<bytes>|device]` (Immediate) sets or clears `ShaderPipelineRenderNode.BudgetCapBytes`,
which only lowers the device's budget, and prints `budget= device= cap= owned=
steady= peak=`; a candidate whose peak does not fit fails `installed` or
`resized` with `SHADERPIPE_BUDGET` and the installed graph keeps running.
`gpu.faults arm <kind> [<n>] | disarm | list` (Immediate, `CommandAudience.Operator`,
registered in both GPU presentation shapes by `AddGpuCreationFaults`) arms the
host's `GpuCreationFaults`; an armed creation fails `installed` with
`GPU_CREATION_FAULT`, and every form prints `armed=… <kind>=<seen> …` for a
canary's `response` extraction. A paused
reset's initialization frame never consumes a pending `pipeline.step`; the step
renders one frame beyond it. A reload, a row upsert or a resize is not a step:
a paused (or time-scale-zero) instance installs it without rendering, so
`installed` and `resized` never wait for `pipeline.step`, and the instance
keeps showing its last image until a step, resume or reset.
`WorldPipelineRuntime.PumpWatches` installs completed candidates on the frame
thread and watches source dependencies with a 150 ms debounce. Shader errors
retain the previous complete pipeline. Frame inputs are filled once per
instance, even when several view slots show it. The pointer is in the slot's
pixels with the origin at the bottom-left; clocks and feedback remain
presentation state.
See [the World workflow](../../../../src/Puck.World/README.md#shader-pipelines)
and [the pipeline contract](../../../../docs/reference/shaders.md#shader-pipelines-and-live-development).