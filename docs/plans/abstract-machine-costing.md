# Abstract-machine costing

Puck currently mixes work-unit heuristics with structural limits. The proposed
result is a deterministic cost model for a fixed reference machine, so the same
composed document, input capacities, and engine version receive the same prices
on desktop, server, and WASM.
Disassembly and published processor measurements establish the prices offline;
the executing computer never chooses them.

This is a proposed implementation brief, based on source analysis at
`197169025`. It does not describe a shipped replacement or a
completed calibration. Its scope is the authored simulation work currently
charged through rules, interactions, decisions, flock affinities, and search,
including the synchronous work their effects cause. Rendering, physics outside
those paths, audio, emulators, and external I/O retain separate accounting.

The owner's contract is an abstract deadline under stated model assumptions.
Use that contract throughout implementation. Do not add host-dependent pricing
or make universal physical deadlines a prerequisite for this work.

## Implementation status

The typed scaffolding exists and the accounting structure of §4 is in place:
heuristic work is a typed bound that propagates an unpriced operation or an
overflow, lines separate setup, check, and firing, and interactions, decisions,
sorts, and transactions are priced at the paths they execute, held against the
evaluator's own trace on enumerated worlds. Search (§6) spends a reserved,
resumable allowance per job, chance plies and playouts included, in the same
heuristic units. The evidence manifest
(`src/Puck.State/ReferenceSchedule.json`) pins eight targets and prices the
unary expression operations; every other coefficient, and the whole memory
profile, is recorded as unmodeled, and admission still runs on the heuristic
work units. The work is two packages of
[the programme](state-and-language.md): [C1](state-and-language.md#c1--ceilings-as-prices)
builds the reference schedule and activates it, and
[the costing correction](state-and-language.md#the-costing-correction) fixes the
accounting and bounds search. This page is their specification; the model's
decision is in [the decisions register](../decisions/state-and-language.md#costing).

## 3. Price schedule and evidence

Put the primitive cost model in `Puck.State`; keep World-specific structural
formulas in `Puck.World.Schema`. Do not introduce another project or a generic
CPU simulator. Follow the existing compiled vocabulary and effect extension
seams rather than creating a second list of authored features.

The proposed minimum contracts are:

```text
CostModel: Id, ReferenceProfile, Coefficients, EvidenceDigest
CostBound: Known(nonnegative reference cycles) | Unmodeled(reason) | Overflow
RuleCost: PerRuleSetup, PerEvaluationCheck, PerFiringEffects
WorldCostReport: ModelId, Scope, RecurringBound, SearchReservations,
                 EditBurstBound, ResourceDimensions, Contributors, Issues
```

Names may follow local conventions. Preserve these distinctions. A report
must be available even when a document exceeds its budget, so authoring can
explain the failure. Use compiled plans once; do not repeatedly compile a
document to produce each panel of its report.

Each primitive coefficient must identify the operation, numeric kind, relevant
shape parameters, reference implementation, instruction evidence, input-domain
bound, and included overhead. An operation with a bounded internal loop needs
a formula, not a benchmark of one convenient operand. Hardware-intrinsic and
software fallback implementations receive the same canonical semantic price
on every executing host; derive that price from the chosen reference lowering.

### Reproducible coefficient derivation

1. Pin the exact .NET SDK/compiler, runtime helpers, build options, ISA, and
   analysis-tool version for each offline evidence target. Include x64 and
   AArch64 lowerings; a surrogate does not prove console-specific support.
   `global.json` pins the band with `rollForward: disable`; the manifest
   records the SDK and that setting together, and a law ties them to the
   checkout. Use fixed optimized reference builds,
   without workload-dependent PGO. A Native AOT reference build is suitable;
   an explicit instruction-set target is required, never `native`. Microsoft's
   [AOT code-generation guidance](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/optimizing.md)
   explains that distinction. This does not require changing production's
   compilation mode.
2. Extend the existing `Puck.Cli/Bench` harness with focused State kernels.
   Capture disassembly of the actual arithmetic entry points and of evaluator
   paths using nonconstant input data. Include called helpers, dispatch, stack
   traffic, bounds/overflow checks, and refusal paths. Also retain isolated
   arithmetic measurements so shared overhead is not charged twice.
3. For the first conservative reference model, serialize instruction service
   within each evidence target's reference path. For instruction form `i`, use
   `q(i) = max(1, ceil(L(i)), ceil(T(i)))`, where `L` is the largest relevant
   operand latency and `T` is reciprocal throughput from that evidence target.
   Bound data-dependent instructions over the supported input domain. A missing
   measurement or helper body is unresolved evidence, not zero or one cycle.
   This is input to abstract service pricing, not a production ISA schedule
   or an out-of-order elapsed-time prediction.
4. Price a kernel by the maximum sum of `q` over its permitted paths. Bound
   loops from source contracts, including fault/refusal paths. Do not require
   a general control-flow solver: a reviewed formula per implementation family
   is sufficient. Sum sequential segments and take maxima only for alternatives
   that cannot both execute. Constant specialization is allowed only when the
   compiled runtime really uses it. Normalize each target's complete kernel
   price to its scalar integer-add service cost. Use an equal-weight median
   within each ISA family and then an equal-weight median of the family results,
   rounded upward; an even-sized median is the exact arithmetic mean of its
   middle two values. This proposed definition of representative service avoids
   overweighting closely related console processors. Pin the cohort and rule
   in the evidence manifest. The resulting semantic prices define the abstract
   machine; they need not upper-bound every sampled physical processor.
   Abstract bounds still maximize permitted input paths and loop counts.
5. Separate memory service from instruction execution. Record scalar access
   counts, bytes copied/cleared, dependent indirect accesses, allocation count,
   allocated bytes, and peak retained bytes. A memory operand's baseline access
   must be charged exactly once. Add profile-specific service beyond that
   baseline; do not assume that every document fits in L1.
6. Check the kernels with `llvm-mca` for each pinned evidence target. Use dependency and
   resource-pressure results to expose mistakes and quantify how conservative
   serial pricing is. MCA does not model cache hits/misses or the complete
   memory hierarchy; its default load latency is optimistic. Treat its output
   as evidence, not a whole-program proof. See [LLVM MCA's model](https://llvm.org/docs/CommandGuide/llvm-mca.html).

The minimum evidence matrix crosses Int/Fixed, small/wide operands, successful
and refused arithmetic, literal/direct/indirect reads, and the capacity breakpoints
of each scan/copy/sort. Include both dependent expression chains and independent
evaluations. Test capacities 0, 1, powers of two and their neighbors, and each
declared maximum where legal. Keep data preparation outside the timed kernel;
include preparation that the real step performs in its separately priced stage.
Hold back mixed workloads when deriving coefficients, then use them to detect
missing costs. A good fit to the same samples used to choose prices is not an
independent check.

**Numerical work still required before activation:** produce the actual kernel
table and a fixed memory-service profile. Instruction-table research alone
does not supply the memory profile or a Puck implementation's full price. Do
not replace the old arbitrary constants with invented nanosecond numbers.

For memory, use published configurations or an offline measurement campaign
covering the reference families. Normalize latency and reciprocal bandwidth
(cycles per byte) to the same scalar service unit and apply the same
family-balanced aggregation before converting back to bandwidth. Pin each configuration,
working-set sizes, access pattern, raw evidence, and chosen conservative service
coefficients. The implementing agent must supply those values and their evidence
before marking the new model complete. Access classes are fixed by the plan:
contiguous copy/clear, contiguous scan, and dependent indirect access. Do not
select classes from current host cache state. Grant a cheaper residency class
only with a documented static footprint/reuse argument; otherwise use the
profile's general-memory class.

Use this explicit conservative service form for each separately owned memory
segment, with bandwidth stored as a rational number of bytes per reference cycle:

```text
MemoryCycles = Starts * StartupCycles
             + DependentAccesses * AdditionalLatencyCycles
             + ceil(BytesTransferred * BandwidthDenominator / BandwidthNumerator)
```

Define whether transferred bytes include read-for-ownership, writeback, and
unaligned line crossings for each class. `AdditionalLatencyCycles` excludes
the baseline already charged by instruction service. Do not put allocation
or garbage collection behind one unexplained multiplier: model bounded
allocation/zeroing work and report allocation and retained storage separately.
Unbounded collection or external-service pauses lie outside the modeled service
contract; that exclusion cannot justify omitting synchronous serialization or
other finite CPU work on the call path.

The first schedule is deliberately conservative. If serial prices make useful
worlds impractical, revise a specific kernel to a documented block service model
and validate it. Do not lower every price with an unexplained global factor.
Keep bulk loops as setup plus per-element/block service rather than expanding
their instruction lists in the authoring process.

Store only Puck's resulting coefficients, formulas, and permitted evidence.
Agner Fog's PDF prohibits public mirroring; link it rather than vendoring its
tables. Check redistribution terms for any other imported dataset too.

## 4. Composition rules and mathematical obligations

Use exact nonnegative integer cycles. Use widened intermediate arithmetic and
checked narrowing, with explicit `Overflow` propagation. A saturated maximum
must never accidentally pass a maximum-valued budget. `Unmodeled` propagates
through reachable work; multiplication by a *proved* zero count may eliminate
it. Never apply floating-point fitting or wall-clock samples during compilation,
validation, scheduling, or replay.

The central rule is that a bound pays for all work on a permitted execution
path, including work performed before learning which alternative applies.

```text
Sequence(A, B)       = A + B
Conditional(P, A, B) = P + max(A, B)
BoundedLoop(N, body) = setup + N * (iterationOverhead + body) + teardown

RuleChecks = sum_i(Setup_i + MaximumEvaluations_i * Check_i)
RuleEffects = ExclusionBound({MaximumFirings_i * Effects_i})
RulePass = sharedPreparation + RuleChecks + RuleEffects + sharedCommitWork
```

`Check` includes binding expressions, zone selection, gate tokens, latch and
schedule checks. A closed-gate memo can improve observed work; assume cold or
invalidated memos for admission unless a stronger invariant is proved. Charge
the slower of cache-hit and recompute paths where necessary. Do not charge
`forEach` key snapshot construction once per key: it is per-rule setup.

Retain the exclusion trie's range reasoning only for firing work. Preserve
its allowance for same-step discriminator writes, including indirect/row-wide
writes. Verify that all writers that can interleave with the discounted rules
are included; if that cannot be established, sum those effects. Do not infer
mutual exclusion between interactions merely because their gates look similar.

Concrete acceptance example, using artificial test coefficients:
three rules each cost 10 cycles to check and 100 to fire. Their gates select
three different immutable values of one phase cell. The bound is
`3 * 10 + 100 = 130`, not 110 and not 330. If a preceding writer can make two
values occur, retain all checks and admit two firing groups plus the writer's
own complete cost. These numbers test the algebra, not the calibration.

For other families:

- **Interactions:** price carrier gathering and up to `L * R` distance tests,
  then nearest-neighbor selection work, then at most `L * min(K, R)` selected
  evaluations. Excluding self can tighten a proved bound. Small `K` never
  removes the initial scan; the current insertion-based selection can also
  require up to `K` comparisons/moves for each qualifying candidate.
- **Decisions:** charge the shared pose image once, each distinct grid rebuild
  once, bounded sort comparisons/moves, grid lookup work, inspected candidates,
  geometric eligibility, sight-query internals, and retained-candidate scores.
  A sight query is not one instruction. Use declared capacities and the query's
  own traversal bound. Selection and effect branches have separate costs.
- **Sorts and lookups:** derive bounds for the implementation actually called.
  `N log N` is an asymptotic description, not an integer upper bound on
  comparisons. Pin the runtime sort implementation or use an explicit proven
  comparison/move envelope. Include comparator cost and string length where
  keys are strings; dictionary access is not a proved constant-time operation
  merely because the usual case is fast.
- **Transactions and mutations:** account for main preflight plus commit on
  success, or main preflight prefix plus refusal-branch preflight/commit on
  failure. Include buffered-state composition, flushes, candidate validation,
  rebuild, and installation at their actual execution boundaries. Model the
  candidate document and its permitted growth, not only the original size.
  Do not change atomicity, immediate visibility, or journal boundaries to make
  a cheaper formula true. A shared flush is charged once only when execution
  shares it.
- **Cadence:** peak per-step work assumes all eligible jobs coincide. Do not
  divide by reconsideration intervals to turn average utilization into a
  deadline guarantee. Staggering earns a lower bound only if implemented and
  derived deterministically from the plan.

Keep startup, recurring steps, and authoring/edit bursts separate. A placement
effect running inside a simulation step contributes its synchronous rebuild
to that step; calling it an edit burst does not move that work off the deadline.
On-demand console edits may have a separate service deadline, but the runtime
must actually schedule them under that contract before receiving that treatment.

## 5. Reference time, tick budgets, and policy

Choose reference throughput separately from instruction prices. The policy is
**3,000,000,000 reference cycles per second**, with **one half reserved for the
modeled authored-work subsystem**. These are product policy, not a 3 GHz CPU
requirement or measured results. They must appear
as named, versioned constants with rationale, not be disguised as instruction
measurements. The reservation does not prove the rest of the engine fits in
the remaining half.

Let `F` be reference cycles per second, `p/q` the reserved share, `H = 50400`
engine ticks per second, and `r` the world's simulation rate. For supported
positive `r`, the engine already requires `H/r` to be an integer.

```text
StepPeriodEngineTicks = H / r
AuthoredBudgetCycles  = floor(F * p / (q * r))
ReferenceEngineTicks(C) = ceil(C * H / F)
ReferenceStepFraction(C) = C * r / F          // retain as a rational
Admission: C * q * r <= F * p               // widened exact arithmetic
```

That per-step allowance is the engine's one per-tick budget. It is not a
second limit beside `RuleCapacity.MaxWorkUnitsPerTick`: that constant becomes
this allowance, expressed in reference cycles, and
[C1](state-and-language.md#c1--ceilings-as-prices) owns its calibration and
every ceiling counted as a share of it.

With the proposed policy, the authored subsystem receives 50,000,000 cycles
per step at 30 Hz and 6,250,000 at 240 Hz. A cost of 1,500,000 cycles is
0.5 reference milliseconds, or 26 whole engine ticks when rounded upward for
display. These are unit-conversion examples, not a mapping from 1,500,000 of
the old work units. There is no justified universal conversion factor between
the old mixed weights and reference cycles.

Do not convert each operation to engine ticks before summing; one engine tick
is about 19.84 microseconds and would erase the useful resolution. Do not use
Q48.16 seconds as an intermediate for instruction-scale timings. Inspect and
reuse the integer division/rounding primitives in `Puck.Maths`; a new public
Maths primitive requires the `maths-laws` workflow.

At simulation rate zero, report no recurring simulation deadline. Do not divide
by zero or silently grant unlimited search. Structural and edit-work reports
remain meaningful. Unused step allowance does not accumulate by default;
accumulation would require a separate deterministic burst contract.

This first report must say **authored simulation cost**, not **total frame
cost**. Keep GPU work, resident bytes, allocation, I/O, and ingress quotas as
named dimensions. [WorldMutationBudgetMeter](../../src/Puck.World.Server/WorldMutationBudgetMeter.cs)
counts authorized submissions: changing the CPU currency must not reinterpret
those security quotas. A future total-world deadline requires costs for the
other server phases and the composition/scheduling of hosted worlds.

## 6. Search must spend a bounded amount of work

Replace `leftover / JudgeCost` as the authoritative search allowance. A node
counter may remain as an authored limit and diagnostic, but it is not a unit
of computational cost.

Give every compiled search job a deterministic cycle allowance. Reserve
mandatory per-step search maintenance before distributing the remaining
authored allowance. Initially divide the remainder equally across declared
jobs, rounding down and leaving the remainder unused; this preserves simple,
predictable allocation. Never infer available work from currently idle jobs
or host timings unless a later explicit scheduler contract implements that.

Represent the search walk as resumable bounded units. Before executing a unit,
reserve its complete cost; yield if it does not fit. Count candidate inspection
and cursor maintenance even when no judge runs. Include frame copy/reset,
candidate application, judge, scoring, position hashing/transposition work,
tree selection and playout, chance outcome traversal, backpropagation, and
output installation. The sum of reservations bounds the step's modeled work.

Make root chance evaluation and recursive chance descendants obey this same
budget. They must resume through explicit continuation state instead of doing
a whole recursive subtree inside one outer node. Tree/UCT playouts need the
same treatment. Preserve deterministic traversal, tie breaking, seed state,
and atomic publication of completed outputs across yields. Charge restart and
cancellation work too; frequent input changes must not provide free rebuilds.

If the smallest indivisible unit exceeds its job's allowance, either split it
at a semantics-preserving boundary or reject the plan with the exact reason.
Never let it overspend "to make progress" and never allow permanent silent
starvation. Search changes can intentionally move completion ticks and results;
verify determinism and update affected fixtures/replays in the same change.
There are no consumers requiring an old-price compatibility mode.

## 7. Authoring and identity

Return the same typed cost report from the shared C# implementation to the
console and browser. Extend [BrowserExports](../../src/Puck.World.Browser/Exports/BrowserExports.cs)
and the existing worker protocol in
[`portal/src/native`](../../src/Puck.Dashboard/src/portal/src/native).
Encode 64-bit values as decimal strings across JavaScript, following the
existing export convention. Do not duplicate formulas or coefficients in
TypeScript.

An author should see the reference budget used, remaining headroom, costliest
rows, the capacity that drives each cost, and unresolved scope. Expandable
details explain checks versus firings, scans versus selected candidates,
shared preparation, and structural rebuilds. Include stable source paths in
the report so composing a module can map costs back to its authoring source.
Keep measured host timings in a clearly separate diagnostic view.
Expose exclusion groups and shared costs so the displayed aggregate can be
reconciled exactly; a list of each rule's isolated maximum will not sum to a
discounted total. Recurring work plus all search reservations must never exceed
the authored subsystem allowance.

Compute cost after composition/validation on the existing worker, reuse its
compiled products, and discard results for superseded document revisions.
Do not analyze on pointer hover or on the render thread. A budget report must
not introduce the interaction stalls this authoring work is meant to remove.

Pin `ModelId` and the schedule digest in compiled reports/plans and engine
identity. Extend replay identity only if its existing engine-version contract
does not already identify the schedule exactly. Never embed a second mutable
copy of the table in documents or let an authored document select a cheaper
profile. Deliberate schedule revisions are engine-version changes.

## 8. Implementation order and acceptance

Steps 1, 3, and 5 are the costing correction. Steps 2 and 4, the reference
schedule and activation, are C1's, because a price and the ceilings expressed
as shares of it cannot be calibrated apart, and one package owns every
ceiling. Implement each step completely before moving to the next. A
temporary comparison against the old model is useful during development; delete
the old heuristics when the replacement becomes authoritative.

1. **Correct the accounting structure.** Separate setup/check/firing costs;
   correct exclusions, pair inspections, and shared preparation. Add explicit
   unknown/overflow handling and exhaustive vocabulary coverage. Keep current
   weights labeled heuristic while building the replacement; do not rename
   them to cycles.
2. **Build and substantiate the reference schedule.** Add the focused benchmark
   cases to the existing harness, capture the fixed reference code, derive
   kernel and memory coefficients, and record formulas/evidence in one owning
   manifest. Every reachable operation in scope must have evidence or an
   explicit unmodeled result. A test should enumerate the registered vocabulary
   so a new operation cannot silently escape pricing. A source/compiler change
   affecting a reference kernel must require review of its evidence.
3. **Integrate structural costs and bounded search.** Follow the actual mutation
   and search paths above. Extend existing tests and diagnostics; do not invent
   a parallel permanent verification runner. Verify independent observed event
   counts against static bounds, not only one cost formula against another.
4. **Activate reference-cycle admission and shared reporting.** Apply the exact
   rate/share arithmetic, retire the 2,000,000 heuristic count and the old
   fallbacks in favor of the priced per-step allowance section 5 defines, and
   connect validator, console, search plans, and browser to the same report.
   The ceiling itself survives as that calibrated price, which
   C1 derives together with every ceiling expressed as a share of it. A reachable unmodeled contribution prevents certification of
   the scoped deadline; it must not appear as zero. In-scope gaps must be
   resolved before final activation. Out-of-scope systems remain named as such.
5. **Validate usefulness and synchronize documentation.** Compare reference
   costs with independent full-workload measurements and inspect disagreements
   by family. Do not tune just to Klondike or raise the allowance to make an
   expensive fixture pass. Update State and World Schema READMEs, CLI commands,
   browser protocol docs, XML/inline cost claims, and applicable skills. Retire
   this brief's implemented instructions into those owning surfaces.

Required adversarial cases:

| Area | Required observation |
|---|---|
| Exclusion | All checks remain charged; disjoint and overlapping ranges; same-step phase writes; cold and invalidated memos; indirect writers; nested transactions. |
| Arithmetic | Int/Fixed wide and narrow paths, overflow, zero division, ties, missing coefficients, maximum counts, and deterministic overflow refusal. |
| Interactions/decisions | Sparse and coincident crowds; `K=1` still scans the candidate set; maximum capacity; shared grids; expensive eligibility and sight paths. |
| Structural effects | One edit versus many, state-only batching versus placement flushes, failure at the last preflight member, candidate growth, and population rebuild at capacity. |
| Search | Zero allowance, exact fit, one cycle short, all candidates rejected, root and internal chance nodes, deep tree playouts, frame-size growth, frequent restarts, and eventual progress. |
| Time and identity | 30/240 Hz and another supported divisor; rate zero; exact rounding boundaries; equal reports and plan allowances across native/WASM; schedule mismatch cannot reuse a stale report. |
| Authoring | Over-budget documents still produce explanations; composed source attribution; large integers survive JS; stale worker responses cannot replace current results. |

For small bounded worlds, exhaustively enumerate state/input cases and compare
an independently counted execution trace with the static bound. Counts must
include closed gates, rejected candidates, and failed mutation work. The
invariant is `observed modeled service <= admitted static/reserved service`.
This proves the accounting relative to the schedule. Benchmarking separately
tests whether that schedule is a useful representation of the chosen reference.
Report ratios and the largest unexplained deviations rather than claiming a
physical worst case from a finite benchmark run.

Extend the existing suites in `tests/Puck.State.Tests`,
`tests/Puck.World.Schema.Tests`, `tests/Puck.World.Tests`, and
`tests/Puck.World.Browser.Tests`. Start with targeted tests, then run each
affected project in Release. Run the real World for changed scheduling or
mutation behavior using the `puck-world` skill's supported stdin/replay recipes.
Use `puck bench world` for an independent integration check and focused
BenchmarkDotNet filters for coefficients. If browser protocol/UI changes,
run the portal's `npm test` and `npm run build` against a freshly built WASM
artifact; tests using a stale engine do not establish parity.

**Done means:** one host-independent schedule with inspectable evidence; sound
composition for all reachable work in scope; bounded, progressing search;
exact rate-based admission; useful source-attributed explanations; and
verification of those properties. Merely replacing numeric literals or adding
an attractive budget meter does not finish this work.

---

[Plans](README.md) · [State and the authoring language](state-and-language.md)
