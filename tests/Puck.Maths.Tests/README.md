# Puck.Maths.Tests

A declaration-first law suite for the `Puck.Maths` fixed-point algebra cluster. Cross-cutting logic lives in five
shared modules; every test is a declaration in `laws/*.json` bound to a run delegate in `LawRegistry`, so there is no
battery-style duplication by construction. The declarations are data rather than C# for one reason above the others:
leg text is the single thing no gate can check—nothing reads the bodies a leg describes—so it has to stay readable
by a person, which four-hundred-character string literals buried among generic combinators are not.

## Modules

| # | Module | Files | Role |
| --- | --- | --- | --- |
| 1 | Domains | `Domains.cs` | Operand sources: one committed edge set, an edge-biased `Pcg32XshRr` sampler, and a fixed stratified window mapped through the stratified `DigitalNetSampler`. |
| 2 | Oracles | `Oracles.cs` and its `Oracles.<area>.cs` partials | Shared-nothing `BigInteger` reference arithmetic (dyadic round/wrap, quadratic product/norm, Möbius step). Never calls a subject kernel. |
| 3 | Laws | `laws/*.json`, `LawDeclarations.cs`, `LawRegistry.cs` and its `LawRegistry.<families>.cs` partials, `Laws.cs`, `LawCase.cs`, `Legs.cs` (the `Leg` record the JSON leg rows materialize into, and the combinator-shape observer), `Subjects.cs` and its `Subjects.<family>.cs` partials, `*Claims.cs` | Declarations are **data**: id, tier, covered members and leg prose live in `laws/<family>.json`, one file per family (several files may feed one family—the loader reads every `*.json` and keys by id). `LawRegistry.cs` and its partials hold only one binding per law—`ClaimCase(id, claim)` for a single `Laws.Claim`, `SweptCase(id, domain, width, claim)` for a single `Laws.SweptClaim`, `Case(id, run)` otherwise—grouped into per-family builder methods that `LawRegistry.Build()` concatenates; `Laws.cs` holds the combinators; the claim bodies sit in `Subjects.cs`, its partials, and the `*Claims.cs` files, sharing `Refusals.cs` and `DoublingTower.cs`. A declaration without a binding, or a binding without a declaration, fails `LawDeclarationTests` by name. |
| 4 | Coverage | `Coverage.cs`, `LawCompletenessTests.cs` | Declared public members and waivers resolve against the current surface. |
| 5 | Evidence | `LawTests.cs`, `LawLegs.cs` | Tier runner and structural evidence checks. |

The owner rules out recorded Maths registers, their coverage ratchet and its floor.
Runs read authored declarations and use fixed seeded operand windows; they write no registers.

## Tiers and how to run them

Tier selection is fully declarative; no environment variable selects a tier. Every case carries its tier as the
`tier` trait, and Deep and Exhaustive cases are explicit, so a plain `dotnet test` runs the **Default** tier only. A run
opts into the explicit cases with `--explicit on` and selects one tier with `--filter-trait tier=<tier>`; without
`--explicit on`, a Deep or Exhaustive filter selects no test. To run one law, filter on its id, which is the case's
display name: `--explicit on --filter-display-name '<law-id>*'`. The trailing
wildcard includes the runner's appended argument display; an exact bare id selects no row.

Build with `-nodeReuse:false`, then run with `--no-build`. `dotnet test` forwards a switch it does not know to the
xUnit v3 test host, which exits 5, so `-nodeReuse:false` belongs to the build and restore alone:

```sh
dotnet build tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release -nodeReuse:false
```

| Tier | Command | Scope |
| --- | --- | --- |
| Default | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --no-build` | Ordinary laws and structural checks |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --no-build --explicit on --filter-trait tier=Deep` | Broader domains and finite carriers |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --no-build --explicit on --filter-trait tier=Exhaustive` | full-carrier sweeps and expensive finite combinatorial-prime checks |

The Default tier reaches the line coverage CI counts for `Puck.Maths`. Deep holds
what Default does not reach: the exhaustive
four-operand edge cross product (`Domains.Quads`), finite carriers swept whole (the 16-bit unit fraction, GF(2^8)), and
modulus, degree and band ladders longer than Default's. A Deep row that only draws more random operands from the
same domain as its Default sibling adds no evidence and does not belong. For the same reason there is no Smoke tier.

The high-bound combinatorial-prime checks use Exhaustive to keep expensive
counts and rank searches out of the ordinary Deep run. Their declarations
name a finite set of operands: this scheduling choice does not establish a
whole-domain proof. Run budgets are measured separately through the prime-request
survey; a selected finite fixture does not bound every request in its tier.

## Structural law completeness

`LawDeclarationTests` requires exactly one binding for each declaration.
`LawCompletenessTests` resolves declared public members and checks waiver reasons.
The retired ratchet grandfathered uncovered public members through saved state;
a check requiring every public member to have a law or waiver does not yet stand
on the authored declarations alone. See [open work](../../docs/plans/open-items.md).

## Reproducible operand windows

Every domain starts at block index zero. Edge batteries, the seeded random sample
and the stratified sample remain deterministic across repeated runs. Default and
Deep select their declared breadth without changing any future run's operands.
Exact fixed-point comparisons and determinism laws remain in the suite.
Performance measurements belong to `puck bench`; no law measures elapsed time.

## Documentation

📚 [Deterministic numerics](../../docs/reference/maths.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
