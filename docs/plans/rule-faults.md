# Rule faults: one outcome, one interpreter, one journal

A design review of rule-fault propagation after four review rounds, the design
that closes the class, and a plain account of what each part of it is enforced
by and where its guarantee stops. The contract it serves is in
[the register](../decisions/state-and-language.md#expression-semantics): a
fault in an evaluated gate conjunct closes the whole gate, under negation and
disjunction too; a fault is not a false, so anything that remembers a verdict
keeps the last one it read successfully; an effect that faults refuses the
whole firing. Every claim below cites the code it rests on; a design review
of the first draft shaped it.

Three kinds of enforcement are named throughout, and nothing here claims a
stronger one than it has:

- **Structure**: the invalid state has no representation (no field, no case,
  no call site), so the code that would produce it does not compile or does
  not exist.
- **One door**: the state is written by exactly one method, and every other
  path to it is deleted; a grep for the store's writers is the check.
- **An analyzer**: `OutcomeAnalyzer` in `Puck.Analyzers` refuses a spelling the
  structure cannot exclude, as a build error in every project, from the
  compiler's symbols.

Where none of the three applies, the plan says "runtime refusal" or "review",
and means it.

## The verdict on the approach

The fault register on `ArenaScratch` (`src/Puck.State/Arena/ArenaScratch.cs:25-81`)
is the right contract (one place a fault lands, read by the one caller that
decides) on the wrong mechanism: an out-of-band sticky flag beside the value,
on an object that is not the evaluation. It cannot be closed, for four
structural reasons, each of which the rounds found one instance of:

1. **Two channels that must agree by convention.** `RuleExpressions.Run`
   reports failure through a `bool` return plus `out ExpressionFault fault`
   (`src/Puck.State.Rules/RuleExpressions.cs:90-91`), and `TryEvaluate` raises
   into the register only when that out-parameter says `Domain` or `Forever`
   (`:44-45`). A nested `Run` or `TryFold` is handed the caller's own `fault`
   variable (`:215`, `:250`, `:504`); a nested success writes `None` into it;
   a later arithmetic failure returns `false` without reassigning (`:437`,
   `:445`, `:548`). Round 3's first finding is exactly this.
2. **The register lives on a replaceable object.** `IStateReader.Scratch`
   resolves `Arena.Scratch` (`src/Puck.State/IStateReader.cs:68`);
   `WorldRuleHost.Arena` resolves `m_host.Arena` (`src/Puck.World.Server/WorldRuleHost.cs:143`),
   which `AdoptPreparedArena` replaces (`src/Puck.World.Server/WorldServer.Arena.cs:225-226`)
   from `CommitTransactional` (`src/Puck.World.Server/WorldRuleHost.Arms.cs:119-128`)
   in the middle of `FireEffects`, which captured `scratch` once
   (`src/Puck.State.Rules/Evaluation/RuleEvaluator.Effects.cs:70`) and opened
   its delivery scopes on the captured object (`:141`) while every raise
   resolved the new one, so an authored delivery fault after a document
   install threw in a live World. Until step 4 deletes the register,
   `RuleEvaluator.Deliver` opens each delivery's scope on the scratch the host
   serves after the commit; the block "a delivery that faults after its firing
   installs a document is refused, not thrown" in
   `worlds/debug/rule-faults-tests.puck` is the witness W9 grows from. The
   register still lives on a replaceable object, so the reason stands for the
   design. `STALE001` ([hazards](../development/hazards.md#a-reference-captured-across-a-replacement))
   holds every capture across a replacement as a build error: an evaluation's
   own scope covers judging alone (`RuleEvaluator.Judge`), which fires nothing,
   and `RunFixpoint` ends its window on the arena served after the pass
   (`RuleEvaluator.EndPassWindow`).
3. **Deciding places must remember to read it.** `RuleEvaluator.cs:580` and
   `:641`, `WorldRuleHost.Decisions.cs:214` and `:257`, `RuleEvaluation.cs:133`
   and `:179` each poll `RaisedFault`; `GateOpen(faulted: out _)` discards its
   fault at `WorldRuleHost.Decisions.cs:190`, `:195`,
   `WorldRuleHost.DecisionNeighbors.cs:98`, `:324` and `RuleEvaluator.Groups.cs:101`
   on the strength of an enclosing scope.
4. **A read-side register cannot order writes.** The Edge latch is written
   before the firing (`RuleEvaluator.cs:590` then `:618`), a decision's choice,
   timers and candidate before its effects (`WorldRuleHost.Decisions.cs:320-334`
   then `:342`, `:366`), and an arm is evaluated twice, once in preflight on
   the old arena and again in delivery on the new one
   (`RuleEvaluator.Effects.cs:83`, `:146`; `WorldRuleHost.Arms.cs:263-277`).

So the approach is not closed and a fifth patching round would not close it.
Three things change, each removing a mechanism rather than an instance: the
fault travels **in the value**, so no out-parameter or register can disagree
with it; **one interpreter** produces every evaluated value and resolves every
address before an operand reads through it, with the addresses lowered into
the program so they have no second evaluator; and everything a firing decides
is **journaled in the one arena journal**, so a rewind at any nesting depth
restores the latch and the decision's choice with the cells, and the firing's
verdict is read once by one door.

## The design

### The invariant

> Every evaluation of authored text yields an `Outcome` whose fault is a case
> of the value; the only code that produces an `Outcome` from tokens is the
> interpreter; every store a firing changes is written through the arena
> journal, so one rewind undoes all of it; and the firing's verdict is read by
> one door, which commits the journal scope only when no outcome the firing
> folds faulted (a `transaction` answers its own body's fault).

| Clause | Enforced by | Where it stops |
|---|---|---|
| A fault is a case of the value | Structure: `Outcome` and `Resolved<T>` are non-positional structs with private fields; the number or key is reachable only through `TryFinite(out long)` / `TryNamed(out T)`; `default` is the faulted case. | A caller can call `TryFinite` and ignore its `false`. The analyzer refuses an unconsumed `TryFinite`/`TryNamed` result (`OUTCOME002`); inside the interpreter and the door there is no such call, because they switch on the case. |
| No nested evaluation can erase a fault | Structure: `Run` returns its `Outcome`; a nested call's outcome is a local the caller combines by a total rule. No shared `out` variable exists. | None: the mechanism is gone. A wrong *finite* answer from a correct evaluation is a logic defect, not a lost fault. |
| Only the interpreter evaluates tokens | One door plus structure: `RuleExpressions`' `Evaluate` takes a `ref EvaluationFrame`, a `ref struct` with no public constructor; `RuleEvaluator` constructs it. Dynamic keys, live row indices, ring ages and pattern letters are tokens of the enclosing program, so they have no evaluator of their own to lose a fault in. | `default(EvaluationFrame)` is forgeable; `Evaluate` refuses a frame with no reader at runtime (a throw, a defect). `EvaluateDetached` exists for the closed list of detached consumers below; a new caller of it is an analyzer finding (`OUTCOME003`) until it is added to the list. |
| Operands cannot read through a faulted address | Structure: the interpreter resolves every declared address first and passes `ReadOnlySpan<ResolvedKey>` whose cases are `Named` and `Nothing`; `Faulted` returned from the token before the operand ran. | An operand still receives the `IStateReader` and could read the arena under a key of its own making. An operand holds its declared `CompiledCellRef` and `LiveRow` (its read set, price and `Addresses` list are built from them), so the field is not the defect; the analyzer refuses an `IRuleOperand` member that resolves one against the reader (`LiveRow.Resolve`, an `IRuleKey` or `CompiledCellRef` resolution; `OUTCOME004`), so the only addresses an operand reads through are the resolved ones it was handed. A hand-written arena read with a literal key is review territory and is not a fault path. |
| Everything a firing decides rewinds with the firing | Structure: the latch's held bits, the decision bindings and the group progress write their previous value into the arena journal through one participant interface before they change; `StateArena.Rewind(mark)` restores them with the cells at any nesting depth. | Writes outside any scope (checkpoint restore, scoped-replay restore, sweep removal) are not journaled: they run between ticks under the authority gate with no scope open, and the journal refuses a participant record when no scope is open (runtime refusal). |
| One door reads the verdict | One door: `RuleEvaluator`'s `Commit(ref Proposal)` is the only caller of `CommitStateTransaction` and `RewindStateTransaction` for a firing; a proposal's `Decision` is one four-case enum, so "faulted and fires" has no representation. | Code after the arena commit (document install, input resynchronisation, deliveries) cannot be rewound; see [the install boundary](#the-install-boundary). |
| Nothing drops an outcome | Analyzer (`OUTCOME001`): an expression statement or discard of `Outcome`, `Resolved<T>`, `Verdict` or `RuleOutcome`, except in a `catch` that rethrows and in the two bool host entries `OutcomeDiscardAllowlist` names. | A detached consumer that handles `Faulted` wrongly is a logic defect the analyzer cannot see; the closed list below names each one's `Faulted` arm. |

### The shapes

```csharp
public enum FaultCase : byte { Faulted = 0, Finite, Absent, Forever }

/// What one evaluation answered. 16 bytes. default(Outcome) is Faulted(Domain).
public readonly struct Outcome {
    private readonly long m_raw;
    private readonly FaultCase m_case;
    private readonly ExpressionFault m_fault;   // Domain or Forever when Faulted
    public static Outcome Finite(long raw);
    public static readonly Outcome Absent;
    public static readonly Outcome Forever;
    public static Outcome Faulted(ExpressionFault why);
    public FaultCase Case { get; }
    public ExpressionFault Fault { get; }       // None unless Faulted
    public bool TryFinite(out long raw);
}

/// What one operand read: an Outcome in a cell encoding. Replaces RuleFact.
public readonly struct Fact { public Outcome Outcome { get; } public CellKind Kind { get; } }

/// What an address named. Replaces KeyNaming and every (CellKey, out KeyNaming) pair. default is Faulted.
public readonly struct Resolved<T> {
    public FaultCase Case { get; }                 // Named is spelled Finite; Nothing is spelled Absent
    public bool TryNamed(out T value);
}

/// What the interpreter hands an operand: Named(key) or Nothing. There is no Faulted case to receive.
public readonly record struct ResolvedKey(CellKey Key, bool Named);

/// What a gate decided. Replaces every (bool, out bool faulted) pair.
public enum Verdict : byte { Faulted = 0, Closed, Open }

/// What a firing decided, read by the one door. There is no (Faulted, fires) combination.
public enum FiringDecision : byte { Faulted = 0, Closed, Held, Fires }

/// What one evaluation of one rule did. Faulted is distinct from Refused: a group's Skip policy skips a refusal
/// and holds on a fault.
public enum RuleOutcome : byte { Idle, Fired, Refused, Faulted }
```

The zero value is the faulted case everywhere, so an uninitialised or
defaulted outcome refuses rather than reads as zero; `ExpressionFault.Absent`
leaves the enum, because absence is a `FaultCase`, which the register's own
remarks already say (`src/Puck.State/ExpressionFault.cs:6-8`). Deleted with
their callers: `KeyNaming`, `RuleFact`, the register's members
(`RaiseFault`, `EnterFaultScope`, `LeaveFaultScope`, `RaisedFault`,
`FaultScope`), `TryEvaluate`, `TryEvaluateAlone`, every `out ExpressionFault`
and `out KeyNaming` parameter, and `RuleEvaluator.HoldOnFault`.

### Absence and the combining rule

Absence keeps the semantics the register describes
(`docs/decisions/state-and-language.md:504-511`, `expressions.md:205-208`,
`:273-286`); only the carrier changes. Inside one program:

- `Faulted` absorbs: any operation consuming a `Faulted` operand returns
  `Outcome.Faulted` from the loop at once, as every failure exit does today
  (`RuleExpressions.cs:155`, `:437`). `Faulted` never lives on the stack.
- `Absent` is a value on the stack (the mark today, `:158-162`). `IsAbsent`
  and `Coalesce` consume it (`:166-174`, `:201-211`); `Not` converts it to
  true and `And`/`Or` to false, then short-circuit (`:184`, `:189`); every
  other operation consuming it makes the program's outcome `Absent` (today's
  `ExpressionFault.Absent` return, `:240-243`, `:271-276`, `:452`). Nothing
  promotes `Absent` to `Faulted`.
- An operand a `ShortCircuit` skips is not evaluated, so it neither reads nor
  faults.

Between programs, at a gate conjunct: the left source and the right source
are each a whole program evaluated to completion (a literal, an operand or an
expression; `TryReadSource`'s three arms, `RuleEvaluation.cs:202-259`, become
three ways to obtain an `Outcome`). Then: either `Faulted` makes the gate
`Faulted` and the fold returns ahead of `Not`, `All`, `Any` and the next
conjunct; otherwise either `Absent` makes the conjunct false (no comparison
holds against an absence, `:153-155`); otherwise the comparison holds or not
under the fact rules, `Forever` included. This is the current rule with the
two sources' order of evaluation pinned: the absent left program returns from
*its* loop, never from the conjunct's, so `not (cards[zone(hand, last)] + 0 == cards[1 / zero])`
evaluates the right program and faults. A text conjunct differs only in its
comparator and in reading a text rather than a number; its two addresses go
through the same resolution, and a faulted address is a `Faulted` verdict.

An effect source that is `Absent` refuses the effect, as today
(`RuleEvaluator.Effects.cs:1162-1173`). A local that is `Absent` ends the
evaluation `Closed` and leaves the latch entry to the sweep; a local that is
`Faulted` ends it `Faulted` and the entry holds. Whether an absent local
should hold instead is the open question at `open-items.md:3148-3157` and is
not decided here.

### One interpreter

`RuleExpressions.Evaluate(ref EvaluationFrame frame, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind) : Outcome`
is the one loop. Inside it:

- The value stack keeps today's shape (`long[]` plus a mark per slot,
  `RuleExpressions.cs:97-103`); the mark widens from `bool` to `FaultCase`
  and holds only `Finite`/`Absent`.
- **Addresses are program.** The compiler lowers `cards[expr]` to
  `[expr…, Operand(cards)]`, `hist[age]` to `[age…, Operand(hist)]` and
  `$zones[idx]` to `[idx…, Operand(zone cell)]`: an operand declares each
  address it reads through (`IRuleOperand.Addresses`, an `OperandAddress` of a
  `CompiledCellRef` and whether it reads a key or an index), a `CompiledCellRef`
  whose `FeedProgram` is set is a computed address, and `RuleCompiler`'s
  `ShortCircuit` walk puts each such program's tokens in front of the operand
  token, which consumes the values they left (`RuleExpressions.Arity`). The
  interpreter resolves every declared address into a `ResolvedKey`
  (`RuleExpressions.ReadOperand`), a fed one from the stack, so a computed
  address's absence reaches the operand as `Nothing` and its fault ends the
  token. A pattern's `value:` token expression is the operand's
  `IWordFoldOperand.WordProgram`: the interpreter runs it once per token with
  `$token`/`$previous` bound (`RuleExpressions.FoldWord`) before the operand
  matches the word, so a faulting letter faults the read. `world.match` spells
  its word through the same `FoldWord` over the caller's disclosed reader
  (`WorldRuleHost.Patterns.cs`, `PatternOperand.ReadWord`), a detached
  evaluation whose boundary is kept as it is. The operands no longer evaluate
  an expression of their own: `EffectKeyFact`, `HistoryOperand`'s age
  evaluation and `PatternOperand`'s tuple read are gone.
  The two evaluations left outside a program's tokens are the ones step 3
  folds into `Decide` and step 6 into declared effect `Sources`: an operand that
  is a gate side or an effect's source (`RuleOperandReads.Read`) runs its
  computed addresses' programs where it reads, and an effect's destination key
  or a rule's live-row selection (`RuleReads.TryFeed`) runs its program where
  the evaluator resolves it; each is one evaluation of a whole program, never
  nested inside an operand's read.
- **Operands read through resolved addresses.** `IRuleOperand` declares its
  addresses (`ReadOnlySpan<CompiledCellRef?> Addresses`, with `RowFrom` an
  address) and reads through `Read(IStateReader reader, ReadOnlySpan<ResolvedKey> keys) : Fact`.
  The interpreter resolves every address first; a `Faulted` resolution is
  `Outcome.Faulted` for the token and the operand is not called. The
  per-operand meaning of `Nothing` (a board origin of -1 at `src/Puck.State.Rules/Compiler/Operands.cs:547`,
  a start position of 0 at `Operands.Patterns.cs:361-363`, `$previous` on a
  first token reading the unheld cell at `RuleReads.cs:282-296`) is kept
  where it is authored. The same holds for `CompiledVector`
  (`Operands.Vectors.cs:79-107`), `LiveRow`, text reads
  (`RuleEvaluation.cs:287-329`) and effect destinations.
- **Host facets answer facts.** `OperandFact<TFacet>.Read` returns a `Fact`
  (`src/Puck.State/OperandFact.cs:37`), so a world fact that cannot answer is
  in-band. Host code evaluates no program and reads no operand: the host
  sites that do today, program evaluations at `WorldRuleHost.Arms.cs:547`,
  `:657`, `:724` and `WorldRuleHost.DecisionNeighbors.cs:331`, and the
  operand read at `WorldRuleHost.Arms.cs:431` (`effect.Magnitude.Read`),
  become declared `Sources` on the effect that the evaluator reads before
  the arm runs. The inventory of host reads is the `puck references` list for
  `RuleExpressions.Evaluate`, `IRuleOperand.Read` and
  `RuleReads.ResolveKey` outside `Puck.State.Rules` at the time of the step,
  not these five lines.
- **The capability frame.** `EvaluationFrame` is a `ref struct` holding the
  reader. `RuleEvaluator` and the host sites above construct it where they
  evaluate; step 6 leaves the evaluator as the one constructor. It is
  forgeable as `default`, which `Evaluate` refuses at runtime; the frame's job
  is to make a stray evaluation a visible call, not to be cryptographically
  unforgeable.

### The detached consumers

`RuleExpressions.EvaluateDetached(IStateReader reader, program, kind) : Outcome`
is the second entry to the same loop, for a caller that is not a firing. The
list is closed and is `OutcomeDetachedAllowlist` in `Puck.Analyzers`, which
carries each entry's reason; each entry names its `Faulted` arm, and the
analyzer refuses a caller outside the list (`OUTCOME003`). The search score
(`RuleArenaSearchJudge.Score`) returns a `JudgeOutcome` and is not detached, and
`world.match` folds words through `PatternOperand.ReadWord`, so neither is on the
list:

| Caller | Writes simulation state? | `Faulted` arm |
|---|---|---|
| The constant folder (`RuleCompiler.Expressions.cs`) | Indirectly: a folded constant enters a program. | The subtree stays unfolded; `Absent` likewise. |
| The flock affinity (`WorldRuleHost.FlockAffinities.cs`; consumed in `WorldPopulation.FlockAffinities.cs`) | Yes: a steering weight. | A non-finite outcome is a counted `AffinityFailures` and weight zero. The affinity is a perception, not a firing; it has no latch to hold. |
| The browser read-back (`BrowserSession.cs`) | No. | The read refuses with the fault's description. |

### The gate

`RuleEvaluation.Decide(ref frame, GateToken[] gate) : Verdict` replaced
`GateHolds`, `TryReadSource` and the evaluator's `GateOpen(out bool faulted)`;
the separate text branch's register poll is folded in. A `Verdict` is three-valued, so `verdict == Verdict.Open` is
legal C# and reads a fault as closed; what prevents that is that no deciding
place compares a verdict: each hands it to the proposal, and the one door
switches on it. The analyzer (`OUTCOME002`) refuses an equality comparison of
a `Verdict` or `FiringDecision` against a case other than `Faulted` (a
comparison to `Faulted` is the handling); a `switch` with every case is the
spelling.

### The journaled firing

Everything a firing decides is simulation state (the latch "hashes and
checkpoints", `RuleLatch.cs:26`; the decision binding hashes at
`WorldRuleHost.Decisions.cs:465-484`; group progress hashes at
`WorldScopedReplayRuntime.Rules.cs:62`). Today only the arena's cells are
journaled; the rest is written in place and cannot be rewound, which is why a
faulted effect consumes an Edge crossing and a faulted option effect leaves a
chosen decision. The design puts the rest in the same journal.

**The journal participant.** `ArenaJournal` records one entry per overwritten
position (`ArenaJournalEntry(Column, Index, Number, Reference)`,
`src/Puck.State/Arena/ArenaJournal.cs:9`, `:118-136`). It gains one more entry
shape: `Column = ArenaColumn.Participant`, `Reference = IJournalParticipant`,
`Index`/`Number` the participant's own undo record. `StateArena.Rewind`
dispatches it to `participant.Undo(index, number)` beside `Restore(entry)`
(`StateArena.cs:661-675`); `Commit` of the outermost scope discards it with
the rest. A participant records before it writes:

```csharp
public interface IJournalParticipant { void Undo(int index, long number); }
// RuleLatch: before Held[binding] = open, journal.RecordParticipant(this, slot, previous bits)
// DecisionStore: before state = next, journal.RecordParticipant(this, bindingSlot, previous copy's index)
// RuleGroupState: before progress = next, journal.RecordParticipant(this, groupOrdinal, packed progress)
```

`RuleLatch`, the decision store and `RuleGroupState` take the arena's journal
at construction and write through it. A record with no scope open is refused
at runtime (`InvalidOperationException`), which is what makes the lifecycle
writers below explicit. Turn undo (`ArenaUndo.cs:665`) replays the same
entries, so a `rewindGroup` now restores the latch bits its turn moved, which
`InvalidateScheduler` at `WorldRuleHost.Undo.cs:33` only approximated; this is
the direction S10's "rewind as a complete continuation" names.

What this buys over a proposal copy installed by the door: nested scopes. The
search opens scopes around judge firings (`ArenaSearch.Candidates.cs:16`,
`:35`), a savepoint opens one inside a firing (`RuleEvaluator.Effects.cs:689`),
and the job-system memo will open more. A latch written in place inside a
child scope survives the parent's rewind; a journaled one does not. The door
does not have to know what a decision binding is (it is a private type of the
world host, `WorldRuleHost.Decisions.cs:24`, which `Puck.State.Rules` cannot
name); the participant interface is the generic shape.

**Two phases and one door.** One evaluation of any rule kind (an ordinary
rule, an iterated or pool binding, an interaction, a decision binding, a group
trigger, a search judge rule) is:

*Propose* (`RuleEvaluator.Propose(...) : Proposal`): open the scope, evaluate
locals, select live rows, decide the gate, write the latch entry through the
journal, and when the rule fires run the effect sequence into the scope with
each effect's declared sources evaluated by the interpreter just before it (so
a later effect still reads what an earlier one wrote, `rules.md:284`), queue
arms **prepared once** (an arm's `Prepare(ReadOnlySpan<long> values, ReadOnlySpan<ResolvedKey> keys) : Resolved<PreparedArm>`
receives values the evaluator read; the arm never holds a frame), and fold
every step's outcome into one `FiringDecision`. A `transaction` step folds
its own body first: a faulted body is the savepoint's refusal (the register's
transaction clause), so the savepoint rewinds, `onFailure` runs, and the
firing folds the savepoint's result rather than the fault. A decision binding's phase
also writes its next binding state through the journal and fires its common
and option effects into the same scope. Nothing outside the journal is
written.

*Commit* (`RuleEvaluator`'s `Commit(ref Proposal)`), the one door, switching on
`FiringDecision`:

- `Faulted`: `RewindStateTransaction(mark)` restores cells, latch bits,
  decision state and group progress together; the prepared arms are
  discarded; one `Arithmetic` refusal is reported naming the fault; the latch
  binding is touched so the sweep keeps its entry.
- `Closed`, `Held`: `CommitStateTransaction(mark)` (the scope holds only the
  latch and scheduler records).
- `Fires`: preflight the prepared arms as a sequence, `PrepareTransactional`,
  `PrepareStateCommit`; a refusal is `Refused` and rewinds exactly as
  `Faulted` does; otherwise `CommitStateTransaction(mark)`, then the install
  sequence below.

`Propose` and `Commit` are the only callers of `BeginStateTransaction`,
`CommitStateTransaction` and `RewindStateTransaction` in `Puck.State.Rules`
(the console's cell commands and the addon runtime have doors of their own,
`WorldDocument.StateCommands.cs:22-30`, `WorldAddonRuntime.Cells.cs:58-75`).

### The install boundary

`CommitStateTransaction` closes the arena scope at
`WorldServer.StateTransactions.cs:30` and then does fallible work: it
resynchronises grants, body cells and input owners and **throws** when input
synchronisation refuses (`:35-38`). `CommitTransactional` then adopts the
proposed publication and installs the prepared document
(`WorldRuleHost.Arms.cs:119-128`), and the deliveries run. Nothing after
`:30` can be rewound, and the catch around `Propose`
(`RuleEvaluator.Effects.cs:90-95`) never covered it. The design does not
pretend otherwise. It draws the boundary and moves what can refuse to the
right side of it:

- **Before the arena commit, everything that can refuse has run.**
  `PrepareStateCommit` already runs `ValidateStateTransaction`
  (`WorldRuleHost.Arms.cs:32-35`), which validates the touched bodies' input
  selections (`StateTransactions.cs:62`). The design makes
  `Input.TrySynchronizeBodies` a two-step prepare/apply: the prepare runs in
  `PrepareStateCommit` and can refuse (rewinding the firing); the apply after
  the arena commit cannot. The `throw` at `:37` becomes unreachable by
  construction of the two-step and is kept as the defect report it is.
- **After the arena commit, the install sequence is non-refusing by
  contract**: latch and decision state are already committed with the cells
  (they were in the journal), the document unit was prepared and
  `InstallPrepared` cannot refuse (`WorldRuleHost.Arms.cs:19-20`), and each
  delivery is a counted refusal that undoes nothing (`rules.md:472`).
- **A throw after the boundary is a defect that leaves the authority
  inconsistent**, exactly as today; the design makes it one place
  (`RuleEvaluator`'s `Commit`, after `CommitStateTransaction`) and names it.
  There is no second rollback mechanism, because none can roll back a
  delivered cue or an installed document; AGENTS.md rule 4 wants the defect
  fixed, not masked.

### Prepared arms and their targets

A prepared arm carries the values the read phase computed and the identity
they denote: a body index **with its population generation**
(the host population's `Generation(index)`, as `DecisionBodyLive` reads it,
`WorldRuleHost.DecisionNeighbors.cs:87-88`), a placement ordinal with the
proposed definition it was composed against, a pose cell with its topology.
Delivery checks the generation and refuses the arm as `BodyInactive` when it
moved, the refusal already delivered for an inactive body today
(`WorldRuleHost.Arms.cs:646-652`); the document unit is composed against the
proposed definition and installed as that unit (`:15-20`), so its identities
are its own. A proposal never crosses an authority boundary: `Propose` and
`Commit` run back to back on the tick thread under the authority gate
(`WorldTick.Step.cs:74`), and a checkpoint, a scoped-replay restore or a
transfer runs between ticks, when no proposal exists and no scope is open.

A decision's liveness recheck between its common and option effects is the
`IFiringContinuation`'s `Next`, handed the arms the common effects queued
(only the branches they took), and it reads the **proposed** population.

**The invariant.** The option runs only when its pair is the same two active
bodies in the population that the install's reconcile produces from the
firing's proposal: the document the queued rows compose to, and the arena as
the common effects leave it.

**What enforces it.** The reconcile has one implementation
(`WorldPopulation.Reconcile`): the structural pass (retire the unbindable,
grow or shrink the literal counts), the cell-driven counts and the census
re-clamp (`SetSimulatedCount`) are generic over a slot table. Every install
runs them over the live table (boot and `RebuildPopulation` through
`ReconcileInstall`, a state write through `ReconcileInhabitCounts`, the
`world.population` verb through `SetSimulatedCount`). `ReconcileKeeps` runs the
same code over a scratch copy that records each retire and admit and changes
nothing else. Whether the structural pass runs is `WorldDocument.RebuildsPopulation`,
the predicate `InstallPrepared` uses too. `ProposedPairLive` composes the queued
document rows against the proposed document, as the preflight does, and asks
`ReconcileKeeps` about the pair, reading the cell-driven counts from the arena
with the firing's scope open. A retire rule added to the reconcile is a rule
the forecast answers from. Inhabitant kits resolve against the reconciled
document's own kit rows, which an install compiles its kit table from, so a
proposed kit edit is forecast as installed. When the check fails, the option
effects are not fired and the common effects stand; the firing is not refused
for it. The witnesses are W11's `liveness-*` blocks and
`ReconcileForecastLawTests` for the census re-clamp, which a test block cannot
set up because no admitted scheduled verb seats census bodies.

**Where it stops.** The forecast reads the proposal before the option's own
effects, so an option whose own write zeroes a count cell or whose own row
removes a placement retires its pair as its own consequence. It answers as
if the cell-driven counts settled at the firing's commit. A unit that rebuilds
the population settles them there, and a value write settles them at its install. A
firing with no row, or a full install that does not rebuild the population,
settles them at the tick's publication: a later firing in the same tick that
restores the cell keeps a body this firing judged retired. The decision
judges by its own proposal and no later firing changes that. Custody holds
(`WorldPopulation.Holds`) are read as they stand; no firing arm changes them.
The forecast reads no queued arm's declared sources: document rows carry
literal identities. So the late read of arm sources (see "Differs from the
design" under step 7) does not reach this path.

### Aggregation to the step

A `FiringDecision` of `Faulted` becomes `RuleOutcome`'s `Faulted` for the
evaluation, and it must reach every aggregate that decides something:

- `RuleEvaluator.Evaluate(rules, …)` returns `RuleOutcome` for the family
  (`Faulted` if any evaluation faulted, else `Refused` if any refused, else
  `Fired` if any fired, else `Idle`) and `applied` beside it; the
  `bool`-returning overloads (`RuleEvaluator.cs:324-356`) are deleted.
- `IArenaSearchJudge.Judge` returns `JudgeOutcome { Applied; Faulted }`;
  `PushScope` and the candidate replay (`ArenaSearch.Candidates.cs:35`,
  `:138`) carry it into the job; `Score` returns `Outcome`; `StepJob` folds
  both into the step's verdict and restarts the step on `Faulted`
  (`ArenaSearch.cs:562-569` does this on the register today) before any
  answer is adopted. A judge whose first rule writes a win marker and whose
  later rule faults is a faulted candidate, not a scored one.
- A staged group advances on `Fired`, skips a `Refused` step under
  `RuleGroupStepPolicy.Skip`, and holds its cursor on `Faulted`
  (`RuleEvaluator.Groups.cs:243` reads the two-case outcome today). A
  fixpoint pass whose member faulted counts no progress for that pass and
  leaves the pass count where it was; the group's trigger is one `Verdict`,
  `Faulted` leaving progress untouched (`Armed`, `:86-108`). Group progress
  is a journal participant, so a member's faulted firing rewinds whatever
  progress the member's own scope wrote.
- `WorldRuleHost.EvaluateDecisionBinding` and `EvaluateInteraction` return
  the same `RuleOutcome`.

### Lifecycle writers

Four writers of latch and decision state are not firings and stay:
`RuleLatch.EndSweep` removal (`RuleLatch.cs:244-258`), `RuleLatch.Restore`
(`:317-321`), the decision store's `Restore` (`WorldRuleHost.Decisions.cs:541-569`)
and the scoped-replay restore (`WorldScopedReplayRuntime.Rules.cs:74-81`).
They run with no scope open: the sweep's end after `EvaluateRule`'s loop has
committed every proposal (`RuleEvaluator.cs:486`), the restores between
ticks. The participant's journal write refuses when a scope is open at those
moments (runtime refusal), which is the "no outstanding proposal" rule made
mechanical: a restore inside a firing throws. The `Reset` the search judge
performs per candidate (`RuleArenaSearchJudge.cs:65`) runs inside the search's
scope and is journaled like any write, so a rewound candidate's latch state
comes back.

### Serial ownership

`Propose` writes the shared arena journal and reads shared binding scratch
(`m_boundLeft`/`m_boundRight`, `Locals`, `InstanceBindings`, the pattern word,
the host's preflight scratch at `WorldRuleHost.Arms.cs:44-50`). The design
requires what holds today and states it: one proposal in flight per arena at a
time, on the tick thread under the authority gate (`WorldTick.Step.cs:74`),
and no proposal straddling snapshot publication. The host's preflight scratch
moves into the proposal (it is per firing already by intent, `:41-43`). A
parallel tick is the job-system memo's design
([the memo](engine-job-system.md)); it owes a partition-owned frame, journal
and scratch per worker and an ordered merge, and this plan hands it a
proposal that is already a value with one writer, which is the shape a
partition needs.

### Pricing

The work sheet charges a `StateCellOperand` one unit whatever its key
expression (`src/Puck.State.Rules/Compiler/Operands.cs:51`; `RuleWorkBudget.ExpressionCost` charges an
operand through `operand.Cost`, `RuleWorkBudget.cs:95-96`), so a computed key
such as `cards[sum(values, x -> x)]` is undercharged today. A history age's
expression is charged inside `HistoryOperand.Cost` (`src/Puck.State.Rules/Compiler/Operands.cs:405-412`),
and a tuple pattern's token expression inside `PatternOperand.Cost`
(`Operands.Patterns.cs:411-417`). After lowering, a computed key and a ring age
are tokens of the enclosing program and `ExpressionCost` prices them once, and
an operand that is a source of its own prices the same programs beside its own
cost (`CompiledValueSource.Cost`); `HistoryOperand` no longer charges its age.
`PatternOperand` still charges its per-token program, which is the fold it runs
rather than tokens of the enclosing program, once. The price of a computed key **rises** to its real work, which
is the correction, and a document near its limit may stop admitting with
unchanged source; that document was over its limit. Ergonomic-is-optimal is
checked by spelling equivalence (`let k = …; cards[k]` and `cards[…]` compile
to the same tokens and the same units), not by "unchanged or lower".

### Cost

The rule VM's per-tick budget is the constraint; the claims below are
estimates until step 3's measurements, and the benchmark that settles them is
specified there.

- **Returning `Outcome` instead of `bool` plus two `out` parameters.** `Run`
  is called once per program plus once per `Call`, fold member,
  `Not`-with-body and `And`/`Or` right operand (`RuleExpressions.cs:177`,
  `:191`, `:248`, `:502`), never per arithmetic token. The JIT's return
  convention for a 16-byte struct is observed, not inferred: the
  `DisassemblyDiagnoser` on `StateExpressionKernels`
  (`src/Puck.Cli.Bench/StateKernelBenchmarks.cs:223`) shows it.
- **The stack mark** widens from `bool` to a byte of the same width; the
  branch on it gains one case. Not zero; small.
- **Addresses as program.** A computed key is tokens of the enclosing program,
  so it runs no nested `Run` frame of its own, no second pair of `Rent` leases
  and no second `stackalloc` (`RuleExpressions.Run`); a bound-key or cell-pointer indirection
  (`RuleReads.cs:281-371`) runs no VM and gains nothing. The saving is per
  *computed* key read, not per dynamic key.
- **Journal participants.** One 24-byte entry per latch bit, decision state
  or group progress write (`ArenaJournal.EntryBytes`), in a scope that
  already journals the firing's cells; the decision's previous state is one
  struct copy into the store's undo slot.
- **Arms prepared once** replace two evaluations (preflight and delivery)
  with one plus a generation compare.

### Where the whole design stops

- Disclosure is outside it. Round 1's report also holds eight disclosure
  findings; an outcome, a frame and a journal impose no policy on what a
  reader may see, and [the disclosure plan](disclosure.md) owns them.
- A throw after the arena commit is a defect that leaves the authority
  inconsistent; the design names the one place it can happen and makes the
  input resynchronisation unable to refuse there, and claims nothing more.
- A detached consumer's `Faulted` arm is reviewed code, listed above.
- An operand's hand-written arena read under a literal key, and a wrong finite
  answer from a correct evaluation, are logic defects, not fault paths.
- Determinism holds as a property (rule 4): the fault paths are deterministic,
  and the serial-ownership precondition is what today's tick provides.

## Research weighed

| Source | What it supports here |
|---|---|
| IEEE 754-2008, clause 7 (status flags are raised and stay raised until explicitly cleared) | The register is the status-flag protocol, and its documented hazard, an intermediate computation clearing or failing to raise a flag the final consumer tests, is round 3's first finding. |
| LLVM Language Reference, "Poison Values" (poison propagates through dependent instructions; `freeze` is the one instruction that stops it) | The absorbing in-band value with named escape hatches: `Faulted` propagates with no escape; `Absent` propagates with `isAbsent` and `??`. |
| Rust `Result<T, E>` with `#[must_use]` (a dropped `Result` is a compiler diagnostic) | The outer fence is a diagnostic over the source, not a runtime throw. Puck's is `OutcomeAnalyzer`, a build error in every project. Rust's is also not a proof: `let _ = result;` is legal there too, which is why this plan names what the analyzer does not catch. |
| Alexis King, "Parse, don't validate" (2019) | The inner fence: no `Value` accessor; obtaining a number requires the finite case. The review's point that `TryFinite(out n)` with an ignored `bool` defeats it is correct, and the analyzer's `OUTCOME002` is the answer the type cannot give. |
| ISO SQL three-valued logic over `NULL` and C. J. Date's critique | The semantics to refuse for faults: Kleene logic lets a disjunction recover an unknown; `Faulted` absorbs. Absence, by contrast, keeps its own conversion rules, which are not Kleene either (an absent `not` is true, an absent arithmetic is absent). |
| Jim Gray, "The Transaction Concept: Virtues and Limitations" (1981) | Atomicity is of the whole unit. A firing that journals its cells but writes its latch and choice in place is not atomic under any nesting; one journal for every participant is. |
| Puck itself: the arena journal (`StateArena.cs:569-594`, `:654-681`) and the one-write-door convergence in [S10 step 3](state-and-language.md#s10--the-state-review) | The Puck-native combination: the transaction exists and nests; the design enlists the remaining stores in it rather than adding a second mechanism beside it. |

## Every finding, one by one

Each row says what closes the finding and by which of the three
enforcements; "structure" is claimed only where the invalid state has no
representation. The witnesses are `test` blocks of
`worlds/debug/rule-faults-tests.puck`, except W13 (a flock needs the population's counters, which a test block cannot
read: `FlockLawTests.AFaultingAffinityIsACountedFailureThatWeighsNothing`) and L1 (a nested-scope law,
`LatchAndTriggerLawTests.ARewoundScopeRestoresTheEdgeCrossingAChildFiringConsumed`).

| Round | Finding | What closes it | Enforcement | Witness |
|---|---|---|---|---|
| 1 (disc2 §9) | A faulting remove key was treated as success through a `ResolveKey` overload that discarded `KeyNaming`. | The remove's address is resolved by the interpreter into `Resolved<CellKey>`; a `Faulted` resolution makes the step `Faulted` and the door rewinds the earlier `score` write with it. | Structure for the resolution (no overload without the case); one door for the rewind; analyzer for an ignored `TryNamed`. | W1 |
| 1 (disc2 §10) | Vector, board-origin, symmetry and pattern reads answered a number for a faulted key. | Operands receive `ResolvedKey` (`Named`/`Nothing`); the interpreter already returned `Faulted`. | Structure for the declared addresses; analyzer `OUTCOME004` against an operand keeping an address of its own; text routing lands with the gate in step 3. | W2 |
| 1 (statecorr G1) | Faults in a dynamic key or ring age became absence then truth under `not`. | The key and age expressions are tokens of the enclosing program. | Structure: no second evaluator exists. | W3 |
| 1 (statecorr G2) | Closing on a fault re-armed an Edge latch, cleared an interrupt, fired `onNoChoice`. | Latch bits and decision state are journaled; a `Faulted` decision rewinds them with the scope. | Structure (journal) plus one door (`FiringDecision` has no faulted-and-fires case). | W4 |
| 2 (t1rf B1) | Dynamic cue keys skipped resolution because the arm tested `effect.Key != default`. | Arms declare addresses and receive resolved ones; a declared `KeyFrom` with no literal key is an address. | One door (the evaluator resolves; arms cannot) plus review of the declaration being complete. A valid dynamic key keeps its body (W5 checks both arms). | W5 |
| 2 (t1rf B2) | Pattern token expressions substituted `0L` for a faulting letter. | The token expression is a `Word` fold body; a faulting body makes the fold `Faulted`. | Structure for the fold; the `world.match` read-back uses the same fold detached. | W6 |
| 2 (t1rf B3) | An absent left expression short-circuited the right read. | Both conjunct sources are whole programs evaluated before combination; an absent program returns from its own loop only. | One door (`Decide`); lands in step 3. | W7 |
| 2 (t1rf low) | A faulting read synthesised Edge transitions and decision no-choice effects. | Same as G2. | Same as G2. | W4 |
| 3 (rf3 §1) | A nested success set the shared out-parameter to `None`. | `Run` returns an `Outcome`; nested calls return their own. | Structure: the variable is gone. | W8 |
| 3 (rf3 §2) | A document commit swapped the arena and a delivery's raise threw. | No register; delivery evaluates nothing (prepared arms). | Structure for the register; one door for preparation. Target validity after the install is the generation check (runtime refusal). | W9 |
| 3 (rf3 §3) | An effect fault consumed an Edge crossing. | The latch write is journaled in the firing's scope and rewinds with it at any nesting depth. | Structure (journal). | W10, L1 |
| 3 (rf3 §4) | A decision installed its choice before its effects faulted. | The decision's next state (selected, candidate, generation, timers, random state, draw count, reconsiderations, last score, interrupt edge) is written through the journal and its common and option effects fire in the same scope. | Structure (journal) plus one door. | W11 |
| open items `:3148` | A search score and a flock affinity read a failed expression as 0. | `JudgeOutcome` and `Outcome` force the choice: a faulted candidate restarts the step; a non-finite affinity is a counted failure. | One door for the search step; listed detached arm for the affinity. | W12, W13 |

## Implementation steps

Step 9 remains (steps 1 to 8 have landed. Step 8, the outcomes analyzer: `OutcomeAnalyzer` in `Puck.Analyzers` holds `OUTCOME001` to `OUTCOME004` as build errors in every non-test project, from the compiler's symbols rather than a `puck scan` rule, so a new caller cannot avoid it and no landing-bar step has to remember it; its fixtures are `OutcomeAnalyzerTests`, and `OutcomeDiscardAllowlist` and `OutcomeDetachedAllowlist` carry the listed exceptions with their reasons. `OUTCOME004` refuses an operand member that resolves an address against the reader, not an operand that holds its declared `CompiledCellRef`/`LiveRow`, which every operand does to build its read set and `Addresses`. Step 1, the representation: `Outcome`, `Fact`, `Resolved<T>`, `ResolvedKey`, `Verdict`, `FiringDecision` and `RuleOutcome.Faulted` are in `Puck.State`, `Fact` and `Resolved<T>` carry every operand read and key resolution, and the witnesses exist. Step 2, addresses as program: the lowering above, `IRuleOperand.Addresses` and `Read(reader, keys)`, and the pricing below. Step 3, the interpreter and the gate: `Run` answers an `Outcome`; `RuleExpressions.Evaluate(ref EvaluationFrame, ...)` and `EvaluateDetached` are the entries; `RuleEvaluation.Decide` answers a `Verdict`, folding the text conjuncts in, with `RuleEvaluation.ReadSource` beside it. Step 4, verdicts at every deciding place: the register is gone, and a rule's judgement, a group trigger and its fixpoint pass, a decision, an interaction and a search step each read the `Outcome`, `Verdict`, `BindingsResult`, `RuleOutcome` or `JudgeOutcome` they were handed; every evaluator-side effect answers a `Step` of `Faulted`, `Refused` or `Done`. Step 5, journal participants: `IJournalParticipant` and the journal's participant records, which `StateArena.Rewind` dispatches; `RuleLatch` and a decision binding write through them, and a firing closes only through `RuleEvaluator.Commit`, switching on `FiringDecision`. Step 6, arms read once: an arm declares `IRuleEffect.Sources` (a key as an index, an expression program, an operand), the evaluator reads them with the firing and hands the values through `EffectFiring.Values`, the same at the preflight and at the delivery; a source that faults is a faulted firing; `IRuleKey.ResolveForWrite` answers a typed `WriteKey`; the input resynchronisation is `TryPrepareBodies`/`TryPrepareAll` before the arena commit and `ApplyPreparedBodies`/`ApplyPreparedAll` after it. Step 6b, from the step 5 review: a decision's common and option effects are one firing under one scope with the binding, a group rewind writes its edge bit only when it landed, every latch lifecycle writer takes the journal and refuses a scope, and a pruned rule gives its ordinal back. Step 7, the decision's remaining faults: a decision reads every gate, option gate and score before it writes its binding, so a fault while gathering leaves the binding untouched, interrupt edge included; its no-choice effects are one firing with the binding they clear (W11's no-choice cases); and the liveness step between the common and option effects reads the population the commit installs, as [Prepared arms and their targets](#prepared-arms-and-their-targets) describes. Not built: arms carrying the population generation of a body target, and the host scratch inside the evaluator's proposal. Differs from the design: a queued arm's declared sources are read in the preflight loop (`RuleEvaluator.Effects.cs`, the loop over the firing's queue), after every reversible effect of the firing has run, where the design reads each source just before its arm, so `emitCue(key: x); x = 5` reads 5; choose which the contract is and make the other match. Unmeasured: `WorldRuleHost.ProposedPairLive` materializes the arena when the common effects queued a document row and the option has a neighbour candidate, and every option with a candidate walks the placements once for the cell-driven forecast (the scratch slot table is copied only when the forecast retires or admits a body); an effects-heavy headless run's allocations and decision work decide whether that matters). Each is a vertically coherent slice: one producer and every consumer
it affects land together, every bridge is named with the step that removes
it, and the check proves what the step establishes. Each step builds what it
touches (`dotnet build <project> -c Release -m:2 -nodeReuse:false`) and lands
on a working integration branch. The witness file is
`worlds/debug/rule-faults-tests.puck`, run with
`puck test worlds/debug/rule-faults-tests.puck --jobs 1`; its header states
each block's expected verdict, and every block is green. W13 and L1 are C# laws
(`FlockLawTests.AFaultingAffinityIsACountedFailureThatWeighsNothing`,
`LatchAndTriggerLawTests.ARewoundScopeRestoresTheEdgeCrossingAChildFiringConsumed`),
since a test block cannot read the counters they need. A name check uses a
word-boundary form, which `puck search` spells
`(^|[^A-Za-z])(Name)([^A-Za-z]|$)` (it rejects `\b`).

9. **Documents.** `expressions.md:305-320`, `rules.md:224-276` and
   `:441-499`, `patterns.md:102-110`, `hosting.md`, the state README and the
   `puck-world` skill's rule paragraph describe the outcome, the absence
   rules, the one interpreter, the journaled firing and the install boundary;
   the register's paragraphs go. Check: `puck docs citations` passes, and the
   W1 to W13 blocks are cited from the reference as the behaviour's examples.

**Benchmarks.** The `Indirect` kernel reads through a cell pointer
(`StateKernelFixture.cs`) and never ran the nested VM step 2 removed, so it
cannot show the saving; the `ComputedKey` kernel reads through a program that
computes the key, its tokens standing in front of the read. Step 3
measures `StateExpressionKernels` on `Literal`, `Direct` and `ComputedKey` for
`DependentChain` and `IndependentEvaluations` at the token breakpoints, with
successful, absent, faulted and short-circuited variants, allocations and the
disassembly's return convention recorded, on Windows and Linux. Step 5
measures an effects-and-decision-heavy headless debug run's `host.frames`
simulate time with firings that succeed, refuse and fault. No step lands
slower than the tip on `DependentChain`/`Literal`.

Determinism (AGENTS.md rule 4): every previously lost fault now closes a gate,
holds a latch or refuses a firing that used to proceed, so a recorded replay
or hash that reached one of those paths moves; each step re-records what it
invalidates.
