# Concise state authoring

A world's `state` section is authored today as an array of JSON-shaped row
objects. The author spells `name`, `kind`, `cells`, `key`, and `value` for every
row, and reads a cell as `row[key]`. This plan proposes declaration syntax for
tables and slots, dot access to cells, row bounds, per-second accumulation,
pile and grid declarations, and conditional effects. Each feature arrives as a
separate change with its own completion condition.

Some stages only change how a document is spelled. Others change the state
engine: they change how existing documents behave, move state hashes, and change
persisted formats. Each engine stage is specified and gated on its own.

The [world transpiler guide](../../src/Puck.World.Transpiler/README.md) and the
[DSL reference](../reference/dsl.md) own current language behavior. The
[state reference](../reference/state.md) owns the row, cell, and trait model.
[State consolidation](state-consolidation.md) section 2 owns merging slot and
keyed trait handling, which stage 5 carries out.
[World release management](world-release-management.md) owns which persistence
changes may reach an official persisted world.

## Implementation status

Every stage's core behavior has landed.

- **Stage 2 (table/slot declarations)** and **stages 7-8 (pile/grid
  declarations)** are implemented in `Puck.Transpiler`/`Puck.World.Transpiler`:
  parsing, lowering, the full refusal set (PUCK049-PUCK066), formatter
  round-tripping, decompiler sugar with a `row { }` fallback for anything not
  representable losslessly, and LSP completion/hover/`documentSymbol` support.
  `draw`, `shuffle`, and `deal` sugar over a `pile` are fully implemented:
  they parse as action statements in `.puck`, lower to `StateTransform.Transfer`
  and `StateTransform.Shuffle`, and round-trip losslessly through the decompiler.
- **Stage 3 (dot access)** is implemented in `ExpressionSpelling` and threaded
  through `WorldModuleNamespace`, the linter, and the LSP; `row.key` and
  `row[key]` compile to identical rule facts.
- **Stage 4 (bounds and overflow)** replaced `StateRow.Min`/`Max`/`NonNegative`
  with an independently-optional envelope and `StateOverflow`, added
  `StateRow.TryAdmitWrite` as the one admission point every write path calls,
  and migrated every shipped document; `nonNegative` is refused by the strict
  parser. Dedicated law tests in `StateBoundsLawTests` guard clamping, refusal,
  and arithmetic overflow saturation at 128-bit boundaries.
- **Stage 5 (behavior inherits per cell)** consolidated slot/keyed trait
  resolution into `EffectiveBehavior`, moved timing state onto
  `StateCell.Clock`, and implemented the settle-on-change transition table.
  Dedicated law tests in `StateCellClockTransitionLawTests` guard value, velocity,
  and epoch resets on behavioral transitions.
- **Stage 6 (per-second accumulation)** renamed `StateAdvance` to
  `PerSecondNumerator`/`PerSecondDenominator`, added the engine-tick epoch
  (`StateCellClock.EpochEngineTick`, independent of `EpochTick`), and threaded
  a real engine-tick coordinate through every rebase path (journal, undo,
  batches, administrative writes, checkpoint restore, `world.save` settling).
- **Stage 9 (conditional effects)** added `ActionEffect.If` to `Puck.State`,
  with condition/branch compilation, transaction-nesting and `save`-in-branch
  refusals, `world.rule.trace` branch narration, and replay/undo reproducing
  the taken branch's mutations without re-evaluating the condition. The
  transpiler lowers `.puck` `if`/`else if`/`else` onto this effect — the one
  control-flow keyword the world vocabulary's rule body carries; `repeat` and
  `break` still have nothing to lower onto and stay refused as PUCK037. The
  formatter needs no dedicated support (it is textual and already
  meaning-preserving over control flow); the decompiler prints `if`/`else if`/
  `else` losslessly, and the LSP completes a declared table's own cell keys
  after a dot-access read, recovering from an in-progress, not-yet-closed
  statement. Dedicated law tests in `ConditionalEffectLawTests` guard branch
  evaluation, journal rollback, and trace narration.

All test suites pass, including dedicated law suites for bounds, clock transitions,
and conditional effects.

## Delivery order

1. Stages 1 to 3 (contract, declarations, dot access) are the first milestone.
2. Stages 7 and 8 (piles, grids) change only the transpiler and may proceed
   once stage 2 lands. They don't wait for any engine stage.
3. Stages 4, 5, 6, and 9 change the engine. Each is specified, reviewed, and
   gated independently; none blocks the language stages.

## Facts the design rests on

- **One row has one kind and one envelope.** `StateRow` declares `Kind`, `Min`,
  `Max`, and `Overflow` once for all of its cells
  ([StateRow.cs](../../src/Puck.State/StateRow.cs)). Cells with different kinds
  or ranges need separate rows.
- **Only world state holds `StateRow`s.** `state.body` and `state.identity` hold
  `ActionStateSlot`, a different record with a float initial value and
  Counter/Timer kinds ([WorldState.cs](../../src/Puck.World.Schema/WorldState.cs)).
- **One method decides every write.** `StateRow.TryAdmitWrite` computes the
  result without wrapping (a 128-bit intermediate), then either admits it,
  refuses it by name (`Refuse`, the default — including an overflow on a row
  with no envelope, counted as an `Arithmetic` rule failure), or clamps it to
  the authored bound or the 64-bit storage limit (`Saturate`). The rule frame
  write, mutation compose, ring push, board combine, write sets, and the rule
  evaluator's no-op check all decide through this one method
  ([StateRow.cs](../../src/Puck.State/StateRow.cs),
  [RuleEvaluator.Effects.cs](../../src/Puck.State/RuleEvaluator.Effects.cs),
  [StateFrame.cs](../../src/Puck.State/StateFrame.cs)). Only computed reads,
  such as an advancing row's value, clamp independently, through
  `ClampToEnvelope`.
- **`advance` is authored per second and evaluated on engine ticks.** The rate
  is a fraction of the row's displayed unit per second, evaluated against the
  same engine-tick clock durations authored as `valueSeconds` already use, at
  50,400 per second — not the simulation tick. A cell's `StateCellClock`
  carries two independent epochs: a write rebases `EpochEngineTick` for
  `advance`, while `EpochTick` (simulation ticks) still serves `dynamics` and
  `cycle` ([StateAdvance.cs](../../src/Puck.State/StateAdvance.cs),
  `StateCellClock` in [StateRow.cs](../../src/Puck.State/StateRow.cs)).
- **The two clocks are independent.** A server keeps a completed simulation
  tick and a separate, checkpointed engine-tick total accumulated from each
  step's width ([WorldServer.Step.cs](../../src/Puck.World.Server/WorldServer.Step.cs)).
  The simulation rate can change in a live session, and both counters stay
  contiguous across the change
  ([WorldInstanceHost.cs](../../src/Puck.World.Server/WorldInstanceHost.cs)).
  Engine time is therefore not a simulation tick multiplied by the current step
  width.
- **The journal records both clocks.** Each entry is `JournalEntry(ulong Tick,
  ulong EngineTick, WorldMutation Mutation)`; undo rebases a `dynamics` or
  `cycle` epoch from the simulation tick and an `advance` epoch from the engine
  tick
  ([WorldServer.Checkpoint.cs](../../src/Puck.World.Server/WorldServer.Checkpoint.cs)).
- **Saving settles every clock.** `world.save` writes each cell's live value at
  epoch zero: it samples a dynamics cell's position and velocity, and keeps a
  cycle cell's settled phase and substep remainder, on that cell's own
  `StateCellClock`
  ([WorldSessionCapture.cs](../../src/Puck.World/WorldSessionCapture.cs)).
  Authority checkpoints and journals keep live epochs.
- **A row's behavior is the default for every cell.** `EffectiveBehavior`
  resolves a cell's own `Advance`, `Dynamics`, or `Cycle` if it declares one
  (including an opt-out to `none`), otherwise the row's default — for slot and
  keyed rows alike, and for a key a write mints later
  ([EffectiveBehavior.cs](../../src/Puck.State/EffectiveBehavior.cs)).
- **An undeclared domain is inferred.** A row with no cells and no capacity is
  inferred to be a slot (`StateRow.InferDomain`).
- **Rule operands are text.** The parser checks each operand through
  `ExpressionSpelling.TryParse`, and the emitter writes the text into the JSON
  unchanged
  ([WorldDocumentEmitter.Rules.cs](../../src/Puck.World.Transpiler/Lowering/WorldDocumentEmitter.Rules.cs)).
  Assignment targets resolve through the same parser.
- **Dot access is part of the expression grammar, and cell names forbid dots.**
  `ExpressionSpelling` splits an unreserved, unquoted `player.health` into the
  state read `player[health]` at parse time; a reserved `$` name or a
  backquoted name keeps its dots as part of one name instead. Row and cell
  names themselves still exclude dots, so the HUD binding `state.<row>.<key>`
  stays unambiguous
  ([ExpressionSpelling.cs](../../src/Puck.State/ExpressionSpelling.cs),
  [SafeName.cs](../../src/Puck.State/SafeName.cs)).
- **Imports rename with an underscore.** An imported fragment's names become
  `<alias>_<name>`. `WorldModuleNamespace` rewrites expression text through
  `ExpressionSpelling`'s own name-scanning and dot-splitting APIs rather than a
  duplicate lexer of its own
  ([WorldModuleNamespace.cs](../../src/Puck.World.Schema/WorldModuleNamespace.cs)).
- **World rules can branch.** `ActionEffect.If` compiles a condition and two
  branches; a `.puck` rule's `if`/`else if`/`else` lowers onto it
  ([ActionEffect.cs](../../src/Puck.State/ActionEffect.cs)). `repeat` and
  `break` still have nothing to lower onto and are refused as PUCK037.
- **Replay and undo reproduce different things.** Tape replay re-drives
  recorded inputs, so rules run again. Undo rebuilds state by reapplying
  journaled mutations, so rules don't run. A replay hash covers the explicitly
  hashed state trajectory, not the journal or the whole document.
- **Official worlds refuse breaking persistence changes.** A breaking
  persistence change needs a separately designed forward transition before it
  can ship to an official persisted world. An offline converter doesn't provide
  one ([world release management](world-release-management.md)).
- **The language spells names out.** The DSL reference prefers function syntax
  and full names over glyphs and abbreviations
  ([dsl.md](../reference/dsl.md#names-are-spelled-out)).

## Decisions

These decisions bind every stage.

1. **One declaration produces one row.** The transpiler never splits a
   declaration into several rows, never generates a state name, and never
   changes grants or bindings.
2. **Declarations are accepted only under `state.world`.** A declaration in
   `body` or `identity` is refused by name. Those scopes hold a different record
   and need their own design.
3. **Declarations don't imply storage scope.** A table named `player` is one
   world row, not per-player storage.
4. **One trait has one spelling.** Accumulation is `advance`, with the sign
   carrying direction. There is no separate `regen` and `decay`. Modifiers use
   function syntax with spelled-out names, following the DSL's naming rule.
5. **Dot access belongs to the state engine's grammar.** `row.key` becomes an
   `ExpressionSpelling` spelling, so the DSL, JSON documents, and every other
   expression consumer agree on what it means.
6. **A range names its overflow policy.** A row either refuses a write that
   leaves its range (today's behavior) or saturates it. The policy is authored,
   and refusal is the default: a refused write is reported, while a silent clamp
   can hide an authoring error.
7. **A row's behavior is the default for every cell.** A behavior is one of
   none, advance, dynamics, or cycle. A row's behavior applies to every cell,
   including keys created later by writes. A cell's own behavior replaces the
   row's as a whole, never trait by trait, and a cell spells `none` to opt out.
8. **Rates are authored per second and do not depend on the simulation rate.**
   The engine evaluates accumulation on the engine-tick clock that durations
   already use. A module then means the same thing in any host, and the
   transpiler never needs a document's effective `rateHz`.
9. **Equivalence is classified per stage.** Declaration stages (2, 7, 8)
   require byte-identical JSON against an explicit equivalent. Dot access
   (stage 3) keeps authored operand text, so it requires semantic equivalence:
   identical tokens and identical compiled rule facts.
10. **Engine stages prove behavior independently.** Each engine stage asserts
    independently derived expected outcomes, proves that behavior it doesn't
    touch is unchanged, and names the contract change behind every re-recorded
    baseline. Running the same replay twice is an additional determinism check,
    never the proof of correctness. Undo and checkpoint correctness are asserted
    separately, because a replay hash doesn't cover them.
11. **Every persistence-changing stage delivers its release eligibility.** The
    stage names each persisted format it changes and its disposition. Old data
    must never load with a new interpretation: a renamed or reshaped field makes
    the strict parser refuse the old form. Development availability and
    eligibility for official deployment are stated separately; the latter
    requires the forward transition the release plan demands.

## Proposed syntax

This is the target shape. Stage 1 turns it into an exact grammar.

```puck
state {
    world {
        table vitals : Int bounds(minimum: 0, maximum: 100, overflow: Saturate) {
            health = 100
            mana = 50 advance(perSecond: 5)
        }

        slot gold : Int = 10 bounds(minimum: 0)

        slot shield : Fixed = 10.0 bounds(minimum: 0.0, maximum: 10.0) advance(perSecond: -0.5)

        row {
            name: "deck"
            kind: "Int"
            domain { $type: "keysOf", row: "cards", ordered: 1 }
        }
    }
}

rule "spend-mana" {
    when vitals.mana >= 10
    vitals.mana = vitals.mana - 10
}
```

`gold` is a separate slot because its range differs from the vitals table.
`row { }` is the escape to the explicit representation for anything the
declarations don't cover, such as draws, phases, or visibility. A `state.world`
section is written either as today's array or as a declaration block, never
both.

## Persisted formats

Stages 4, 5, and 6 each change at least one of these. Each stage's release
eligibility section gives the disposition of every format it touches.

| Format | Written by |
|---|---|
| World documents | `world.save`, authored assets, generated `.puck` output |
| Owned-identity documents | The owned-world catalog |
| Authority checkpoints, including the undo journal | Hosts and the silo |
| Replay tapes and their boot images | `replay.record` |
| Release fixtures and archives | World release preparation and capture |

## Stage 1: syntax and lowering contract

Write the exact contract into this plan before any code lands. It needs:

- The grammar for `table`, `slot`, cell entries, modifiers, and `row { }`,
  including how the parser distinguishes a `world { }` declaration block from
  today's `world [ ]` array. It also covers how cell entries (`health = 100`)
  stay distinct from rule assignment statements, which use the same spelling.
- The explicit JSON each accepted form produces, including value spelling per
  kind. A declaration must emit exactly what the explicit row form emits.
- Every refusal, with its diagnostic code and source location:
  - Duplicate row names or keys.
  - Reserved `$` keys.
  - Invalid defaults.
  - A capacity smaller than the declared cells.
  - `bounds` or `advance` on `Bool` or `Text`.
  - More than one behavior on a row or cell.
  - Declarations in `body` or `identity`.
  - Mixing the array and block forms.
- Defaults. A table with no cells and no capacity emits `domain: keys`, so an
  empty table is never inferred to be a slot. It emits no capacity unless one is
  declared.
- How `let`, `template`, and compile-time `for` compose with declarations.
- Formatter output and decompiler behavior. The decompiler prints a declaration
  only when the whole row can be represented without loss; otherwise it prints
  `row { }`.

**Complete when** every example in the contract has an exact lowering or a
specified refusal, and the contract has been reviewed.

## Stage 2: table and slot declarations

This stage changes only the transpiler. Parse declarations into new syntax
nodes in `Puck.Transpiler`, then lower them in `Puck.World.Transpiler` through
the existing slot (`value`) and keyed (`cells`) representations. Preserve row
and cell order, and register source-map entries for every row and cell.
`bounds` and `advance` are refused until stages 4 and 6 land.

Ship with the rest of the authoring surface:

- Formatter support.
- Decompiler support.
- LSP completion, hover, and document symbols.
- `editors/vscode/syntaxes/puck.tmLanguage.json`.
- Updates to the transpiler README, the DSL reference, `puck-dsl`, and
  `puck-world`.

**Complete when:**

- Each concise declaration compiles byte-identically to an independently
  authored explicit document and passes `puck compile --validate`.
- Formatting is idempotent and preserves meaning (`FormatterRoundTripTests`).
- Decompiling and recompiling reproduce the JSON.
- The parser, emitter, and diagnostic suites cover every refusal.
- `ShippedWorldsParityTests` and the cartridge round-trip tests still pass.
- A world authored with declarations boots under `dotnet run --project
  src/Puck.World -c Release -- --exit-after-seconds 2`, and `world.state` echoes
  its rows.

## Stage 3: dot access

This stage changes `Puck.State`'s expression grammar and its consumers. Teach
`ExpressionSpelling` that an unreserved name of the form `row.key` is the state
read `row[key]`:

- The part after the dot is a literal cell key. Dynamic keys, `$each`, bindings,
  and expressions keep bracket form.
- A reserved `$` name keeps its dotted segments unchanged. Existing examples
  include `cell:<row>.<key>` inside `$symmetry:` and body references.
- Audit every infix position that names something other than a cell. Topology,
  table, and generator names are `SafeName`s, which may contain dots.
- Replace `WorldModuleNamespace`'s duplicate name lexer with `ExpressionSpelling`
  itself, so import renaming cannot drift from the grammar it rewrites.
- Leave `ExpressionSpelling.Print` producing brackets. The emitter's pass-through
  of operand text is unchanged, so a dotted source keeps its dotted text in the
  JSON.

Assignment operators are unchanged. A dotted target admits exactly the
operators a bracket target admits, and PUCK039 still refuses the rest.

**Complete when:**

- `row.key` and `row[key]` parse to identical tokens and compile to identical
  rule facts, in gates, operands, bindings, scores, and assignment targets.
- Tests pin the canonical forms: a dotted source keeps dotted text in the JSON,
  `ExpressionSpelling.Print` produces brackets, and the decompiler prints the
  JSON's text unchanged. Decompiling and recompiling a dotted document
  reproduces its JSON.
- `ExpressionSpellingLawTests` covers these cases:
  - Decimals such as `0.5`.
  - Reserved names.
  - Imported names after namespacing.
  - Backquoted names.
  - Numeric keys.
  - A trailing dot during editing.
- The LSP completes the keys of a declared table after `vitals.` and recovers
  from the incomplete input.
- A JSON world using dotted operands boots and its rules fire.

## Stage 4: bounds and overflow policy

This stage changes the state engine.

1. **Replace the range fields.** Replace `StateRow.Min`, `Max`, and
   `NonNegative` with one envelope: an optional minimum, an optional maximum,
   and an overflow policy of `Refuse` or `Saturate`. A one-sided range replaces
   `NonNegative`, and a timer becomes a row with minimum zero.
2. **Define arithmetic overflow.** Every write computes its result without
   wrapping, using a 128-bit intermediate or a sign test that reports the
   overflow direction:
   - `Saturate` clamps the true result to the authored bound on that side, or
     to the 64-bit storage limit when that side has no bound.
   - `Refuse` refuses by name, both for an overflow and for a result outside
     the range.
   - A row with no envelope refuses an overflow by name as an `Arithmetic`
     failure. This deliberately replaces today's silent wrap and is listed as a
     behavior change in the stage's change description.
   - `set`, `add`, expressions, frame writes, and mutation composition follow
     the same rules.
3. **Enforce the policy in one place.** Put policy enforcement in one `StateRow`
   method that every write path calls instead of comparing against
   `ClampToEnvelope`. The paths are:
   - The rule frame write.
   - The mutation compose path.
   - Ring push.
   - Board combine.
   - Write sets.
   - The rule evaluator's no-op check.
4. **Keep mutations deterministic.** A saturating write stores the clamped
   value, but the mutation still carries the rule's operand, so reapplying it
   composes the same result. A saturating write never refuses on range, so it
   never triggers a transaction's `onFailure`.
5. **Migrate once.** Migrate every shipped world, fixture, and test document,
   with no compatibility reader for the old fields.
6. **Add the DSL modifier.** Add `bounds(minimum:, maximum:, overflow:)` to the
   declarations.
7. **Echo the policy.** `world.state` shows the envelope and its policy.

**Release eligibility:** the envelope reshapes every row in every persisted
format listed above. Old documents are refused at the removed `min`, `max`, and
`nonNegative` fields. The stage states whether an offline migration is provided
for development state, and records that official deployment waits for a forward
transition under the release plan.

**Complete when:**

- Law tests with independently derived expectations prove refusal and
  saturation at both authored bounds and at `long.MinValue` and `long.MaxValue`.
  They cover `add` and `set`, rows with and without bounds, the frame and
  compose paths, transactions, and a live advancing value.
- Tests prove that rows whose writes stay in range behave exactly as before.
- Undo, checkpoint restore, and replay each reproduce saturated values.
- Every re-recorded baseline names the contract change that moved it.
- The shipped worlds boot.

## Stage 5: row behavior applies to every cell

This stage changes the state engine and carries out
[state consolidation section 2](state-consolidation.md#2-consolidate-slot-and-keyed-cell-behavior-handling).
Resolve each cell's effective behavior once: its own behavior if it declares
one (including `none`), otherwise the row's.

Timing state belongs to the cell. Each cell carries its own epoch, a dynamics
cell also carries its position and velocity, and a cycle cell carries its
substep remainder, so a key created at tick `t` starts from `t`.

When a behavior changes, whether by re-authoring a row's default or a cell's
own behavior, every affected cell settles at the change tick. A cell that
inherits the default is affected when the default changes; a cell with its own
behavior is not.

| Change | Survives | Resets |
|---|---|---|
| Advance parameters change | The current value becomes the new base | The epoch moves to the change tick |
| Dynamics parameters change | The sampled position and velocity, as `world.save` and `RebaseDynamics` already keep them | The epoch |
| Cycle parameters change | The settled phase and substep remainder | The epoch |
| Switching between behaviors, or to `none` | The current value, settled by the old behavior | Velocity and substep to zero; the epoch to the change tick |

Before committing to a representation, read and record how these parts would
change:

- The frame layout's trait handling (`StateFrame.HasTraits` leaves such rows
  unframed).
- `StateReader.Live`.
- `RebaseCellTraits`.
- The save-time settling in `WorldSessionCapture`.
- Checkpoint restore.

A row that declares a behavior before any cell exists must not gain a cell by
normalization.

**Release eligibility:** per-cell timing state changes world documents,
owned-identity documents, checkpoints, and replay boot images. The stage names
the reshaped fields that refuse old data, and states development and official
deployment availability separately.

**Complete when:**

- Law tests with independently derived expectations prove that a key created
  later inherits the row's behavior from its creation tick.
- A cell's own behavior, including `none`, replaces the row's as a whole.
- Every transition in the table above is tested for what survives and what
  resets.
- Rows with only slot traits behave exactly as before.
- Rebase, undo, save-and-reload, and checkpoint restore each reproduce every
  cell's value, velocity, and phase.
- Every re-recorded baseline names the contract change that moved it.

## Stage 6: per-second accumulation

This stage changes the state engine. Author `advance` as displayed units per
second and evaluate it on engine ticks, the base `valueSeconds` already uses.
At a constant simulation rate this gives the same value at every tick boundary
as today's per-tick rational rate. For example, 5 per second at 30 Hz is 1/6
per tick, which is 1 per 10,080 engine ticks. Both reach `floor(5n/30)` after
`n` simulation ticks. Decay is the negative rate. Epochs are recorded in engine
ticks.

**Clock contract.** Every rebase of a trait's epoch reads one engine-time
coordinate, never a simulation tick converted at the current rate:

- Journal entries record the engine-tick timestamp alongside the simulation
  tick, and undo rebases from the recorded engine timestamp.
- Rule writes, administrative writes (`world.state.cell.set` and other console
  and addon mutations), batches, and checkpoint restore use the same coordinate.
- A simulation-rate change moves no epoch.

Resolved during implementation:

- A `rateHz: 0` world never steps — `Advance`/`Step` are never invoked for the
  resident, non-stepping world `simulation.rateHz` already documents, so its
  engine tick (`CompletedEngineTicks`) never advances either. `advance` is
  legal, not refused, on a `rateHz: 0` row; its computed value simply never
  moves past whatever base the last explicit write left it at, exactly like
  every other tick-driven behavior on a world that never steps. This is a
  consequence of the existing rate-0 contract (no code was added to special-
  case `advance` against the simulation rate — `StateAdvance` reads only the
  engine tick, never `simulation.rateHz`), not a separate refusal.
- Save-time settling and checkpoints record `StateCellClock.EpochEngineTick`
  beside `EpochTick`; `world.save` settles it to zero like every other epoch.
- `world.state`/`world.state.row` echo the rate as
  `advance=<numerator>/<denominator>/s@engineEpoch<n>`.

If engine ticks prove unsuitable, the fallback is to keep per-tick storage and
convert from the effective rate. The transpiler would use `PuckDocumentComposer`,
which already composes a root document's basis and imports, and a module
without a root would refuse per-second rates. The clock contract above still
applies to the fallback.

The DSL spelling is `advance(perSecond: <rate>)`. Decimal rates must reduce to
an exact fraction that fits in 64 bits.

**Release eligibility:** the rate fields are renamed so that a per-tick rate is
refused rather than read as per-second. Saved documents settle epochs to zero,
so they need only the rate migration. Checkpoints and their journals carry live
epochs and simulation-tick journal timestamps, and need a migration or refusal
of their own. Development and official deployment availability are stated
separately.

**Complete when:**

- Law tests with independently derived expectations prove a per-second world
  and a hand-authored explicit world agree at tick boundaries, after writes, at
  bounds, and for positive and negative fractional rates.
- The same holds at 30 Hz, 60 Hz, and 240 Hz, and in a world that inherits its
  rate through `basis`.
- A test writes an advancing cell, changes the simulation rate, writes again,
  then undoes both writes and continues from a checkpoint, asserting the value
  at each step.
- An administrative write and a rule write rebase identically.
- A law test proves an `advance` row on a `rateHz: 0` world never accrues past
  its last explicit write, matching the decision above.
- `hgb-mirror.puck`'s clocks are migrated, and its capture still shows the same
  frames.
- Every re-recorded baseline names the contract change that moved it.

## Stage 7: pile declarations

This stage changes only the transpiler and doesn't wait for any engine stage. A
pile names its token-domain row explicitly and lowers to `domain: keysOf` with
`ordered` set. The contract specifies:

- Initial members and their order.
- Token uniqueness.
- Capacity against the token domain.
- Refusals for an unknown or non-keyed domain row and for duplicate tokens.

New `draw`, `shuffle`, or `deal` spellings are separate work, because each maps
to existing transfer and arrange effects that need their own contract.

**Complete when** an ordered-deck example compiles byte-identically to an
independently authored explicit document and boots. Every refusal names its
source location.

## Stage 8: grid declarations

This stage changes only the transpiler and doesn't wait for any engine stage. A
grid declaration names:

- Its dimensions.
- Its indexing order.
- Its topology.
- Its occupancy row and empty value.
- The token row that supplies positions.

It generates only the topology, `cellsOf` domain, `valuesFrom`, and `inverse`
relationships that the declaration spells out.

**Complete when:**

- A board-occupancy example matches an independently authored explicit
  document byte-for-byte and boots.
- Invalid references, topology mismatches, duplicate tokens, and writes to a
  derived row receive diagnostics that name the fix.

## Stage 9: conditional effects

This stage changes the state engine: a new `ActionEffect` arm. Splitting a
branch into separate rules is not equivalent, because rule writes land on the
tick's frame and rule verdicts are cached between ticks.

First decide and record the answers to these questions, then write tests
against them:

| Question | Proposed answer |
|---|---|
| What does the condition read? | The frame at the effect's position, including earlier effects' writes in the same firing. Confirm that earlier effects are visible today before adopting this. |
| Condition true or false | Run `then`, or `else` when present. A false condition is not a failure. |
| Condition evaluation fails | Report the existing arithmetic or refusal failure in `world.rule.failures`, run neither branch, and apply the same boundary a failing top-level effect has. |
| An effect inside a branch fails | Apply the same policy as the same effect at top level. A branch is not a transaction. |
| Atomic branches | Written as `transaction` inside the branch. A branch may sit inside a transaction, but transactions still don't nest. |
| Tape replay | Rules run again, so the branch is chosen again from the same frame state. |
| Undo and checkpoint journals | The journal holds the mutations the taken branch produced. Reapplying them runs no condition and records no branch choice. |

Touch the rule compiler (effect facts, reads, writes, and work budget), the
evaluator, the frame, `world.rule.trace` (which must show the branch taken),
the emitter, the decompiler, and the formatter. PUCK037 keeps refusing `repeat`
and `break`.

**Complete when:**

- Tests distinguish a false condition from a failed evaluation.
- Tests prove branch selection, visibility of earlier writes, and rollback
  inside a transaction.
- Tape replay reproduces the branch taken, and undo restores the state before
  the branch's writes.
- Rules without `if` behave exactly as before.
- A world using `if` boots and its trace shows the branch taken.

## Verification for every stage

- Run `tests/Puck.World.Transpiler.Tests`, plus
  `tests/Puck.GamingBricks.Transpiler.Tests` when `Puck.Transpiler` changes.
- Run `tests/Puck.State.Tests` and `tests/Puck.World.Tests` for engine stages.
- Keep `ShippedWorldsParityTests` passing. Recompile committed `.puck` sources
  and commit both halves of each cartridge.
- Boot a representative world with the new syntax by running `Puck.World`.
  Build success is not verification.
- Prove the equivalence class the stage's decision assigns: byte identity for
  declarations, semantic equivalence for dot access. Don't compare replay
  hashes of identical documents.
- For engine stages, meet decision 10: independent expected outcomes,
  preservation tests for untouched behavior, separate undo and checkpoint
  assertions, and a named contract change behind every re-recorded baseline.
  Then run the same replay twice as an additional determinism check.
- For persistence-changing stages, meet decision 11 before the stage is
  complete.
- Update affected skills in the same change: `puck-dsl`, `puck-world` and its
  state references, and `sdf-authoring` if examples move.
- Migrate a small set of shipped examples and confirm the regeneration gates
  still pass.
