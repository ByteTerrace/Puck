# The console — verbs, routing, and the stdin contract

The console is the control plane: process stdin drives verbs, results echo
on stdout, refusals and server narration on stderr, all mirrored onto the
in-game panel (`WorldConsoleMirror` — a MIRROR only; nothing that draws can
take the control plane away). Every capability is a verb; `help` is
generated from the registered commands. Infrastructure lives in
`src/Puck.Commands/` (`CommandRegistry.cs`, `CommandDefinition.cs`,
`CommandRouting.cs`, `TextCommandSource.cs`, `WireArgs.cs`); the modules
live in `src/Puck.World/*CommandModule.cs`.

## Contents

- Command modules
- Routing — the determinism class
- Output contract
- The stdin drain barrier and `world.wait`
- The mirror
- Screenshots
- The document has ONE door — do not add a per-section verb
- Grammar conventions for new verbs

## Command modules

A module implements `Puck.Commands.ICommandModule` — one
`GetCommands() → IEnumerable<CommandDefinition>`. Convention: state as
constructor parameters (never `IServiceProvider`), verb logic inline; when a
module hits the analyzer complexity ceiling, carve by SUBJECT into more
modules (the six `EditorSculpt*CommandModule`s), never into
shell+static-logic. Registration is `services.AddSingleton<ICommandModule,
X>()` in `Program.cs`; `CommandRegistry` aggregates all modules and
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
registered command, which is why descriptions here are long.

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
(zero-copy tokenization, principal stamped `CommandPrincipal.Console`);
everything else takes the full parse. Simulation lines are excluded from
the fast path by construction.

## Output contract

Handlers return data (`CommandResult`), never write streams. The host sink
in `Program.cs` splits: `IsError` → stderr, otherwise → stdout; then the
mirror records the line. Engine narration (`[world.addon: …]`,
`[unified-overlay] …`, `[world.mutation: …]`, boot origin lines) goes
straight to stderr. **Capture both streams or you read half the
conversation.** Echo format is one bracketed assertable line:
`[verb: field=x field=y]`; refusals share the shape and set `IsError`. The
pervasive convention: a no-arg invocation of a lever verb echoes the
current value.

Three echo models — do not conflate them:

1. **Session/query verbs** format their result lines from the completion
   payload the callback receives — never from a live read after the call.
2. **Lever verbs** (`world.shadows`, `world.ao`, …) submit a fire-and-forget
   `WorldSessionLever` (no completion) and echo a live read of the settings
   service — valid only because loopback delivers synchronously.
3. **Mutation verbs** return `CommandResult.None` with NO synchronous echo;
   the accept/reject narration arrives at the tick boundary through
   `WorldServer.EchoTap` (stderr + toast + mirror), and a rejection
   increments `wire.errors` via `NoteDeferredRejection`.

`wire.ack [on|quiet]`: quiet drops SUCCESSFUL echoes of verbs registered
`ackOnly: true` (flood-friendly); errors and answer-bearing verbs always
echo. Corollary contract: a `WithWireArgs` handler MUST set `IsError: true`
on every failure — that is what makes quiet safe. `wire.errors [reset]`
reports `[wire.errors: N rejected]`.

## The stdin drain barrier and `world.wait`

The barrier lives in `TextCommandSource.Collect` (`Puck.Commands`): while a
Simulation submission is pending (`CommandRegistry.
HasPendingSimulationSubmission`), a following line that does NOT route to
Simulation is held — so a scripted write-then-read pair (`world.row.set kits …`
then `world.status`) needs no polling. Further Simulation lines keep
draining FIFO into the same pending snapshot. Blank lines and `#` comments
are skipped, so piped scripts can be self-documenting. **The barrier holds
only `Immediate` lines** — it fences reads behind writes; it does not delay
Simulation traffic.

`world.wait <ticks>` (`WorldWaitCommandModule` + `WorldConsoleWaitGate`) is
the explicit fence: Immediate, 1..144000 ticks (ten minutes at the 240 Hz
fixed step), clocked by COMPLETED SIMULATION TICKS
(`WorldServerStepShell` via `WorldConsoleWaitGate.PublishTick`), never wall time. Echo:
`[world.wait: N ticks from T — releasing at tick R]`. Being Immediate, the
barrier holds `world.wait` itself until a preceding mutation lands, so its
countdown starts from a tick that already contains it. Use it for
read-after-write across ticks (e.g. asserting motion after input).

## The mirror

`WorldConsoleMirror` (`ICommandObserver`, 64-line ring): records the echoed
input line plus each output line with its refusal flag, catches
tick-deferred verdicts of Simulation lines through `OnCommand`, and records
unsolicited edit echoes (`RecordEcho`, fed by `EchoTap`). The published
frame's `Input` is always empty — no on-screen prompt, no keystroke path.
`world.console [on|off]` toggles visibility.

`world.binding-bar [on|off|auto] [player]` is the binding bar's parallel live
control and read-back. `on`/`off` force a side, `auto` returns to the authored
enabled/rest policy, and every form reports the resolved per-seat policy,
current hidden state and reason, and layout values.

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
  composed frame]`. No file exists yet. **Fence a frame (`world.wait`) before
  reading it.**
- stderr, when the frame lands: `[capture] unified overlay -> <path>` (the
  overlay decorator served it) or `[debug] captured frame N -> <path>` (the
  engine node beneath it did). THIS is the line that says a file exists.
- stderr, at shutdown: `[world.screenshot] WARNING: a capture of <path> was
  still pending when the run ended … NO FILE WAS WRITTEN`
  (`WorldPostBuildWiring`'s `ApplicationStopped` drain).

Arming a second capture while one is still pending is REFUSED by name
(`SdfWorldRender.PendingCapturePath`) and counts in `wire.errors`: the render
chain holds exactly ONE pending path, so arming over it would silently drop a
file the caller was already promised. Any Simulation-routed line between two
shots fences a composed frame for free (the drain barrier), which is why
back-to-back shots separated by an ordinary write never trip it (the pattern
the now-QUARANTINED `docs/verification/hud-document` battery exercised — see
[hud.md](hud.md)'s "Verifying" section).

## The document has ONE door — do not add a per-section verb

`world.row.set <path> <json>` and `world.row.remove <path> <key>`
(`WorldRowCommandModule`) are the whole document-mutation surface. `<path>` is a
dotted document member path in the document's own camelCase JSON names — `kits`,
`placements`, `hud.panels`, `views.layouts`, `views.seatRig`. An unknown path
refuses by name and enumerates its siblings.

**Adding a section means adding a ROW to `BuildSections`, never a verb pair.**
That table carries the only three facts the document model cannot supply: whether
the section is a keyed list, which member is its key, and its
upsert/remove `WorldMutation` pair. The 2026-08-07 reduction wave collapsed 49
per-section verbs into these two; re-growing one is the regression that wave
exists to prevent. `puck schema` documents payload shapes — cite it, but there is
deliberately NO runtime schema validation (owner deferred the gate; validation
stays at the full-document revalidation on apply).

Same rule for per-field convenience: a verb that reads a row, changes one field
and submits the whole row back is a stale read against the same batch's own
composing writes. That is a defect class, not a shortcut.

## Grammar conventions for new verbs

- `family.verb` dotted names (`world.*`, `player.*`, `screen.*`,
  `editor.*`, `profile.*`, `storage.*`, `capture.*`, `replay.*`,
  `audio.*`, `market.*`); names case-insensitive on the full parse, ordinal
  on the fast path.
- Row-valued mutation verbs take ONE inline-JSON argument in the exact wire
  shape of the document section row, reconstructed from the raw text
  (quotes survive) and parsed via `WorldJsonPayload.TryParse` — a parse
  error echoes inline and submits nothing.
- **A stepped twin is not a verb.** `.next`/`.prev`/`.up`/`.down` fold onto the
  verb they step: keep it `Bindable`, declare `valueKind: Axis1D`, give the bound
  rows an `Axis(±1f)` constant, and read `context.Value` when
  `context.Source is not null` (non-null ⇒ dispatched by a binding; it is null on
  every text path). Do NOT discriminate on `context.Value.Kind` — that is only
  coincidentally reliable while everything declares `Digital`.
- **The recompose trap.** A binding whose dispatched value kind disagrees with its
  command's declared `ValueKind` is only NARRATED by the boot sweep, but
  `WorldSeatBindings.RecomposeSeat` REJECTS the whole seat document and keeps the
  prior mapping — so every later `player.bind`, profile load or context regroup is
  silently discarded. Boot narration is NOT proof a binding change is safe: force a
  recompose (`player.bind 1 keyboard.p editor.status`) and assert stderr carries no
  `recompose rejected` line.
- `player.bind` can carry a constant for a command destination with `value:<v>`
  (validated against the destination's declared kind; mutually exclusive with
  `scale:`). It can only address the play group's resting page or a
  `(group, chord)` row — never a named sub-page, which is a known open gap.
- No-arg → echo current value. Refusal → same bracketed shape + `IsError`.
- Choose routing by determinism class, not convenience: anything that
  touches sim state is `Simulation`; a read-back is `Immediate`. Routing
  describes when the command handler runs. For example,
  `world.addon.reload` is Simulation-routed but calls the addon runtime
  synchronously once its handler runs, while `world.addon.mount` enqueues a
  `PendingOp.AddonLifecycle` for the tick boundary. Both buy the drain
  barrier, so a following read waits for settled state.
- New decision surface ⇒ read-back verb in the same change.
