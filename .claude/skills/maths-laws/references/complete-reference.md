# Maths-laws complete reference

Read the relevant section when `SKILL.md` routes here.

## Contents

- [Declaration-first cases](#1-declaration-first-the-declaration-is-data-the-run-is-code)
- [Tiers and budgets](#2-tiers-and-budgets)
- [Legs](#3-legs-what-the-statement-stands-on)
- [Oracles](#4-oracles)
- [Domains](#5-domains)
- [Law completeness](#6-structural-law-completeness)
- [Claim bodies](#7-claim-bodies)
- [Mutation proof](#8-validating-that-a-new-law-actually-bites)
- [Deliberate corrections](#9-rule-4--a-deliberate-correction-is-expected-to-move-values)

How the suite is shaped, what a declaration owes, and how to check that a new
law discriminates. It does not design the kernel under test. The owner's target
for the suite (the smallest set of tests that reaches 100% line coverage, plus
an approved keep-list) is in `AGENTS.md` and governs which laws to write at
all: [what a law is for](../SKILL.md#what-a-law-is-for).

Sources of record, all more detailed than this file:
[tests/Puck.Maths.Tests/README.md](../../../../tests/Puck.Maths.Tests/README.md)
(modules, tiers, structural completeness and deterministic sampling) and the XML docs on `Legs.cs`,
`Laws.cs`, `Domains.cs`, `Coverage.cs` and `LawLegs.cs`. When one of those
disagrees with this file, verify the current implementation and update this
reference in the same change.

---

## 1. Declaration-first: the declaration is DATA, the run is code

**A law is authored in two places, and the split is the point.** Never add a
`[Fact]` or `[Theory]` for a law. `LawTests.Law` drives `LawRegistry.All` as one
theory row per case, tagged with the case's tier trait and **named by its id** —
the test display name *is* the law id.

**The declaration** — id, tier, covered members and every leg — is a JSON row in
`tests/Puck.Maths.Tests/laws/<family>.json`. One file per family; more than one
file may contribute to a family (`presented.json` and `presented-zeta.json` both
do), because the loader reads every `*.json` in the directory and keys by id.

```json
{
  "Id": "<family>.<kebab-statement>",
  "Tier": "Default|Deep|Exhaustive",
  "Members": [ { "Type": "Puck.Maths.FixedQ4816", "Name": "Multiply" } ],
  "Legs": [ {
    "Kind": "Classical|PresentedTwin|InTreeIndependent|SharedSubstrate|Structural",
    "Flavor": "None|FusedSubstrate|SharedExactKernel|DelegationTwin|Transcription|IntraPresented|SharedUpstream",
    "Subject": "…", "Against": "…", "Shared": "…", "Citation": "…", "Absolute": ""
  } ]
}
```

`Type` is `Type.FullName`; a generic definition looks like
``Puck.Maths.PresentedAlgebra`2`` and a closed one carries its assembly-qualified
arguments. Copy the string verbatim from an existing row wherever the same member
already appears rather than reconstructing it. `Flavor` is `None` unless `Kind`
is `SharedSubstrate`. A row is materialized directly (`LegText.ToLeg`, into the
`Leg` record in `Legs.cs`), and `LawLegTests` rejects an illegal slot
combination, so the table is the vocabulary a row's slots follow:

| Leg | Slots it fills |
|---|---|
| Classical | Subject, Against |
| Published constant (classical) | Subject, Against=table, Citation=provenance |
| InTreeIndependent | + Citation=envelope |
| FusedSubstrate / IntraPresented / SharedUpstream | + Shared |
| SharedExactKernel / DelegationTwin | + Shared, Citation=envelope |
| Transcription (faithful carriage) | Shared=transcribes, Citation=witness |
| Structural | Subject=statement |
| Doc gap (structural) | Subject, Citation carries the `DOC GAP:` marker |
| Relative canary (structural) | Kind=Structural, + Absolute |

**The binding** — the one thing JSON cannot carry — is a line in
`LawRegistry.Build()`:

```csharp
ClaimCase(id: "<family>.<kebab-statement>", claim: Subjects.<Claim>),
SweptCase(id: "<family>.<kebab-statement>", domain: <Domain>, width: <lanes>, claim: Subjects.<SweptClaim>),
Case(id: "<family>.<kebab-statement>", run: () => Laws.<Combinator>(lawId: "<family>.<kebab-statement>", domain: <Domain>, tier: Tier.Default, …)),
```

`Case(id, run)` looks the declaration up by id, parses the tier, resolves each
member against the `Puck.Maths` assembly, and materializes each leg. A law whose
whole run is one `Laws.Claim` binds with `ClaimCase(id, claim)`, and one whose
whole run is one `Laws.SweptClaim` with `SweptCase(id, domain, width, claim)`:
both spell the id once and take the tier from the declaration, so the two cannot
disagree. Any other run is a `Case` lambda; repeat the id inside it — the
combinator quotes it on failure — and pass the tier the declaration names.

The registry is built in `LawRegistry`'s static constructor, after every
partial's `Domain` field is assigned, so a binding may capture a domain eagerly.

**A declaration without a binding, or a binding without a declaration, fails
`LawDeclarationTests.EveryDeclarationHasExactlyOneCase` (tier `Default`) and the
failure names the offending ids.** Every change already runs that tier, so
adding a law means editing two files, and forgetting the second is a named
failure, not a silent gap.

**Why it is data.** Nothing reads the bodies a leg describes, so a person
reading the legs is the only check on a leg that claims independence it does not
have; declarations in their own files stay readable, and a family can be edited
without contending for one C# file.

Where things live, and nothing crosses:

| File | Holds | Never holds |
|---|---|---|
| `laws/*.json` | ids, tiers, covered members, leg prose | anything executable |
| `LawRegistry.cs` and its per-family partials | domains and one binding per law (`ClaimCase`, `SweptCase` or `Case`) | legs, members, arithmetic, assertions |
| `LawDeclarations.cs` | the declaration records and their loader | law logic |
| `Subjects.cs` and its per-family partials, `*Claims.cs` | subject closures + claim bodies | oracle arithmetic |
| `Refusals.cs`, `DoublingTower.cs` | the refusal probes and the doubling-tower basis and lane readout every claim body shares | law-specific logic |
| `Oracles.cs` and its per-area partials | shared-nothing reference arithmetic | a `Puck.Maths` call |
| `Domains.cs`, `Frontier.cs` | operand sources | anything law-specific |
| `Laws.cs` | generic combinators, written once | per-subject logic |
| `Coverage.cs` | member surface, waivers, completeness check | law logic |
| `Legs.cs`, `LawLegs.cs` | leg vocabulary + gates | law logic |

**Ids.** `<family>.<kebab-statement>`, uniquely named in the declarations. The family
set is the distinct prefixes before that first dot — not the file list under
`laws/`, since a file groups cases by topic and a file's name need not match
the family its cases declare (a `core.*` case can live in a topic file named
for the type it covers, not for `core`). Derive the live set rather than
trusting a pasted list:

```
puck search -M 0 '"Id": "[A-Za-z0-9-]+\.' tests/Puck.Maths.Tests/laws -g "*.json"
```

Deep rows take the **tier** as their prefix instead: `deep.*`.
Reuse a family; do not invent one for a type that has one.

### The combinators (`Laws.cs`) — instantiate, never re-implement

| Combinator | Operand stream | Shape reported |
|---|---|---|
| `ScalarBinaryMatchesOracle(lawId, domain, tier, subject, oracle)` | `Pairs` | OracleAgreement |
| `ScalarMatchesOracle(…)` (norm) | `Pairs` | OracleAgreement |
| `BinaryMatchesOracle(…)` (element pair) | `Quads` | OracleAgreement |
| `VectorMatchesOracle(…, width, …)` | `Vectors` | OracleAgreement |
| `MobiusMatchesOracle(…, subject, oracleNumerator)` | `Pairs` | OracleAgreement |
| `TwinBinary` / `ScalarTwin` / `VectorTwin` / `VectorTernaryTwin` / `TwinPower` | Quads / Pairs / Vectors / Triples / Pairs×ladder | Twin (+ Witnessed) |
| `PureBinary` / `PureScalarBinary` | Quads / Pairs | SelfContained |
| `RoundTrip`, `IdentityElement`, `ConjugateSymmetry`, `NormMultiplicativity` | Pairs / Quads | SelfContained |
| `DivergenceCanary(…, fused, perProduct, minimumDivergences)` | `Vectors` | Divergence |
| `KnownFalse(lawId, counterexample)` | none (a fixed witness) | Divergence |
| `Claim(lawId, claim)` | none (own basis) | Claim |
| `SweptClaim(lawId, domain, tier, width, claim)` | `Vectors` | Claim |

Every twin's `witness` parameter is **required, not optional**. Pass the
independent third leg where one exists; pass `null` and the declaration must
admit the gap in a leg. That explicit null admits the gap in code rather than in
prose; do not add a defaulted overload.

Failures are self-reproducing: `Laws.Fail` prints
`{lawId} [{domain.Key}] seed={seed} k={index} {detail}`, and `Domain.Seed(k)`
is by construction the state the run's generator started from.

---

## 2. Tiers and budgets

Tier selection is declarative — **no environment variables anywhere**. Every
case carries its tier as the `tier` trait, and Deep and Exhaustive cases are
explicit, so plain `dotnet test` runs the Default tier.

| Tier | Selected by | Measured | When it runs |
|---|---|---|---|
| Default | a plain run | ordinary | **every change**, unconditionally |
| Deep | `--explicit on --filter-trait tier=Deep` | broader | before any rounding change lands |
| Exhaustive | `--explicit on --filter-trait tier=Exhaustive` | long | on demand or nightly; full-carrier sweeps and explicitly selected expensive combinatorial-prime checks |

Explicit cases never fire on a plain `dotnet test`, and without
`--explicit on` a `tier=Exhaustive` filter selects no test. Select an opt-in
tier with `--explicit on` and its trait, and narrow it with
`--filter-display-name <law-id>`. **Tier by COST, not by
the word "exhaustive."** `Exhaustive` is for sweeping every value of a carrier —
a 2³² word sweep qualifies; a 240×240 pair sweep is milliseconds and belongs at
`Default` or `Deep`. Parking a cheap case in an opt-in tier silently costs it its
everyday coverage. The expensive combinatorial-prime checks also use this tier
so high-bound counts and rank inversion do not extend the ordinary Deep run.
Their declarations name the finite operands and explicitly disclaim a
full-carrier proof; the tier is a scheduling choice, not an evidence upgrade.
Cases without a measured runtime state their estimated algorithmic work;
they do not claim a measured budget. `Exhaustive` is also the one tier whose cases must NOT consume
a `Domain`: a domain hands out a sample by construction. Use `Laws.Claim` with its own complete
basis there.

No law measures time; performance measurements belong to `puck bench`.

**Tier placement.**

- **Default** is the home tier. Choose another tier only for the stronger or
  cheaper execution contract described below; do not infer placement from the
  current inventory.
- **Deep rows** hold exact results over a domain Default does not reach: the
  exhaustive four-operand edge cross product (`Domains.Quads` switches to the
  full product only at Deep), a finite carrier swept whole, or a modulus,
  degree or band ladder longer than Default's. A Deep case that only draws
  more random operands (the 4096-draw batch against Default's 256) from its
  Default sibling's domain buys nothing and adds no line coverage; do not write
  one, and delete one you touch. Where a Deep row stays, say `MIRROR of <id>`
  and what it adds in the leg so the declaration reads honestly.
- There is no Smoke tier: a sentinel repeated a Default statement on a tiny
  domain and added neither evidence nor line coverage.

Deep and Default siblings normally share a domain key and deterministic seed.

---

## 3. Legs: what the statement stands on

A leg is a declared answer to "what does agreement here actually prove?".
In a `laws/*.json` row, fill the matching `Kind`, `Flavor` and prose slots; the
leg gate rejects an illegal combination (a flavorless shared-substrate leg, a
delegation twin with nothing cited, a canary with no absolute sibling) by name.

| Leg (`Kind` / `Flavor`) | Use when |
|---|---|
| Classical | the reference is independently authored (`Oracles`, or an exact inline expectation) and shares no code and no rounding substrate; a constant table is classical only with its provenance in `Citation`, otherwise it is a regression pin and a structural leg |
| PresentedTwin | the other side is the presented charged algebra object |
| InTreeIndependent | a second *shipped* implementation; cite the envelope it itself rests on in `Citation` |
| SharedSubstrate / FusedSubstrate | both sides round through a house fused rounding kernel — name it, and say identical member vs sibling copy |
| SharedSubstrate / SharedExactKernel | both sides call the same *exact* kernel; (B) is vacuous, cite where that kernel is pinned |
| SharedSubstrate / DelegationTwin | one side wraps the other — carriage only; cite the delegated-to kernel's own evidence |
| SharedSubstrate / Transcription | the reference transcribes the subject's own rule, or is built from its output; name the independent witness, or say in those words that none stands |
| SharedSubstrate / IntraPresented | both sides live inside the presented world |
| SharedSubstrate / SharedUpstream | both sides consume one upstream computation neither owns |
| Structural | purity, refusal, an identity element, a certificate shape, a measured floor |
| Structural with a `DOC GAP:` citation | behaviour diverges from the member's own XML doc |
| Structural with `Absolute` set | a relative canary: two disciplines **required to differ**; name the absolute sibling |

**Name evidence honestly, and name the shared thing.** On any
shared-substrate leg the `shared` field must say *what* is shared and *which*
— "`FixedQ4816.RoundProductSum`, BOTH overloads, the IDENTICAL member, not a
sibling copy" is the level of detail required; "they share rounding" is not.
The gate rejects an empty `shared`, but only a reviewer can reject a vague one.

**The trust vocabulary the leg prose uses** (three conditions; use these
words, they are what the declarations are read against):

- **(A) kernel envelope** — every kernel behaviour the leg leans on is
  independently pinned *over the regime this law uses*.
- **(B) substrate** — the shared rounding substrate is independently pinned, or
  not involved at all. An exact material makes (B) "drop out"; say so.
- **(C) independence of data** — no reference is derived from the subject it
  checks. A transcription is legal only when labelled as faithful carriage and
  never counted as independent evidence.

**Markers that reviewers search for** — spell them exactly:

- `ENVELOPE:` — what the law does *not* reach: an operand fold that saturates,
  a branch unreachable at legal operands, a host-specific path, a substituted
  divisor. An envelope that is real but unstated is the defect this marker
  exists to surface, so write it down.
- `OWED:` opening a citation — the evidence named is not there yet. This is
  the spelling used to find missing evidence in authored declarations.
- `MIRROR of <id> …` — a Deep row's Default sibling, with what the Deep row
  adds.
- `UNCERTAIN (sweep):` — a classification you are not sure of, with the
  alternative reading stated.

**Doc gaps.** When behaviour and the member's XML doc disagree, pin the
behaviour with a structural leg whose citation opens `DOC GAP:` and says what
the XML doc claims — never a plain structural leg. A doc-gap citation on another leg kind fails the leg check, as does one that
omits the documented claim. Close a gap by correcting the code or documentation
and updating the authored leg.

**What the leg gate checks.** Checks 1–2 run in
`LawLegTests.LawLegsAreDeclared` at tier `Default`; check 3 runs inside
`LawTests` on every case, so xUnit parallelism cannot make it vacuous.

1. `DeclarationViolations` — every statement names ≥ 1 leg; an agreement names
   what it stands against; a shared-substrate leg names what is shared; a
   delegation/shared-exact leg and an in-tree-independent leg carry a citation;
   a transcription names its witness; a canary names an absolute sibling; a
   doc-gap citation sits on a structural leg and says what the doc claims.
2. `UnresolvedSiblings` — a canary's absolute token resolves to a real law id
   or the honest `owed:<…>`. Nothing else resolves.
3. `ShapeViolation` — the declaration against the shapes the combinators
   actually reported: ran a twin or oracle agreement ⇒ declares an agreement
   leg; ran a twin **with a witness** ⇒ declares an *independent* leg; ran
   `DivergenceCanary` ⇒ declares a relative-canary leg.

**What they cannot check: whether a leg is TRUE.** Nothing reads the bodies the
strings describe. A leg declared classical that actually shares an algorithm
passes every gate. Only a reader who opens both bodies catches that: the
adversarial reviewer does it on review, and you do it when you write the leg.

---

## 4. Oracles

`Oracles.cs` is the **single home of reference arithmetic**, and it is
shared-nothing by construction.

- **Never call a `Puck.Maths` kernel from an oracle.** Every value is computed
  in `BigInteger` (or is an exact inline expectation / hand-derived constant
  table in `Subjects.cs`).
- **One rounding.** A returned raw is *one* ties-to-even rounding of the exact
  rational value of the ideal expression at the ideal scale, then wrapped to
  the carrier. `RoundRationalTiesToEven` is the module's one tie body and
  `WrapToUnsignedRaw` its one carrier reduction; `RoundDyadic`,
  `RoundDyadicUnsigned`, `RoundDyadicRatio` and `RoundToEvenUnits` are faces of
  them. Route through the faces so the signed and unsigned carriers cannot
  drift apart.
- **Prefer a different derivation route from the subject.** Agreement is worth
  most when the two sides could not fail together: the subject truncates a
  `UInt128` and rebuilds a correction from the discarded low word, so the
  oracle forms the whole product in arbitrary width and rounds once; the
  subject compares `r` against `d − r` because `2r` would leave its carrier, so
  the oracle compares `2r` against `d`; the subject takes a square root, so the
  oracle runs a bracketed integer search whose predicate is one exact squaring.
  Say the difference out loud in the leg.
- **Reuse, never duplicate.** Oracles may share their primitives with *each
  other* — that is deliberate. Before adding a rounding, a wrap or a decimal
  rendering, look for the existing one.
- **State the envelope.** Where an operand fold saturates, where a branch is
  unreachable at legal operands, where the tie is provably never hit — put it
  in the leg as `ENVELOPE:`. `unit-fraction16.div-vs-oracle` is the worked
  example: the equal-to-half branch is dead on both sides, so agreement pins
  the truncation, the round-up and the saturation only.

---

## 5. Domains

`Domains.cs` combines fixed edge batteries, a seeded `Pcg32XshRr` sample
and a stratified `DigitalNetSampler` window. Every run uses block index zero;
no sampling counter persists. `Domain.Key` identifies the domain and derives
its seed, while `Block` sets its stratified sample size. Default samples
256 random operands and Deep 4096; Deep also reaches the full edge cross
product where the combinator supports it. Keep exact-result assertions and
independent oracles when changing a domain.

## 6. Structural law completeness

The owner's ruling removes the coverage ratchet, its floor, recorded registers
and their generator. `LawCompletenessTests.EveryDeclaredMemberAndWaiverResolves`
checks declared references and waiver reasons against the current public surface.
`LawDeclarationTests` checks declaration/binding completeness. No check reads or
writes a recorded artifact. A whole-public-surface law check needs the uncovered
API classified first; the retired ratchet grandfathered it through saved state.
A new or renamed member lands with its law or a precise waiver in the same change.

Details that decide a declaration:

- A `CoverRef` resolves by **declaring type (generic definition) + name**, so
  it credits **all overloads** of that name at once. `LawCompletenessTests`
  fails a reference that resolves to no member — a typo cannot silently
  under-count.
- **Coverage is credited only from executed cases.** Every registry entry is a
  law `LawTests` runs and asserts.
- **Cite only what the case could catch.** A citation is a claim that the case
  would fail if the member were wrong. Citing a member from every case in a
  family records a breadth of coverage in declarations that the evidence does
  not support. A lane surface, for example, is cited only by cases that read
  lanes back and compare them lane for lane, and the registry says so at the
  declaration.
- **A waiver is a category argument, not an apology.** Write one shared reason
  per category and cite it verbatim from every member of it; name the gate of
  record that *does* pin the member (a law id, or an emulator battery stage).
  `Puck.Post` is quarantined in `experimental/` and is never a gate of record.
  An individual reason appears only where the category does not honestly fit.
- Presentation boundaries — the points where a value leaves the deterministic
  world for the renderer — are **not** automatically waivable: lane order and
  narrowing rounding are decidable in integers, so they take laws. A lossy map
  is still a decidable one.

## 7. Claim bodies

Claim bodies live in `Subjects.cs` and return **the counterexample text, or
`null` when the claim holds** — the assertion stays in `Laws`, so the registry
keeps its declaration-only shape.

```csharp
public static string? ThingIsExact() { … return null; }                    // Laws.Claim
public static string? ThingIsExact(long[] left, long[] right) { … }        // Laws.SweptClaim
```

`Laws.Claim` catches an exception and reports it as
`{lawId} threw {Type}: {message}`; `Laws.SweptClaim` does not — an exception in
a swept body propagates. Message text should name the operand and what it did
(`$"one is not a left identity at raw {value.Value}"`), not merely that
something failed.

House rules the build and the determinism contract impose:

- **No `stackalloc` inside a loop.** CA2014 is a build error here
  (`TreatWarningsAsErrors` + `CodeAnalysisTreatWarningsAsErrors` in
  `Directory.Build.props`). Hoist the span to method scope, or use a
  `static readonly` table.
- **Hoist tables and providers to `static readonly`.** Ladders, expectation
  tables and `NumberFormatInfo` instances (`Subjects.UnsignedSeparators`) are
  built once.
- **Pass an explicit provider — never ambient culture.** Every parse and format
  in a claim body names `CultureInfo.InvariantCulture` or its own
  `NumberFormatInfo`. (`InvariantGlobalization` is on, which is a floor, not a
  licence to omit the provider.)
- **No wall clock and no fresh randomness.** All draws come from the domain's
  seeded generator; no `DateTime.Now`, no `Random`, no `Guid`.
- **No floating-point arithmetic in law logic.** A boundary where a `double`
  appears is pinned by a hand-derived constant ladder compared on exact bit
  patterns (`BitConverter.DoubleToUInt64Bits`) against a `BigInteger` encoding
  assembled from the IEEE-754 *format* — computing an expectation in `double`
  would put float in the law.
- **Read declared constants into locals before comparing them**, so the
  comparison is one the run makes rather than one the compiler folds away —
  a folded comparison makes its counterexample unreachable.
- Assert refusals as part of the statement: the exception **type** and the
  **parameter name**, plus what the failed call left behind (`TryX` returned
  false *and* left `default`).

---

## 8. Validating that a new law actually bites

A green new case does not show it can fail. When that is in doubt, two
instruments help, cheapest first.

**Mutate in head.** Trace one wrong line in the kernel — flip the tie
direction, change the shift, drop the sign handling, swap two lanes, widen a
narrow gate — and ask whether *this* case's operand stream reaches an operand
where the difference is observable. If every operand it sweeps is degenerate
for the mutation (unit basis elements, zero lanes, a saturating fold), the leg
is decorative and its text must say so as an `ENVELOPE`.

**The mutation probe** — the empirical version, an optional tool:

1. Work in an **isolated worktree** (the harness's `EnterWorktree`, or
   `git worktree add`), so the broken kernel never meets another session's
   build. Never record during a probe: a plain run writes only to the build
   output.
2. Break the kernel the case claims to pin, **one line**.
3. Run the tier the case lives in.
4. **The new case should go red.** If it stays green, it does not bite — the
   operands, the fold, or the leg is wrong. Note the collateral too: which
   *other* cases went red tells you what the case adds over its neighbours (a
   mutation that reddens only mirrors of the same statement means the new case
   is a duplicate).
5. Restore the kernel, discard the worktree. Never commit probe artifacts.

Commands (`maths-usage` owns the routing; these are the ones a probe needs).
Build first with `dotnet build tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj
-c Release -nodeReuse:false`; `dotnet test` forwards switches it does not know
to the test host, which exits 5, so the tests run with `--no-build`:

```text
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --no-build
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --no-build --explicit on --filter-trait tier=Deep
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --no-build --explicit on --filter-trait tier=Exhaustive
```

For a fast single-case loop, filter on the display name — which *is* the law
id (`--filter-display-name '<law-id>*'`, with `--explicit on` for an opt-in tier).
The wildcard includes the runner's appended argument display; an exact bare id
selects no row. Confirm from the run output
what actually executed rather than assuming a filter composed with the tier
gate the way you expected. Run the unfiltered Default tier for structural checks.

**Reading a failure.** The enriched message carries the domain key, the derived
seed, the frontier index `k` and the raw operands, so it reproduces without
re-running the sweep: `Domain.Rng(k)` starts from exactly the reported seed.
A leg-gate failure names the statement, the leg number and its kind. A member-reference failure names the law and unresolved reference.

**What green means, and does not.** A green run means no probe *failed*. It
does not mean nothing *diverged*: a defect that turns a subject predicate
uniformly false can spin a search helper rather than trip an assertion, which
is why every unbounded search whose predicate is a subject carries a **step
budget whose exhaustion is a named failure**. Read the exit code *and* the
section output. And green says nothing about whether a leg's classification is
honest — no gate reads the bodies the leg text describes.

---

## 9. Rule 4 — a deliberate correction is expected to move values

Determinism pins the mapping, not the values: same input → bit-identical result
at a fixed code version, never output stability across versions. When you
correct a rounding, a fold or a claim:

- make the correction; do **not** preserve a wrong result to keep a value
  stable, and never add a path that reproduces old-wrong behaviour;
- re-run the relevant tier to prove determinism still holds (the gates are
  self-referential and pin no historical values);
- re-record any persisted replay or exact expectation ladder invalidated by the
  deliberate correction. A hand-derived ladder that no longer
  matches is either a correction to re-derive from the definition or a bug the
  law just caught; decide which and say so in the leg.
