# Remaining rule-fault work

The settled rationale lives in [Rule faults](../decisions/state-and-language.md#rule-faults).
The reference owns [outcomes and absence](../reference/state/expressions.md#constant-folding-and-faults),
[journaled firings and witnesses](../reference/state/rules.md#make-a-firing-atomic),
and [the install boundary and analyzer](../reference/state/hosting.md#the-install-boundary).
This plan holds only unresolved work.

## Not built

- Queued arms retain source outcomes and host bindings, but not a body target's
  population generation. Carry that identity and check it at delivery so a
  body index reused by installation cannot silently retarget an arm. The
  decision continuation's `WorldRuleHost.ProposedPairLive` generation check
  does not supply this guarantee for queued arms. Check a queued body delivery
  across an install that retires and replaces its target.
- Host preflight scratch remains on `WorldRuleHost`, outside the evaluator's
  proposal. Move per-firing scratch into proposal ownership rather than rely
  on serial host reuse. Check nested firing cleanup and refused preflight so
  no proposed unit or binding survives into the next firing.

## Differs from the design

`RuleEvaluator.ProposeEffects` reads queued `IRuleEffect.Sources` in the
preflight loop, after every reversible effect and the continuation have run.
The original design read each source immediately before its arm's authored
position. Consequently `emitCue(key: x); x = 5` reads 5 in the current code.
Choose the intended author contract and make the interpreter, reference and
spelling-equivalence examples agree. The reference describes the current late
read. Both timings retain one source read and reuse its outcome at delivery.
`WorldRuleHost.ProposedPairLive` does not read these sources: its queued
document rows carry literal identities.

## Unmeasured

`WorldRuleHost.ProposedPairLive` materializes the arena when common effects
queue a document row and the option has a neighbour candidate. Every option
with a candidate also walks placements for the cell-driven forecast;
`WorldPopulation.ReconcileKeeps` copies scratch slots only when the forecast
retires or admits a body. Measure allocations and counted decision work in an
effects-heavy headless debug run to decide whether this needs improvement.

Measure `StateExpressionKernels` on `Literal`, `Direct` and `ComputedKey` for
`DependentChain` and `IndependentEvaluations`, with successful, absent,
faulted and short-circuited variants, allocations and the disassembly's return
convention, on Windows and Linux. `Indirect` uses a cell pointer and does not
measure the nested computed-key evaluation that lowering removed. Measure an
effects-and-decision-heavy headless run's simulate work with firings that
succeed, refuse and fault; timing comparisons belong in the lead's quiet
measurement window. These measurements establish the cost of the outcome
return, removed nested frames, journal participants and one-read arms.
