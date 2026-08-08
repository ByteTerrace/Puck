# Maths-laws complete reference

This preserves the full declaration, evidence, coverage, and mutation-proof
contract. Read the relevant section when the compact skill routes here.

## Contents

- [Declaration-first cases](#1-declaration-first-there-is-one-shape)
- [Tiers and budgets](#2-tiers-and-budgets)
- [Legs](#3-legs-what-the-statement-stands-on)
- [Oracles](#4-oracles)
- [Domains](#5-domains)
- [Coverage ratchet](#6-the-coverage-ratchet)
- [Claim bodies](#7-claim-bodies)
- [Mutation proof](#8-validating-that-a-new-law-actually-bites)
- [Deliberate corrections](#9-rule-4--a-deliberate-correction-is-expected-to-move-values)

This skill is **factual and procedural only**: how the suite is shaped, what a
declaration owes, and how to prove a new law discriminates. It does not design
the kernel under test. The user's current instruction outranks it — if it
argues against a change you were asked to make, it is stale; update it in the
same change and say so.

Sources of record, all more detailed than this file:
[tests/Puck.Maths.Tests/README.md](../../../../tests/Puck.Maths.Tests/README.md)
(modules, tiers, ratchet, frontier, RESULTS) and the XML docs on `Legs.cs`,
`Laws.cs`, `Domains.cs`, `Coverage.cs` and `LegLedger.cs`. When one of those
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
  "Tier": "Smoke|Default|Deep|Exhaustive|Bench",
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
is `SharedSubstrate`. The seven prose slots map onto the `Legs.cs` factories:

| Factory | Slots it fills |
|---|---|
| `Classical(subject, against)` | Subject, Against |
| `PublishedConstant(subject, table, provenance)` | Subject, Against=table, Citation=provenance |
| `InTreeIndependent(subject, against, envelope)` | + Citation=envelope |
| `FusedSubstrate` / `IntraPresented` / `SharedUpstream(subject, against, shared)` | + Shared |
| `SharedExactKernel` / `DelegationTwin(subject, against, shared, envelope)` | + Shared, Citation=envelope |
| `FaithfulCarriage(subject, against, transcribes, witness)` | Shared=transcribes, Citation=witness |
| `Structural(statement)` | Subject=statement |
| `PinnedAsObserved(statement, documented)` | Subject, Citation carries the `DOC GAP:` marker |
| `RelativeCanary(subject, against, absolute)` | Kind=Structural, + Absolute |

**The binding** — the one thing JSON cannot carry — is a line in
`LawRegistry.Build()`:

```csharp
Case("<family>.<kebab-statement>", () => Laws.<Combinator>(lawId: "<family>.<kebab-statement>", domain: <Domain>, tier: Tier.Default, …)),
```

`Case(id, run)` looks the declaration up by id, parses the tier, resolves each
member against the `Puck.Maths` assembly, and materializes each leg. Repeat the
id inside the run lambda — the combinator quotes it on failure.

**A declaration without a binding, or a binding without a declaration, fails
`LawDeclarationTests.EveryDeclarationHasExactlyOneCase` (tier `Default`) and the
failure names the offending ids.** That gate is what replaced the old
compile-time guarantee: when declarations lived in C#, a case could not exist
without its legs because it would not compile. Now the check is a Default-tier
assertion — which every change already runs, thirteen seconds in — rather than a
compiler error. Adding a law means editing two files, and forgetting the second
is a named failure, not a silent gap.

**Why it is data.** The leg text is the ONE thing no gate can check — nothing
reads the bodies a leg describes, so a leg claiming independence it does not have
passes everything. That makes a human reading legs the only defence the property
has, and four-hundred-character string literals buried among generic combinators
in a six-thousand-line file defeat that reading. It also means a family can be
reviewed and edited on its own, and that several authors can add laws at once
without contending for one file.

Where things live, and nothing crosses:

| File | Holds | Never holds |
|---|---|---|
| `laws/*.json` | ids, tiers, covered members, leg prose | anything executable |
| `LawRegistry.cs` | domains and one `Case(id, run)` binding per law | legs, members, arithmetic, assertions |
| `LawDeclarations.cs` | the declaration records and their loader | law logic |
| `Subjects.cs`, `*Claims.cs` | subject closures + claim bodies | oracle arithmetic |
| `Oracles.cs` | shared-nothing reference arithmetic | a `Puck.Maths` call |
| `Domains.cs`, `Frontier.cs` | operand sources | anything law-specific |
| `Laws.cs` | generic combinators, written once | per-subject logic |
| `Coverage.cs` | member surface, waivers, ratchet | law logic |
| `Legs.cs`, `LegLedger.cs` | leg vocabulary + gates | law logic |

**Ids.** `<family>.<kebab-statement>`, ordinal-sorted in the ledger. Families
in use: `scalar`, `unsigned-scalar`, `closed-unit`, `unit-fraction16`,
`unit-fraction32`, `integer`, `complex`, `split`, `dual`, `quaternion`,
`vector`, `rigid`, `position`, `rate`, `algebra`, `mobius`, `presented`,
`certified`, `quasicrystal`, `sampling`, `core`, `binary-field`, `polynomial`,
`prime-field`, `reed-solomon`. Mirrors take the **tier** as their prefix instead:
`smoke.*` and `deep.*`. Reuse a family; do not invent one for a type that has
one.

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

Tier selection is declarative — **no environment variables anywhere**. The
project binds `default.runsettings` via `RunSettingsFilePath`
(`TestCaseFilter` = `tier!=Deep&tier!=Bench`), so plain `dotnet test` runs
Smoke + Default.

| Tier | Selected by | Declared budget | When it runs |
|---|---|---|---|
| Smoke | `--settings tests/Puck.Maths.Tests/smoke.runsettings` | < 2 s | a tight inner loop; carries **no new evidence** by construction |
| Default (Smoke + Default) | the bound default | ~13 s | **every change**, unconditionally |
| Deep | `--settings …/deep.runsettings` | minutes | before you commit, and before any rounding change lands |
| Exhaustive | `--settings …/exhaustive.runsettings` | long | on demand or nightly; full-width sweeps over an ENTIRE carrier |
| Bench | `--settings …/bench.runsettings` | timing, breach-tolerant | on demand; gates no value |

`default.runsettings` filters `tier!=Deep&tier!=Bench&tier!=Exhaustive`, so the
three opt-in tiers never fire on a plain `dotnet test`. **Tier by COST, not by
the word "exhaustive."** `Exhaustive` is for sweeping every value of a carrier —
a 2³² word sweep qualifies; a 240×240 pair sweep is milliseconds and belongs at
`Default` or `Deep`. Parking a cheap case in an opt-in tier silently costs it its
everyday coverage. `Exhaustive` is also the one tier whose cases must NOT consume
a `Domain`: a domain hands out a sample by construction, and consuming one would
advance the frontier counter its Default sibling reads, sliding that sibling's
operands as a side effect of a sweep having run. Use `Laws.Claim` with its own
basis there.

The budgets are **declared design targets** (`Tier`'s XML docs + the project
README), not enforced by any assertion, and **nothing records what a session
cost** — `RESULTS.md` carries no duration, by design, because every figure in
it is machine-independent and a wall time is not (see that file's own header).
Treat a budget as the ceiling your new case must not blow through, and if you
want to know what it costs, time the run yourself on an idle machine and
compare it only against that same machine. Cost across machines is the bench
tier's business: a ratio against a per-machine baseline, with a busy-machine
guard that records nothing when the environment is suspect.

**Tier placement.**

- **Default** is the home tier. Choose another tier only for the stronger or
  cheaper execution contract described below; do not infer placement from the
  current inventory.
- **Deep mirrors** run the *same statement at strictly stronger operands* —
  the exhaustive four-operand edge cross product (`Domains.Quads` switches to
  the full product only at Deep) and a 4096-draw random batch. A Deep case
  whose operands are not strictly stronger than its Default sibling's is
  buying nothing. Say `MIRROR of <id> at strictly stronger operands` in the leg
  so the ledger reads honestly.
- **Smoke sentinels** mirror the **hottest kernel** of a family on
  `SmokeDomain` (block 64, 16 random draws). A smoke row carries *no new
  evidence* and its leg says so: `MIRROR of <id> at SmokeDomain: no new
  evidence`. Add one only when the kernel is genuinely the family's hot path.

Deep and Default siblings normally share a domain **key**, so they advance one
frontier counter together.

---

## 3. Legs: what the statement stands on

A leg is a declared answer to "what does agreement here actually prove?".
`Legs.cs`'s factories are the only constructors — illegal combinations cannot
be spelled.

| Factory | Kind | Use when |
|---|---|---|
| `Leg.Classical(subject, against)` | classical | the reference is independently authored (`Oracles`, or an exact inline expectation) and shares no code and no rounding substrate |
| `Leg.PublishedConstant(subject, table, provenance)` | classical | the reference is a constant table; `provenance` is **required** and is what makes it classical rather than a regression pin |
| `Leg.PresentedTwin(subject, against)` | presented-twin | the other side is the presented charged algebra object |
| `Leg.InTreeIndependent(subject, against, envelope)` | in-tree-independent | a second *shipped* implementation; cite the envelope it itself rests on |
| `Leg.FusedSubstrate(subject, against, shared)` | shared-substrate | both sides round through a house fused rounding kernel — name it, and say identical member vs sibling copy |
| `Leg.SharedExactKernel(subject, against, shared, envelope)` | shared-substrate | both sides call the same *exact* kernel; (B) is vacuous, cite where that kernel is pinned |
| `Leg.DelegationTwin(subject, against, shared, envelope)` | shared-substrate | one side wraps the other — carriage only; cite the delegated-to kernel's own evidence |
| `Leg.FaithfulCarriage(subject, against, transcribes, witness)` | shared-substrate | the reference transcribes the subject's own rule, or is built from its output; name the independent witness, or say in those words that none stands |
| `Leg.IntraPresented(subject, against, shared)` | shared-substrate | both sides live inside the presented world |
| `Leg.SharedUpstream(subject, against, shared)` | shared-substrate | both sides consume one upstream computation neither owns |
| `Leg.Structural(statement)` | structural | purity, refusal, an identity element, a certificate shape, a measured floor |
| `Leg.PinnedAsObserved(statement, documented)` | structural | behaviour diverges from the member's own XML doc |
| `Leg.RelativeCanary(subject, against, absolute)` | relative-canary | two disciplines **required to differ**; name the absolute sibling |

**Name evidence honestly, and name the shared thing.** On any
shared-substrate leg the `shared` field must say *what* is shared and *which*
— "`FixedQ4816.RoundProductSum`, BOTH overloads, the IDENTICAL member, not a
sibling copy" is the level of detail required; "they share rounding" is not.
The gate rejects an empty `shared`, but only a reviewer can reject a vague one.

**The trust vocabulary the leg prose uses** (the campaign's three conditions —
use these words, they are what the ledger's registers are read against):

- **(A) kernel envelope** — every kernel behaviour the leg leans on is
  independently pinned *over the regime this law uses*.
- **(B) substrate** — the shared rounding substrate is independently pinned, or
  not involved at all. An exact material makes (B) "drop out"; say so.
- **(C) independence of data** — no reference is derived from the subject it
  checks. A transcription is legal only when labelled as faithful carriage and
  never counted as independent evidence.

**Markers that the registers and reviewers key on** — spell them exactly:

- `ENVELOPE:` — what the law does *not* reach: an operand fold that saturates,
  a branch unreachable at legal operands, a host-specific path, a substituted
  divisor. An envelope that is real but unstated is the defect this marker
  exists to surface, so write it down.
- `OWED:` opening a citation — the evidence named is not there yet. This is
  the one spelling the owed-marker register keys on; a gap named in any other
  words is invisible to it.
- `MIRROR of <id> …` — a tier mirror, with what it adds (or that it adds
  nothing).
- `UNCERTAIN (sweep):` — a classification you are not sure of, with the
  alternative reading stated.

**Doc gaps.** When behaviour and the member's XML doc disagree, pin the
behaviour with `Leg.PinnedAsObserved(statement, documented)` — never a plain
`Leg.Structural`. It writes the `DOC GAP:` citation the ledger's
*behaviour pinned as observed* register is derived from, and `documented` is
required (an empty one throws at the factory). The register closes only by
**correcting the code or the doc and re-spelling the leg**; editing
`leg-ledger.md` closes nothing — it is regenerated on the next run. The
tool-side label grammar has no token for this, so a doc gap can only be spelled
in `LawRegistry.cs`.

**What the leg gate checks.** Checks 1–2 run in
`LegLedgerTests.LawLegsAreDeclared` at tier `Default`; check 3 runs inside
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

`Domains.cs` is the only operand source; **no law generates its own operands**.
Each law declares a `Domain` and receives three streams from it:

1. the **edge battery** — `EdgeRaws` mapped through the domain: the committed
   24-raw set of extremes and off-by-ones around every boundary the kernels
   branch on (`0, ±1`, both carrier extremes, `±2³¹` and `±2⁴⁷` each with both
   off-by-ones, `±65536`, `±32768`, `±256`). Exhaustive square for pairs; for
   quads, a budget-bounded rotation at Smoke/Default and the **full
   four-operand cross product at Deep**.
2. the **edge-biased random batch** — `Pcg32XshRr` seeded purely from the key
   and the frontier counter; 16 draws at Smoke, 256 at Default, 4096 at Deep.
3. the **frontier block** — contiguous stratified `DigitalNetSampler` indices
   mapped by `Domains.FrontierRaw`.

```csharp
private static readonly Domain Thing = new(Key: "thing", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
```

`Key` is the persisted frontier key; `Block` the sample indices consumed per
run; the two fractions steer the random mixture; `SublatticeShift` (when
non-zero) folds every operand onto `2^shift` multiples bounded by an odd span,
so pairwise products are exact and a rounding-free law can hold bit-for-bit.

- **A new domain needs no code outside its declaration.** `Frontier.Consume`
  registers an unseen key on first use and the ledger writes it into
  `frontier.json`. Do not hand-add an entry.
- **Give a new law its own key** unless you deliberately want the shared-operand
  bracket: cases sharing a key in one run read the *same* index and therefore
  sweep bit-identical operands (some legs lean on exactly that, and say so).
  The counter advances by **one per consuming GREEN run**, whatever the case
  count. The advance is green-gated at PERSISTENCE: a session in which any law
  failed writes `frontier.json` not at all, for no key, so re-running from the
  committed state reproduces the red at the same indices instead of sliding the
  window past it. Consumption is unaffected — a domain still hands out its index
  mid-sweep, so operand determinism within a run does not depend on how the run
  ends. The non-law gates (ratchet, leg gate, bench) do not gate it and
  need not: they consume no domain, so their verdicts sweep no operands and
  reproduce identically whatever the counters say.
- **Fold at the domain only when the whole point is that nothing rounds**
  (the sublattice bands). Otherwise fold inside the **subject and the oracle
  alike** — the `Subjects.ClosedUnitRaw` / `Subjects.UnsignedRaw` pattern —
  rather than shrinking the sampler, and declare the fold's bias as an
  `ENVELOPE`.
- Tune `EdgeFraction` down when every subject folds its operand onto a band
  (see `scalar-transcendental` at 0.25): an edge-heavy mixture would otherwise
  spend its draws on a handful of folded images.
- **The vector buffers are REUSED across iterations.** `Domains.Vectors` and
  `VectorTriples` yield the same arrays each step. A claim body that wants to
  retain one must copy it.

---

## 6. The coverage ratchet

`CoverageManifestTests.ManifestRatchetHolds` (tier `Default`) reconciles the
reflected public `Puck.Maths` surface against `coverage-manifest.json`. It
fails when a member is **classified nowhere** — no committed state, no covering
law, no waiver — or when one moved covered→uncovered. It never fails on the
initial uncovered backlog: coverage only grows.

**The workflow when you add or rename a public member:**

1. Land it **with its classification in the same change**: a law case naming it
   in `Members` via a `CoverRef(typeof(T), "MemberName")`, **or** an entry in
   `Coverage.WaiverDeclarations` with a reason.
2. Run the Default tier. Commit the regenerated artifacts.

Land it with neither and the gate fails on **every** run and cannot heal
itself: `Coverage.Generate` never writes a state for a member the committed
manifest does not already mention, so the failing run cannot record it as
uncovered.

Details that decide a declaration:

- A `CoverRef` resolves by **declaring type (generic definition) + name**, so
  it credits **all overloads** of that name at once. `CoverageManifestTests`
  fails a reference that resolves to no member — a typo cannot silently
  under-count.
- **Coverage is credited only from executed cases.** Every registry entry is a
  law `LawTests` runs and asserts; the bench has no registry declaration, so
  timing work never counts.
- **Cite only what the case could catch.** A citation is a claim that the case
  would fail if the member were wrong. Citing a member from every case in a
  family records a breadth of coverage in the manifest that the evidence does
  not support — `QuaternionLaneSurface` is cited only by cases that read lanes
  back and compare them lane for lane, and the registry says so at the
  declaration.
- **A waiver is a category argument, not an apology.** Write one shared reason
  per category and cite it verbatim from every member of it; name the gate of
  record that *does* pin the member (a battery section, a Post stage). An
  individual reason appears only where the category does not honestly fit.
- Presentation boundaries — the points where a value leaves the deterministic
  world for the renderer — are **not** automatically waivable. Three of them
  were waived as "presentation-only" and were wrong: lane order and narrowing
  rounding are decidable in integers, so they are laws now. A lossy map is
  still a decidable one.

**Never hand-edit `coverage-manifest.json`, `leg-ledger.md`, `RESULTS.md` or
`frontier.json`.** All four are machine-written and every write is
execution-gated: the manifest belongs to the ratchet gate, the leg ledger to
the leg gate, each `RESULTS.md` tier block to that tier, the frontier to
runs that consumed domains **and stayed green**. A filtered or single-tier run
therefore leaves every other record exactly as its own last run left it — and
can never quiet a gate it never ran. Because the ratchet and leg gate carry the
`Default` trait, **only a run that includes the Default tier
regenerates the manifest and the ledger**; a Smoke-only or Deep-only run will
not. Commit whatever the run regenerates as part of your change.

---

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

A green new case proves nothing by itself. Two instruments, cheapest first.

**Mutate in head.** Trace one wrong line in the kernel — flip the tie
direction, change the shift, drop the sign handling, swap two lanes, widen a
narrow gate — and ask whether *this* case's operand stream reaches an operand
where the difference is observable. If every operand it sweeps is degenerate
for the mutation (unit basis elements, zero lanes, a saturating fold), the leg
is decorative and its text must say so as an `ENVELOPE`.

**The mutation probe** — the empirical version, and the one to use before
claiming a new law verified:

1. Work in an **isolated worktree** (the harness's `EnterWorktree`, or
   `git worktree add`). A green run advances `frontier.json`, and any run
   rewrites `RESULTS.md`, so probing in the live tree dirties committed
   artifacts.
2. Break the kernel the case claims to pin, **one line**.
3. Run the tier the case lives in.
4. **The new case must go red.** If it stays green, it does not bite — the
   operands, the fold, or the leg is wrong. Note the collateral too: which
   *other* cases went red tells you what the case adds over its neighbours (a
   mutation that reddens only mirrors of the same statement means the new case
   is a duplicate).
5. Restore the kernel, discard the worktree. Never commit probe artifacts.

Commands (`maths-usage` owns the routing; these are the ones a probe needs):

```text
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/exhaustive.runsettings
```

A CLI `--settings` overrides the bound default. For a fast single-case loop,
filter on the display name — which *is* the law id. Confirm from the run output
what actually executed rather than assuming a filter composed with the tier
gate the way you expected, and remember that a filtered run regenerates only
the artifacts of the checks it ran; the run that produces what you commit is
the unfiltered Default tier.

**Machine gotcha — `-c Release` must PRECEDE the file path** in any file-based
`dotnet run <script>.cs`. Put it after and the script is silently built and run
as Debug; Windows App Control then blocks loading the never-seen Debug binary
(`FileLoadException 0x800711C7`) and the tool fails outright. Release outputs
load cleanly.

**Reading a failure.** The enriched message carries the domain key, the derived
seed, the frontier index `k` and the raw operands, so it reproduces without
re-running the sweep: `Domain.Rng(k)` starts from exactly the reported seed.
A leg-gate failure names the statement, the leg number and its kind. A ratchet
failure lists the unclassified ids verbatim — `MemberSurface` ids, carrying the
declaring type and the full parameter list, so each names exactly the
`CoverRef(typeof(T), "Name")` (or waiver) it is missing.

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
- **re-record what the correction invalidated in the same change** — the
  regenerated manifest, ledger, `RESULTS.md`, frontier, and any expectation
  ladder whose values genuinely changed. A hand-derived ladder that no longer
  matches is either a correction to re-derive from the definition or a bug the
  law just caught; decide which and say so in the leg.
