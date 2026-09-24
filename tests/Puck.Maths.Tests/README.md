# Puck.Maths.Tests

A declaration-first law suite for the `Puck.Maths` fixed-point algebra cluster. Cross-cutting logic lives in five
shared modules; every test is a declaration in `laws/*.json` bound to a run delegate in `LawRegistry`, so there is no
battery-style duplication by construction. The declarations are data rather than C# for one reason above the others:
leg text is the single thing no gate can check—nothing reads the bodies a leg describes—so it has to stay readable
by a person, which four-hundred-character string literals buried among generic combinators are not.

## Modules

| # | Module | Files | Role |
| --- | --- | --- | --- |
| 1 | Domains | `Domains.cs`, `Frontier.cs` | Operand sources: one committed edge set, an edge-biased `Pcg32XshRr` sampler, and a rolling frontier mapped through the stratified `DigitalNetSampler`. |
| 2 | Oracles | `Oracles.cs` and its `Oracles.<area>.cs` partials | Shared-nothing `BigInteger` reference arithmetic (dyadic round/wrap, quadratic product/norm, Möbius step). Never calls a subject kernel. |
| 3 | Laws | `laws/*.json`, `LawDeclarations.cs`, `LawRegistry.cs` and its `LawRegistry.<families>.cs` partials, `Laws.cs`, `LawCase.cs`, `Subjects.cs` and its `Subjects.<family>.cs` partials, `*Claims.cs` | Declarations are **data**: id, tier, covered members and leg prose live in `laws/<family>.json`, one file per family (several files may feed one family—the loader reads every `*.json` and keys by id). `LawRegistry.cs` and its partials hold only one binding per law—`ClaimCase(id, claim)` for a single `Laws.Claim`, `SweptCase(id, domain, width, claim)` for a single `Laws.SweptClaim`, `Case(id, run)` otherwise—grouped into per-family builder methods that `LawRegistry.Build()` concatenates; `Laws.cs` holds the combinators; the claim bodies sit in `Subjects.cs`, its partials, and the `*Claims.cs` files, sharing `Refusals.cs` and `DoublingTower.cs`. A declaration without a binding, or a binding without a declaration, fails `LawDeclarationTests` by name. |
| 4 | Coverage | `Coverage.cs`, `CoverageManifestTests.cs` | Reflection-driven coverage ratchet against `coverage-manifest.json`. |
| 5 | Ledger | `LawTests.cs`, `LedgerFixture.cs` | Tier runner and the RESULTS ledger. |

Committed artifacts (all deterministic, stable ordering, update-on-change): `frontier.json`, `coverage-manifest.json`,
`leg-ledger.md`, `RESULTS.md`.

## Where a run writes

A test run never changes the checkout. Every run writes its ledger to `records/maths-ledger/` beside the test assembly
in the build output, reading the previous frontier and `RESULTS.md` from there when an earlier run left them, and
from the committed files otherwise. The committed artifacts change only through the recording verb:

```sh
puck baselines maths-ledger
```

It clears the run's ledger directory, runs the unfiltered Default tier, and promotes what the run wrote over the
committed files; commit the result with the change that caused it. `puck baselines maths-ledger --check` compares
instead. Every run checks the two artifacts that follow from the declarations alone: the ratchet gate fails when
`coverage-manifest.json` differs from what the declarations generate from it, and the leg gate fails when
`leg-ledger.md` differs from the rendered declarations. Each failure names the verb to record with. `frontier.json`
and `RESULTS.md` are run records, so a stale committed copy fails nothing; record them when a change should move the
committed sweep window or report. `LedgerOutputTests` holds the writer to this: it refuses every committed path.

`TestPaths` resolves these files in the running checkout through the shared
repository locator. CI source-path mapping does not change where declarations
are read or generated artifacts are written; see [CI and releases](../../docs/development/ci.md).

Every artifact write is **execution-gated**: the ledger persists an artifact only when the check that owns it actually
ran this session. The manifest belongs to the ratchet gate, the frontier to the runs that consumed domains, each
`RESULTS.md` tier block to that tier, and the coverage block to the ratchet. A filtered or
single-tier run therefore leaves every other record exactly as its own last run left it—and can never quiet a gate it
never executed. The frontier carries a second gate on top of that one: it is **green-gated** as well, and advances only
when every law the session ran passed.

Artifact replacement remains atomic when another Windows process briefly holds a
report open without delete sharing: the writer retries until the replacement succeeds
or the run is cancelled. Failed writes remove their sibling staging file;
they never fall back to rewriting the live report in place.

## Tiers and how to run them

Tier selection is fully declarative; no environment variable selects a tier. The project binds
`default.runsettings` through `RunSettingsFilePath`, whose `TestCaseFilter` excludes Deep and Exhaustive, so a plain
`dotnet test` runs **Smoke + Default** only. Each other tier is a committed `*.runsettings` selected with
`--settings` (a CLI `--settings` overrides the bound default). A CLI `--filter` does not override it: it is combined
with the bound default's filter, so `--filter "tier=Exhaustive"` selects no test. To run one law of an opt-in tier,
pass its tier's `--settings` together with a `--filter` on the law's id.

| Tier | Command | Budget |
| --- | --- | --- |
| Default (Smoke + Default) | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release` | < 30 s |
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings` | < 2 s |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings` | exhaustive |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/exhaustive.runsettings` | long—full-width sweeps over an ENTIRE carrier; on demand or nightly, never in a change loop |

## The ratchet

The coverage gate fails only when a public `Puck.Maths` member is classified nowhere—no state in the committed
manifest, no covering law, no waiver—or when a member moved covered→uncovered. It never fails on the initial
uncovered backlog: coverage only grows. The manifest is regenerated mechanically by the assembly ledger from the
registry's member declarations, so it tracks the surface without hand-editing; `puck baselines maths-ledger` commits it.

Classification is explicit, and only two things classify a member: a law case that declares it, or a waiver with a
reason in `Coverage.WaiverDeclarations`. Landing a new public member together with its law (or its waiver) therefore
passes the ratchet on the first run, and the manifest check then asks for `puck baselines maths-ledger`; landing one with neither
fails on **every** run, because the regenerator never writes a state
for a member the committed manifest does not already mention—the failing run cannot heal itself by recording the
member as uncovered. (Bootstrapping is the one exception: with no committed manifest at all, the whole surface is
written once, backlog included.)

For example, [cost-model laws](laws/cost-model.json) pair complete status and refusal tables with independent
`BigInteger` arithmetic for `CostBound` and `CostModelProfile`. They check overflow boundaries, exact budget
admission and upward deadline rounding; they establish the abstract arithmetic contract, not hardware calibration.

Coverage is credited only from cases the runner executes. Every entry in `LawRegistry` is a law `LawTests` runs and
asserts, and no law measures time.

## The frontier

Each domain owns a block counter `k` in `frontier.json`; a run consumes sample indices `[k·B, (k+1)·B)` and the ledger
advances `k` by **one—on a green run**, so the next run takes the adjacent window and consecutive runs sweep contiguous
ground. Two successive green runs therefore sweep fresh operands and advance the run's own `frontier.json`, while
`coverage-manifest.json` and `leg-ledger.md` stay byte-stable. A build output with no ledger yet starts from the
committed counters, which move only when a run records.

**The advance is green-gated.** A session in which *any* law failed persists **no** advance, for **no** key: it writes
`frontier.json` not at all and leaves the `Frontier` block of `RESULTS.md` reading whatever its own last run left. The
persisted counter is what decides which operands the *next* run sweeps, so advancing past a red would hand the re-run a
different window and let the failure vanish unfixed. Leaving the counters alone makes the re-run take the same window, the
same derived seeds and the same indices, so the red reproduces where it was found.

Two details of the gate are deliberate. It sits at **persistence**, never at consumption: a domain still hands out its
index while the sweep is running, so operand determinism *within* a run is untouched by how that run ends. And one law
failure withholds the advance for *every* key, because a red run's whole sweep is suspect and a partial advance would
leave the persisted frontier in a state no run ever swept from.

The non-law gates—the ratchet and both leg gates—do **not** gate the frontier, and need not: only the
combinators in `Laws.cs` consume a domain, so those gates sweep no operands. Their verdicts are pure functions of the
reflected member surface, the declaration text and the tool files, and reproduce identically on the next run whatever the
counters say. They have no sweep to be masked by.

## The RESULTS ledger

`RESULTS.md` is a merge of per-block last-run records, not a whole-session snapshot: `Invocations`, one block per tier,
`Coverage`, `Legs`, `Frontier`. A run rewrites only the blocks it owns and copies the rest forward verbatim, so
alternating tiers never thrash the file. The `- last run:` line of each block carries that block's date and nothing else; those lines are excluded from
change detection, so a run that moves nothing leaves the file untouched.

**Every figure in it is machine-independent, and that is the point.** Executed case counts, coverage counts, leg
counts and frontier indices are functions of the commit, not of the hardware, so the same tree produces the same
`RESULTS.md` on every machine the engine is developed against and any difference you read there is a real one. The
file records **no duration**, deliberately. One written here could not survive three defects at once: it would carry
no machine identity, so each machine's run would overwrite the last one's and two consecutive readings would compare
two different computers; it would be the whole *session's* elapsed time stamped identically onto every block, so it
could not attribute cost to a tier even on one machine; and nothing here measures the environment, so a figure taken
on a loaded machine would be committed as fact. A block carried forward from an earlier run has its last-run line
normalized to the date alone, so a rarely-run tier cannot keep publishing a shape this ledger does not write.

No law measures time either. A claim about cost is stated deterministically—an allocation meter reading, as in
`complex.multiply-routes-allocate-nothing`—or it is not a law: latency and throughput are `puck bench`'s
measurements (`puck bench kernels --filter '*ComplexMulNarrow*'` compares the generic and hand-written complex multiply).

## Documentation

📚 [Deterministic numerics](../../docs/reference/maths.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
