# Puck.Maths.Tests

A declaration-first law suite for the `Puck.Maths` fixed-point algebra cluster. Cross-cutting logic lives in five
shared modules; every test is a declaration in `laws/*.json` bound to a run delegate in `LawRegistry`, so there is no
battery-style duplication by construction. The declarations are data rather than C# for one reason above the others:
leg text is the single thing no gate can check — nothing reads the bodies a leg describes — so it has to stay readable
by a person, which four-hundred-character string literals buried among generic combinators are not.

## Modules

| # | Module | Files | Role |
| --- | --- | --- | --- |
| 1 | Domains | `Domains.cs`, `Frontier.cs` | Operand sources: one committed edge set, an edge-biased `Pcg32XshRr` sampler, and a rolling frontier mapped through the stratified `DigitalNetSampler`. |
| 2 | Oracles | `Oracles.cs` and its `Oracles.<area>.cs` partials | Shared-nothing `BigInteger` reference arithmetic (dyadic round/wrap, quadratic product/norm, Möbius step). Never calls a subject kernel. |
| 3 | Laws | `laws/*.json`, `LawDeclarations.cs`, `LawRegistry.cs` and its `LawRegistry.<families>.cs` partials, `Laws.cs`, `LawCase.cs`, `Subjects.cs` and its `Subjects.<family>.cs` partials, `*Claims.cs` | Declarations are **data**: id, tier, covered members and leg prose live in `laws/<family>.json`, one file per family (several files may feed one family — the loader reads every `*.json` and keys by id). `LawRegistry.cs` and its partials hold only a `Case(id, run)` binding per law, grouped into per-family builder methods that `LawRegistry.Build()` concatenates; `Laws.cs` holds the combinators; the claim bodies sit in `Subjects.cs`, its partials, and the `*Claims.cs` files. A declaration without a binding, or a binding without a declaration, fails `LawDeclarationTests` by name. |
| 4 | Coverage | `Coverage.cs`, `CoverageManifestTests.cs` | Reflection-driven coverage ratchet against `coverage-manifest.json`. |
| 5 | Ledger + bench | `Bench.cs`, `BenchTests.cs`, `LawTests.cs`, `LedgerFixture.cs` | Tier runner, RESULTS ledger, and the breach-tolerant bench. |

Committed artifacts (all deterministic, stable ordering, update-on-change): `frontier.json`, `coverage-manifest.json`,
`bench-baselines.json`, `RESULTS.md`.

Every artifact write is **execution-gated**: the ledger persists an artifact only when the check that owns it actually
ran this session. The manifest belongs to the ratchet gate, the frontier to the runs that consumed domains, each
`RESULTS.md` tier block to that tier, the bench block to the bench, and the coverage block to the ratchet. A filtered or
single-tier run therefore leaves every other record exactly as its own last run left it — and can never quiet a gate it
never executed. The frontier carries a second gate on top of that one: it is **green-gated** as well, and advances only
when every law the session ran passed.

## Tiers and how to run them

Tier selection is fully declarative — no environment variables anywhere. The project binds
`default.runsettings` through `RunSettingsFilePath`, whose `TestCaseFilter` excludes Deep, Exhaustive and Bench, so a plain
`dotnet test` runs **Smoke + Default** only. Each other tier is a committed `*.runsettings` selected with
`--settings` (a CLI `--settings` overrides the bound default).

| Tier | Command | Budget |
| --- | --- | --- |
| Default (Smoke + Default) | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release` | < 30 s |
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings` | < 2 s |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings` | exhaustive |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/exhaustive.runsettings` | long — full-width sweeps over an ENTIRE carrier; on demand or nightly, never in a change loop |
| Bench | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/bench.runsettings` | timing |

## The ratchet

The coverage gate fails only when a public `Puck.Maths` member is classified nowhere — no state in the committed
manifest, no covering law, no waiver — or when a member moved covered→uncovered. It never fails on the initial
uncovered backlog: coverage only grows. The manifest is regenerated mechanically by the assembly ledger from the
registry's member declarations, so it tracks the surface without hand-editing.

Classification is explicit, and only two things classify a member: a law case that declares it, or a waiver with a
reason in `Coverage.WaiverDeclarations`. Landing a new public member together with its law (or its waiver) therefore
passes on the first run; landing one with neither fails on **every** run, because the regenerator never writes a state
for a member the committed manifest does not already mention — the failing run cannot heal itself by recording the
member as uncovered. (Bootstrapping is the one exception: with no committed manifest at all, the whole surface is
written once, backlog included.)

Coverage is credited only from cases the runner executes. Every entry in `LawRegistry` is a law `LawTests` runs and
asserts; the bench has no registry declaration, so timing work never counts as coverage.

## The frontier

Each domain owns a committed block counter `k`; a run consumes sample indices `[k·B, (k+1)·B)` and the ledger advances
`k` by **one — on a green run**, so the next run takes the adjacent window and consecutive runs sweep contiguous ground.
Two successive green runs therefore sweep fresh operands and advance `frontier.json`, while `coverage-manifest.json` and
`bench-baselines.json` stay byte-stable.

**The advance is green-gated.** A session in which *any* law failed persists **no** advance, for **no** key: it writes
`frontier.json` not at all and leaves the `Frontier` block of `RESULTS.md` reading whatever its own last run left. The
committed counter is what decides which operands the *next* run sweeps, so advancing past a red would hand the re-run a
different window and let the failure vanish unfixed — which is exactly how a latent divergence once stayed hidden until a
third consecutive run happened to land on it again. Leaving the counters alone makes the re-run take the same window, the
same derived seeds and the same indices, so the red reproduces where it was found.

Two details of the gate are deliberate. It sits at **persistence**, never at consumption: a domain still hands out its
index while the sweep is running, so operand determinism *within* a run is untouched by how that run ends. And one law
failure withholds the advance for *every* key, because a red run's whole sweep is suspect and a partial advance would
leave the committed frontier in a state no run ever swept from.

The non-law gates — the ratchet, both leg gates, the bench — do **not** gate the frontier, and need not: only the
combinators in `Laws.cs` consume a domain, so those gates sweep no operands. Their verdicts are pure functions of the
reflected member surface, the declaration text and the tool files, and reproduce identically on the next run whatever the
counters say. They have no sweep to be masked by.

## The RESULTS ledger

`RESULTS.md` is a merge of per-block last-run records, not a whole-session snapshot: `Invocations`, one block per tier,
`Bench`, `Coverage`, `Frontier`. A run rewrites only the blocks it owns and copies the rest forward verbatim, so a bench
session no longer reports the law suite as having executed zero cases, and alternating tiers no longer thrashes the
file. The `- last run:` line of each block carries that block's date and nothing else; those lines are excluded from
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

Cost is the **bench** tier's job, and that tier already does it properly: it measures a *ratio* rather than a
duration, compares it against a baseline held **per machine** in
[`bench-baselines.json`](bench-baselines.json) keyed by `Bench.Fingerprint()`, and skips without recording anything
when `Bench.Calibrate()` says the machine is busy. Its `RESULTS.md` block names the machine it ran on, because a ratio
is the one number here that hardware moves — the committed baselines differ by more than 2× between two of this
repository's own machines. Restore timing to any other block only with that machinery attached to it.
