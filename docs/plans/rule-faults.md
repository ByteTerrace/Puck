# Rule faults: one outcome, one interpreter, one commit

A design review of rule-fault propagation after four review rounds, and the
design that closes the class by construction. The contract it serves is in
[the register](../decisions/state-and-language.md#expression-semantics): a
fault in an evaluated gate conjunct closes the whole gate, under negation and
disjunction too; a fault is not a false, so anything that remembers a verdict
keeps the last one it read successfully; an effect that faults refuses the
whole firing. Read-only review: every claim below cites the code it rests on at
the integration tip this lane merged (`lead/fundamentals`).

## The verdict on the approach

The fault register on `ArenaScratch` (`src/Puck.State/Arena/ArenaScratch.cs:24-80`)
is the right *contract* (one place a fault lands, read by the one caller that
decides) built on the wrong *mechanism*: an out-of-band sticky flag beside the
value, on an object that is not the evaluation. It cannot be closed, for four
structural reasons, each of which the rounds found one instance of:

1. **Two channels that must agree by convention.** `RuleExpressions.Run`
   reports failure through a `bool` return plus `out ExpressionFault fault`
   (`src/Puck.State.Rules/RuleExpressions.cs:90-91`), and `TryEvaluate` raises
   into the register only when that out-parameter says `Domain` or `Forever`
   (`:44-45`). Every failure exit must leave the right value in `fault`. A
   nested `Run` or `TryFold` is handed the caller's own `fault` variable
   (`:215`, `:250`, `:504`); a nested success writes `None` into it; a later
   arithmetic failure returns `false` without reassigning (`:437`, `:445`,
   `:548`). Round 3's first finding is exactly this: the chokepoint depends on
   about twenty failure exits each assigning the right out-parameter. A flag
   beside a value is the IEEE 754 status-flag protocol, whose known hazard is
   precisely that an intermediate computation clears or fails to set the flag.
2. **The register lives on a replaceable object.** `IStateReader.Scratch`
   resolves `Arena.Scratch` (`src/Puck.State/IStateReader.cs:68`);
   `WorldRuleHost.Arena` resolves `m_host.Arena` (`src/Puck.World.Server/WorldRuleHost.cs:143`),
   which `AdoptPreparedArena` replaces (`src/Puck.World.Server/WorldServer.Arena.cs:223-224`)
   from `CommitTransactional` (`src/Puck.World.Server/WorldRuleHost.Arms.cs:119-128`)
   in the middle of `FireEffects`, which captured `scratch` once
   (`src/Puck.State.Rules/Evaluation/RuleEvaluator.Effects.cs:70`) and opens
   its delivery scopes on the captured object (`:140`) while every raise
   resolves the new one. Fault state is per evaluation; the arena is per
   document. The round-3 throw is the register's lifetime, not an API misuse.
3. **Deciding places must remember to read it.** `RuleEvaluator.cs:575` and
   `:636`, `WorldRuleHost.Decisions.cs:214` and `:257`, `RuleEvaluation.cs:133`
   and `:179` each poll `RaisedFault`; `GateOpen(faulted: out _)` discards its
   fault at `WorldRuleHost.Decisions.cs:190`, `:195`,
   `WorldRuleHost.DecisionNeighbors.cs:98`, `:324` and `RuleEvaluator.Groups.cs:100`
   on the strength of an enclosing scope. "Every caller remembers" moved up
   one level; it did not go away.
4. **A read-side register cannot order writes.** The Edge latch is written
   before the firing (`RuleEvaluator.cs:585` then `:613`), a decision's choice,
   timers and candidate before its effects (`WorldRuleHost.Decisions.cs:320-334`
   then `:342`, `:366`), and an arm is evaluated twice, once in preflight on
   the old arena and again in delivery on the new one
   (`RuleEvaluator.Effects.cs:82`, `:145`; `WorldRuleHost.Arms.cs:263-277`).
   Round 3's last three findings are all "decided state installed before the
   verdict was in". No fault channel fixes that; only a firing whose every
   state transition waits for one verdict does.

So the approach is fundamentally not closed, and the fix is not a fifth
patching round. Three things change, each removing a whole class rather than an
instance: the fault travels **in the value**, so no out-parameter or register
can disagree with it; **one interpreter** produces every evaluated value and
operands cannot read state except through the addresses it resolved, so no
operand or host can turn a faulted address into a number; and a firing is
**transactional over everything it decides**, so the latch, the choice, the
arena scope and the arms commit through one door after one verdict, or not at
all.

## The design

### The invariant

> Every evaluation of authored text yields an `Outcome` whose fault is a case
> of the value, not a flag beside it; the only code that produces an `Outcome`
> from tokens is `RuleExpressions.Evaluate`; the only code that turns a set of
> outcomes into installed simulation state is `RuleEvaluator.Commit`, which
> installs nothing when any outcome in the firing faulted.

What enforces each clause:

| Clause | Enforcement |
|---|---|
| A fault is a case of the value | The shape of `Outcome`, `Fact`, `Resolved<T>` and `Verdict`: no `Value` property; the number is reachable only through `TryFinite(out long)`, which answers `false` for `Absent`, `Forever` and `Faulted`. There is no API that reads a faulted value as a number, so the compiler refuses the code that would. |
| No nested evaluation can erase a fault | `Run` returns its `Outcome`; a nested call's outcome is a local the caller combines. There is no shared `out` variable. The combining rule is total and absorbing (below), so a fault in any operand is the result. |
| Only one interpreter evaluates tokens | `RuleExpressions.Evaluate(ref EvaluationFrame frame, …)` takes a `ref struct` frame only `RuleEvaluator` constructs. Host arms, operands and world code hold no frame, so they cannot call it; the compiler refuses the call. The compiled form inlines every dynamic key, live row index and ring age into the program it addresses, so those expressions have no evaluator but the one loop. |
| Operands cannot read through a faulted address | The interpreter resolves an operand's declared addresses before calling it and passes `ReadOnlySpan<ResolvedKey>` whose cases are `Named(key)` and `Nothing`; `Faulted` never reaches an operand because the interpreter already returned `Outcome.Faulted` for the token. |
| One commit door | `RuleLatch.Held`, `DecisionBinding`, the arena scope and the arm queue are written only by `RuleEvaluator.Commit(in Proposal)`; the proposal is produced by the read phase and carries a `Verdict`. The stores' mutators are `internal` to the evaluator's commit path. A proposal whose verdict is `Faulted` has no `Fires`/`Idle` case to install, so the install code does not type-check against it. |
| Nothing discards an outcome | A `puck scan --only outcomes` analyzer (`src/Puck.Cli.Source/Scan/Analyzers/`, beside `LockAnalyzer`) refuses an expression statement or `_ =` discard whose type is `Outcome`, `Fact`, `Resolved<T>`, `Verdict` or `RuleOutcome`. This is the outer fence, the same kind of check as `IVT001` and `PUCKARCH00n` in `build/PuckBuildTasks.cs`; the inner fences above make the hot paths unable to need it. |

Where the guarantee stops is stated at the end of this section.

### The shapes

```csharp
public enum FaultCase : byte { Finite, Absent, Forever, Faulted }

// What one evaluation answered. 16 bytes; no Value accessor.
public readonly record struct Outcome(long Raw, FaultCase Case, ExpressionFault Fault) {
    public static Outcome Finite(long raw);
    public static Outcome Absent;                       // a value, answered by isAbsent and ??
    public static Outcome Forever;                      // a value a comparison reads as +inf; arithmetic faults on it
    public static Outcome Faulted(ExpressionFault why); // Domain only, today
    public bool TryFinite(out long raw);
    public bool IsFaulted => Case == FaultCase.Faulted;
}

// What one operand read. Fact is Outcome plus the encoding; today's RuleFact with its two bools folded into Case.
public readonly record struct Fact(Outcome Outcome, CellKind Kind);

// What an address named. Replaces KeyNaming and every (CellKey, out KeyNaming) pair.
public readonly record struct Resolved<T>(T Value, FaultCase Case) { public bool TryNamed(out T value); }

// What a gate, a rule, a decision or a group trigger decided. Replaces every (bool, out bool faulted) pair.
public enum Verdict : byte { Closed, Open, Faulted }
```

`ExpressionFault.Absent` leaves the enum: absence is a `FaultCase`, never a
fault, which the register's own remarks already say
(`src/Puck.State/ExpressionFault.cs:6-8`). `KeyNaming`, `RuleFact.IsAbsent`,
`RuleFact.IsFaulted`, `ArenaScratch.RaiseFault`, `EnterFaultScope`,
`LeaveFaultScope`, `RaisedFault`, `FaultScope`, `TryEvaluateAlone` and the
`out ExpressionFault` parameters are deleted with their callers.

### One interpreter

`RuleExpressions.Evaluate(ref EvaluationFrame frame, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind) : Outcome`
is the one loop. Inside it:

- The value stack keeps today's shape (`long[]` plus a mark per slot,
  `RuleExpressions.cs:97-103`), with the `bool absent` mark widened to a
  `FaultCase` byte. `Faulted` never lives on the stack: the token that yields
  it returns `Outcome.Faulted` from the loop at once, which is what every
  failure exit does today (`:155`, `:437`). The change is only in what returns:
  an `Outcome` the caller cannot misassign.
- The combining rule is strict and absorbing. Any operation consuming a
  `Faulted` operand is `Faulted`; `Not`, `And`, `Or`, `Select`, `Coalesce` and
  the folds included. `Absent` is absorbed only by `IsAbsent` and `Coalesce`;
  every other consumer turns it into `Faulted(Domain)` as today
  (`:240-243`, `:271-276`). An operand a `ShortCircuit` skips is not evaluated,
  so it neither reads nor faults, which keeps `expressions.md:205-208`. This is
  deliberately not Kleene's three-valued logic (SQL's `TRUE OR NULL = TRUE`):
  round 1's G1 and round 2's third finding are both an `or` or a `not`
  recovering a fault, and the contract forbids it.
- **Addresses are program.** The compiler lowers `cards[expr]` to
  `[expr…, Operand(cards, keyFromStack)]`, `hist[age]` to
  `[age…, HistoryOperand(popAge)]`, `$zones[idx]` to `[idx…, Operand(rowFromStack)]`,
  and a pattern's `value:` token expression to a `Word` fold whose body runs
  once per token with `$token`/`$previous` bound and whose accumulator is the
  pattern word. `EffectKeyFact.ResolveIndex` (`ZoneTable.cs:302-318`),
  `HistoryOperand.Read`'s nested `TryEvaluate` (`Operands.cs:421-435`) and
  `PatternOperand.ReadTupleWord` (`Operands.Patterns.cs:306-351`) are deleted:
  the expression they ran is now tokens of the enclosing program, evaluated
  by the one loop, and its poison propagates on the stack. A key or age that
  faults never reaches the operand; the operand token is never executed.
- **Operands read only through resolved addresses.** `IRuleOperand` declares
  its addresses (`ReadOnlySpan<CompiledCellRef?> Addresses`, with `RowFrom` an
  address too) and reads through `Read(IStateReader reader, ReadOnlySpan<ResolvedKey> keys) : Fact`.
  The interpreter resolves every address first; a `Faulted` resolution returns
  `Outcome.Faulted` for the token and the operand is not called. `ResolvedKey`
  has the cases `Named(key)` and `Nothing` only, so the per-operand meaning of
  "nothing" (a board origin of -1 at `Operands.cs:554`, a start position of 0
  at `Operands.Patterns.cs:361-363`, `$previous` on a first token reading the
  unheld cell at `RuleReads.cs:282-296`) is kept where it is authored, while
  "fault" has no representation there. The same holds for `CompiledVector`
  (`Operands.Vectors.cs:79-107`), `LiveRow`, text reads
  (`RuleEvaluation.cs:287-336`) and effect destinations.
- **Host facets answer facts.** `OperandFact<TFacet>.Read` already returns a
  `RuleFact` (`src/Puck.State/OperandFact.cs:37`); it returns a `Fact`, so a
  world fact that cannot answer (`Forever`, an absent body) is in-band too.
  Host facets never evaluate programs: `WorldFactOperand` reads values
  (`src/Puck.World.Schema/WorldFactOperands.cs:29-37`), and the four host
  sites that evaluate a program today (`WorldRuleHost.Arms.cs:547`, `:657`,
  `:724`; `WorldRuleHost.DecisionNeighbors.cs:331`) become declared
  `CompiledValueSource`s the evaluator reads in the firing's read phase; the
  arm receives a `long`.
- **The capability frame.** `EvaluationFrame` is a `ref struct` holding the
  reader, the bound keys and the scratch lease; `RuleEvaluator` constructs it
  at the start of an evaluation and nothing else can. The constant folder
  (`RuleCompiler.Expressions.cs:138`) and the two read-back consumers
  (`WorldRuleHost.FlockAffinities.cs:14`, `src/Puck.World.Browser/Engine/BrowserSession.cs:716`)
  use `RuleExpressions.EvaluateDetached(...)`, a second entry to the same loop
  that returns the same `Outcome` and is what a console read, a perception
  sample or a fold on literals uses: it decides for itself what the outcome
  means, and it installs no simulation state.

### The gate

`RuleEvaluation.GateHolds` becomes `RuleEvaluation.Decide(ref frame, GateToken[] gate) : Verdict`.
A conjunct's two sources are evaluated by the one loop (`TryReadSource` goes
away: a literal, an operand and an expression are all `Outcome`s). A `Faulted`
source makes the gate `Faulted` and the fold returns at once, ahead of `Not`,
`All`, `Any` and the next conjunct, which is what `RuleEvaluation.cs:178-181`
does by polling today. Both sides of a comparison are always tokens evaluated
before the compare, so an absent side can never hide a faulting side (round
2's third finding) without the compiler having emitted a `ShortCircuit`, and
a `ShortCircuit` skip is the documented semantics. A text conjunct differs only
in its comparator: its two addresses go through the same resolution, and a
faulted address is a `Faulted` verdict; the separate text branch with its own
register poll (`RuleEvaluation.cs:123-136`) is deleted.

A `Verdict` is a three-case enum, so a caller cannot read it as a `bool`; it
has to write the `Faulted` arm, and the only thing that arm can do with a
verdict inside a firing is put it in the proposal.

### The transactional firing

One evaluation of any rule kind (an ordinary rule, an iterated binding, a pool
binding, an interaction, a decision binding, a group trigger, a search judge
step) is two phases with one door between them.

**Read phase** (`RuleEvaluator.Propose`): opens the arena scope
(`BeginStateTransaction`, `IEffectHost.cs:277`), evaluates locals, selects
live rows, decides the gate, and when it fires runs the effect sequence into
the open scope exactly as `FireSequence` does today, with each effect's
sources evaluated by the interpreter just before it (so a later effect still
reads what an earlier one wrote in the scope, `rules.md:284`). Arms are
**prepared once**: an arm's `Prepare(ref frame) : Resolved<PreparedArm>`
evaluates its addresses and values and stores the resolved body index, cue
payload, pose cell or document row into the queue; preflight checks the
prepared form. The phase produces a `Proposal`:

```csharp
ref struct Proposal {
    Verdict Verdict;             // Closed, Open, Faulted
    bool Fires;                  // Open and (Level or a new Edge crossing)
    int ArenaMark;               // the open scope
    Range Arms;                  // prepared arms queued by this evaluation
    LatchEntry Latch;            // the entry this evaluation would install (binding, held)
    DecisionBinding? Decision;   // the decision state this evaluation would install
    ExpressionFault Fault;       // when Faulted
}
```

Nothing durable is written in this phase: the latch dictionary, the decision
binding, the interrupt edge, the schedule caches and every store outside the
journaled arena are read into the proposal and left as they were.

**Commit phase** (`RuleEvaluator.Commit(ref Proposal)`), the one door:

- `Faulted`: rewind the scope, discard the prepared arms, install nothing,
  report one `Arithmetic` refusal naming the fault, and touch the latch
  binding so the sweep keeps its entry (`HoldOnFault`'s job today,
  `RuleEvaluator.cs:635-642`). The latch keeps its last verdict, the decision
  its choice and interrupt edge, the group its progress.
- `Closed` or `Open` without firing: commit the (empty) scope, install the
  latch entry and the decision state, capture the scheduler's versions.
- `Open` and fires: preflight the prepared arms as a sequence, prepare the
  transactional unit, `PrepareStateCommit`, then commit the scope, install the
  latch entry and the decision state, install the unit, deliver the prepared
  arms. A refusal anywhere before the commit is `Refused`: rewind, discard,
  install nothing, as `FireEffects` does today (`RuleEvaluator.Effects.cs:111-117`).

A delivery evaluates nothing (it delivers the prepared form), so a document
install that replaces the arena between the commit and the delivery
(`WorldRuleHost.Arms.cs:119-128`) changes no value a delivery reads; round 3's
second finding has no evaluation left to go wrong, with or without a register.

A decision binding is one such evaluation: locals, interrupt gate, gate,
option gates and scores in the read phase, the winner computed into
`Proposal.Decision`, the common effects and the chosen option's effects fired
into the same open scope, and the whole installed by `Commit`. The two
separately scoped `FireDecisionEffects` calls (`WorldRuleHost.Decisions.cs:342`,
`:366`) become one firing, which also closes "a fault in the common effects
does not stop the option effects from committing".

This is the atomicity the firing already promises for arena writes
(`rules.md:441-448`), widened to the simulation state outside the arena that
a firing decides. The Puck-native direction beyond this plan is to move the
latch and decision bindings into the arena as engine rows so the journal
itself rewinds them and checkpoints carry them for free (they are already
hashed and checkpointed as simulation state, `RuleLatch.cs:26`,
`WorldRuleHost.Decisions.cs:465-484`); the proposal-and-commit door is complete
without that move and does not preclude it.

### What goes away

`ArenaScratch`'s register and scopes (`ArenaScratch.cs:19-80`); `KeyNaming`
and every `out KeyNaming` (`CompiledFacts.cs:53-63`); `RuleFact`'s two bools
(`src/Puck.State/RuleFact.cs:22`); `ExpressionFault.Absent`;
`RuleExpressions.TryEvaluate`, `TryEvaluateAlone` and `Run`'s out-parameters;
`RuleEvaluation.GateHolds`, `TryReadSource`, `ReadText`'s register raise;
`RuleEvaluator.GateOpen(out bool faulted)`, `HoldOnFault`, the `RaisedFault`
polls; `EffectKeyFact.ResolveIndex`'s nested evaluation,
`HistoryOperand.AgeExpression`'s nested evaluation, `PatternOperand.ReadTupleWord`;
the second locals evaluator `RuleEvaluator.Groups.TryEvaluateLocals`
(`RuleEvaluator.Groups.cs:284-310`), a duplicate of `EvaluateBindings`
(`RuleEvaluator.cs:694-773`) that the one read phase replaces; the host's four
program evaluations in arms; the decision's pre-effect state mutation. The
search's fault scope (`src/Puck.State.Search/ArenaSearch.cs:525-564`) becomes a
`Verdict` the step returns.

### Where the guarantee stops

- **Detached evaluation decides for itself.** `EvaluateDetached` serves the
  constant folder, console read-backs, the browser session and perception
  samples. Its `Outcome` is in-band and cannot be read as a number without
  matching, but what a detached caller does with `Faulted` is that caller's
  decision, outside the firing door. None of them installs simulation state;
  the analyzer's discard rule still applies to them.
- **A host that throws** is handled as today: `FireEffects`' `catch` rewinds
  the scope (`RuleEvaluator.Effects.cs:89-94`), and the commit door keeps
  that. A throw is not a fault; it is a defect, and it still propagates.
- **Absence keeps today's meaning.** A gate reads an absent comparison as
  false; an effect source that is absent refuses; a local that is absent ends
  the evaluation as `Closed` (the latch entry is left for the sweep, as
  `HoldOnFault`'s remark says). Whether an absent local should instead hold
  the latch is the open item at `open-items.md:3148-3157`, unchanged here.
- **The scheduler's skip** (`RuleEvaluator.cs:534-551`) reuses a `Closed`
  verdict when nothing read has changed; it never caches a `Faulted` one, and
  the commit door captures versions only on `Closed`. A fault tick therefore
  re-evaluates next tick, as it should.
- **Checkpoints** carry the latch and decision bindings as they do; the design
  changes when they are written, not what they hold.
- **The analyzer is a fence, not the floor.** Code that matches an `Outcome`
  and substitutes a number in the `Faulted` arm is still writable in a
  detached caller. Inside the simulation it is not, because the only numeric
  consumers of outcomes are the interpreter's own combining rule and the commit
  door, and neither has such an arm.

## Research weighed

| Source | What it supports here |
|---|---|
| IEEE 754-2008, clause 7 (default exception handling; status flags are raised and stay raised until explicitly cleared) | The register is the status-flag protocol. Its documented failure mode, an intermediate computation clearing or failing to raise a flag the final consumer tests, is round 3's first finding. A flag beside a value needs every producer to cooperate; a case of the value needs none. |
| LLVM Language Reference, "Poison Values" (poison propagates through dependent instructions; `freeze` is the one instruction that stops it) | The absorbing in-band value, with named escape hatches. Puck's analogue: `Faulted` propagates through every operation and has no escape; `Absent` propagates and has two (`isAbsent`, `??`). |
| Rust `Result<T, E>` with `#[must_use]` and `?` (the compiler warns on a dropped `Result`; `?` propagates the error case) | The outer fence: a dropped outcome is a compile-time diagnostic, not a runtime throw. Puck's `puck scan --only outcomes` is the same rule over C#. |
| Alexis King, "Parse, don't validate" (2019) | The inner fence: put the proof in the type. `Outcome` has no `Value`; obtaining a number requires matching `Finite`, so the invalid read is unrepresentable rather than checked. |
| ISO SQL three-valued logic over `NULL` (`TRUE OR NULL` is `TRUE`; `NOT NULL` is `NULL`) and C. J. Date's critique of it | The semantics to refuse. Kleene logic lets a disjunction or a negation recover an unknown; the contract (and rounds 1 and 2) require `Faulted` to absorb, which is strictness, not Kleene. |
| Jim Gray, "The Transaction Concept: Virtues and Limitations" (1981) | Atomicity is of the whole unit, or it is not atomicity. A firing that journals its arena writes but installs its latch and choice outside the journal is not atomic, which rounds 2 and 3 observed as the consumed Edge crossing and the half-installed decision. |
| Puck itself: the arena journal scope (`StateArena.BeginScope`/`Commit`/`Rewind`, `src/Puck.State/Arena/StateArena.cs:568-593`, `:653-680`) and the "one write door" convergence in [the state plan](state-and-language.md#s1--the-projection-law-and-the-one-door) | The Puck-native combination beats the borrowed one: the transaction already exists; the design widens what passes through its door rather than adding a second mechanism. |

## Every finding, one by one

Each row says why the finding is impossible under the design (not fixed at
its site) and names the fault-injection witness in the witness file below.

| Round | Finding | Why it cannot recur | Witness |
|---|---|---|---|
| 1 (disc2 §9) | A faulting remove key was treated as success through a `ResolveKey` overload that discarded `KeyNaming` (`RuleEvaluator.Effects.cs:723` then). | There is no key without matching `Resolved<CellKey>.TryNamed`; a `Faulted` resolution yields no key to remove with, and the effect's step returns `Faulted` into the proposal, so the firing's earlier `score` write rewinds at the door. | W1 |
| 1 (disc2 §10) | Vector, board-origin, symmetry and pattern reads erased key faults and answered a number (`Operands.Vectors.cs:87`, `Operands.cs:534-554`, `:660`, `Operands.Patterns.cs:88`, `:212`). | Operands receive `ReadOnlySpan<ResolvedKey>` with cases `Named` and `Nothing` only; the interpreter returned `Outcome.Faulted` before the operand ran. An operand has no faulted key to turn into `-1` or `0`. | W2 |
| 1 (statecorr G1) | Arithmetic faults in a dynamic key or ring age became absence then truth under `not` (`ZoneTable.cs:301-310`, `Operands.cs:412-423`). | The key and age expressions are tokens of the enclosing program; their poison is the program's outcome. There is no second evaluator to drop it. | W3 |
| 1 (statecorr G2) | Closing on a fault re-armed an Edge latch, cleared an interrupt, fired `onNoChoice`. | `Proposal.Verdict == Faulted` has no install arm: latch, interrupt edge and decision binding are written only by `Commit` and only on `Closed`/`Open`. | W4 |
| 2 (t1rf B1) | Dynamic cue keys skipped resolution because the arm tested `effect.Key != default` (`WorldRuleHost.Arms.cs:263` then). | Arms do not resolve; the evaluator resolves every declared address in `Prepare`. A declared address with `KeyFrom` and no literal key is still an address, and an undeclared one cannot be read. | W5 |
| 2 (t1rf B2) | Pattern token expressions substituted `0L` for a faulting letter (`Operands.Patterns.cs:315-323`). | The token expression is the body of a `Word` fold run by the interpreter; a faulting body makes the fold, and so the match, `Faulted`. There is no word buffer slot for a letter that did not evaluate. | W6 |
| 2 (t1rf B3) | An absent left expression short-circuited the right read, hiding its faulting key (`RuleEvaluation.cs:139` then). | Both comparison operands are tokens evaluated before the compare; only a compiler-emitted `ShortCircuit` skips evaluation, and it is the authored `and`/`or` semantics. | W7 |
| 2 (t1rf low) | A newly faulting read still synthesised Edge transitions and decision no-choice effects. | Same as G2: no install without a non-faulted verdict. | W4 |
| 3 (rf3 §1) | A successful nested evaluation set the shared out-parameter to `None`, so a later arithmetic failure raised nothing (`RuleExpressions.cs:215`, `:250`, `:437`, `:445`, `:504`, `:548`). | `Run` returns an `Outcome`; nested calls return their own; every failure exit constructs `Outcome.Faulted(Domain)` at the site. No variable exists that a nested success could leave stale. | W8 |
| 3 (rf3 §2) | A document commit swapped the arena, so a delivery's raise reached a register with no open scope and threw (`RuleEvaluator.Effects.cs:70`, `:140`; `WorldRuleHost.Arms.cs:125`). | There is no register, and a delivery evaluates nothing: it delivers the form prepared in the read phase. The population change the finding relies on is read once, before the commit. | W9 |
| 3 (rf3 §3) | An effect fault consumed an Edge crossing because the latch was written before `FireRule` (`RuleEvaluator.cs:585`, `:613`). | The latch entry is in the proposal and installed by `Commit` after the effects' outcomes are in; a `Faulted` or `Refused` firing installs nothing, so the crossing is still pending next tick. | W10 |
| 3 (rf3 §4) | A decision installed its choice, candidate and timers before its effects faulted (`WorldRuleHost.Decisions.cs:320-334`, `:342`, `:366`). | The decision binding is a proposal copy installed by `Commit`; common and option effects are one firing in one scope. A faulted effect leaves `Selected`, `Candidate`, `CommitmentRemaining` and `Reconsiderations` as they were. | W11 |
| open items `:3148` | A search score and a flock affinity read an absent expression as 0. | `Outcome` forces the choice: a `Faulted` or `Absent` score makes the judge step's verdict `Faulted` (the step decides nothing and restarts, as a thrown one does today, `ArenaSearch.cs:522-564`); a flock affinity that is not `Finite` is a counted perception failure as today. Both are detached callers, recorded as such. | W12 |

## Implementation steps

Each step is one lane's brief, builds what it touches
(`dotnet build <project> -c Release -m:2 -nodeReuse:false`), and has a
mechanical check. The witness file is `worlds/debug/rule-faults-tests.puck`,
one `test` block per witness, run with `puck test worlds/debug/rule-faults-tests.puck --jobs 1`;
a witness is written in step 1 against the construction in its finding and is
expected red until the step that closes it lands. The steps are ordered so the
tree builds after each.

1. **Shapes and the witness file.** Add `Outcome`, `Fact`, `Resolved<T>`,
   `Verdict` and `FaultCase` to `Puck.State`; make `RuleFact` a thin alias of
   `Fact` for this step only; write W1 to W12 as `test` blocks. Check: the
   solution builds; `puck test worlds/debug/rule-faults-tests.puck --jobs 1`
   runs every witness and the ones for still-open findings are red (the
   witness proves the escape before the fix).
2. **`Run` returns `Outcome`.** Rewrite `RuleExpressions.Run` to return
   `Outcome`, with every failure exit constructing its own case and nested
   calls combined by value; delete the `out` parameters; `TryEvaluate` and
   `TryEvaluateAlone` become `Evaluate(ref frame, …)` and `EvaluateDetached`.
   Keep the register one more step so the tree builds. Check: W8 green;
   `puck search "out ExpressionFault" src/Puck.State.Rules` returns no line;
   `puck bench kernels --filter '*StateExpressionKernels*'` within noise of the
   recorded tip on `DependentChain` and `Direct`.
3. **Addresses are program.** Compiler lowering of dynamic keys, live row
   indices, ring ages and pattern token expressions into the enclosing program
   (`RuleCompiler.Operands.cs`, `RuleCompiler.Patterns.cs`); `IRuleOperand`
   gains `Addresses` and `Read(reader, ReadOnlySpan<ResolvedKey>)`; the
   interpreter resolves addresses before the operand token; delete
   `EffectKeyFact.ResolveIndex`'s evaluation, `HistoryOperand.AgeExpression`'s
   evaluation and `PatternOperand.ReadTupleWord`. Spelling equivalence: the
   `.puck` source is unchanged; `puck compile` of `worlds/debug` and
   `worlds/standard` succeeds and `world.budget`'s per-rule units are unchanged
   or lower (the key expression's tokens were already priced through
   `CollectFacts`, `CompiledFacts.cs:306-327`). Check: W2, W3, W6, W7 green;
   `puck search "KeyNaming" src` returns no line; `puck bench kernels --filter '*StateExpressionKernels*'`
   on `Indirect` is not slower than the tip (one nested `Run` and two leases
   per dynamic key are removed).
4. **The gate decides a `Verdict`.** `RuleEvaluation.Decide`, both conjunct
   sources through the interpreter, the text branch folded in, `GateOpen(out bool)`
   deleted, every caller matching the three cases. Check: W7 green;
   `puck search "faulted: out _" src` returns no line.
5. **Proposal and the commit door.** `RuleEvaluator.Propose`/`Commit`;
   `RuleLatch.Held` and the scheduler captures written only by `Commit`; arms
   `Prepare` once and deliver the prepared form; `FireEffects` becomes the
   read phase's effect sequence; delete `HoldOnFault` and every `RaisedFault`
   poll in `Puck.State.Rules`. Check: W1, W5, W9, W10 green; `puck search
   "Held\[" src/Puck.State.Rules` finds writes only in the commit path;
   `puck test --reproduce` over `worlds/debug` agrees across two runs.
6. **The decision is one firing.** `DecideBinding` fills `Proposal.Decision`
   and fires common and option effects into the proposal's scope; the four
   host program evaluations become declared value sources read by the
   evaluator; group triggers and interactions go through the same
   `Propose`/`Commit`; delete `TryEvaluateLocals`. Check: W4, W11 green;
   `puck search "RuleExpressions.Evaluate" src/Puck.World.Server` finds only
   `EvaluateDetached` in `WorldRuleHost.FlockAffinities.cs`.
7. **Delete the register, close the search and the detached callers.** Remove
   `ArenaScratch`'s fault members and `FaultScope`; `RuleArenaSearchJudge.Score`
   returns `Outcome` and `ArenaSearch.StepJob` reads a `Verdict`;
   `ExpressionFault.Absent` deleted; `RuleFact` deleted in favour of `Fact`.
   Check: W12 green; `puck search "EnterFaultScope|RaisedFault|RaiseFault|LeaveFaultScope" src`
   returns no line; the solution builds.
8. **The outcomes analyzer.** `puck scan --only outcomes` over `src/`: an
   expression statement or discard whose type is `Outcome`, `Fact`,
   `Resolved<T>`, `Verdict` or `RuleOutcome` is a finding. Check: the scan
   over the tree after step 7 reports zero findings; a scratch file with
   `_ = RuleExpressions.EvaluateDetached(...)` reports one.
9. **Documents.** `docs/reference/state/expressions.md:305-320`,
   `rules.md:224-276` and `:441-499`, `patterns.md:102-110`, the state README
   and the `puck-world` skill's rule paragraph describe the outcome, the one
   interpreter and the two-phase firing; the register's paragraphs go.
   `puck docs citations` passes.

Determinism (AGENTS.md rule 4): every previously lost fault now closes a gate,
holds a latch or refuses a firing that used to proceed, so any recorded replay
or hash that reached one of those paths moves. Each step re-records what it
invalidates in the same change; the witness worlds are new and record nothing.

## Cost

The rule VM's per-tick budget is the constraint; the design is cost-neutral
on the dependent-arithmetic path and cheaper on the dynamic-key path.

- **Returning `Outcome` instead of `bool` + two `out` parameters.** `Run` today
  writes two out-slots on every exit and reads them in the caller; the
  16-byte struct is returned in a register pair on SysV x64 and through a
  return buffer on Windows x64, one store and one load per call. `Run` is
  called once per program plus once per `Call`, fold member, `Not`-with-body
  and `And`/`Or` right operand (`RuleExpressions.cs:177`, `:191`, `:248`,
  `:502`), never per arithmetic token, so the delta is a few nanoseconds per
  nested construct. Estimate: within measurement noise on
  `StateExpressionKernels` for `DependentChain`.
- **The stack mark.** `bool absent` becomes a `FaultCase` byte of the same
  width; `Faulted` never lives on the stack. Zero delta.
- **Addresses as program.** Today each dynamic key read runs a nested
  `TryEvaluate`: a second `Run` frame, two `Rent` leases and a `stackalloc`
  frame (`RuleExpressions.cs:95-105`) per key per read. Inlining removes all
  of that; the key expression's tokens were already in the budget's price.
  Estimate: a saving of one call frame and two lease round-trips per dynamic
  key read, measurable on `StateExpressionKernels` with `Read = Indirect`.
- **The gate.** A `Verdict` byte stack replaces a `bool` stack; `Faulted`
  returns early as today. Zero delta.
- **The proposal and commit door.** The latch write moves after the effects
  (same write); the decision proposal is one struct copy of about 80 bytes
  per decision evaluation; `Proposal` is a `ref struct` on the machine stack.
  Arms are evaluated once instead of twice (preflight and delivery), a saving
  of one evaluation per queued arm per firing.
- **The analyzer** runs under `puck scan`, not in the build; zero tick cost.

The implementer measures steps 2 and 3 with
`puck bench kernels --filter '*StateExpressionKernels*'` against the recorded
tip, and a headless debug-world run's `host.frames` simulate time before and
after step 5, as the mechanical checks say. No step may land slower than the
tip on `DependentChain`.

## Open decisions

None are the owner's: the semantics are the register's own contract, restated.
Two engine decisions the implementing lanes make and report:

- Whether the latch and decision bindings move into the arena as engine rows
  (so the journal rewinds them) in step 5, or stay in their stores behind the
  commit door. The door is complete either way; the arena move removes the
  separate checkpoint codec paths (`WorldPersistence.Checkpoint.cs:664`,
  `WorldAuthorityCheckpointCodec.Server.cs:623`) and is the one-write-door
  direction of [S1](state-and-language.md#s1--the-projection-law-and-the-one-door).
- Whether `EvaluateDetached` keeps its own `Outcome` or the detached callers
  (search score, flock affinity, console match, browser read) share one small
  `DetachedReading` helper that spells the `Faulted` case for them. One helper
  is the vigilance-against-duplication answer if a third consumer appears.
