---
name: maths-laws
description: "Authors Puck.Maths.Tests laws: laws/*.json rows, LawRegistry bindings, oracles, legs, waivers, the coverage ratchet, puck baselines maths-ledger. Primitive choice: maths-usage."
---

# Writing a Puck.Maths law that bites

This skill owns authoring and validating `tests/Puck.Maths.Tests`. The suite's
code and tests are authoritative; reconcile its README, XML documentation and
this skill with them when they disagree.

## What a law is for

`AGENTS.md` sets the owner's target for the suite: the smallest set of tests
that reaches 100% line coverage, plus a short keep-list of invariant tests that
coverage cannot see (determinism, exact fixed-point results). That shapes every
decision here.

- Write a law where running the change cannot show the behavior: an exact
  fixed-point result, a refusal or overflow boundary, a determinism property.
  CI requires 100% line coverage of live `src/`, so a new public member lands
  with a law (or an existing test) that reaches it.
- Author at the `Default` tier. A Deep row that only draws more random
  operands from its Default sibling's domain adds no evidence and no line
  coverage; write one only for a domain Default cannot reach (the exhaustive
  edge cross product, a whole finite carrier, a longer ladder), and delete a
  mirror you touch that does not.
- The suite's own machinery (the coverage ratchet, the leg gate, declaration
  parity, the recorded registers) was written by agents and is a tool, not a
  precondition. When it blocks a deletion, rename or restructure, change it in
  the same change.

## Required workflow

1. Declare a law in two places and nowhere else: the declaration (id, tier,
   covered members, legs) as a JSON row in
   `tests/Puck.Maths.Tests/laws/<family>.json`, and one binding in
   `LawRegistry.cs` or its per-family partial (`ClaimCase`, `SweptCase`, or
   `Case(id, run)` for any other run). A bare `[Fact]` runs but credits no
   member to the ratchet and declares no leg. A declaration without a binding,
   or a binding without a declaration, fails
   `LawDeclarationTests.EveryDeclarationHasExactlyOneCase` by name.
2. Choose the cheapest honest tier by measured cost, not by whether the word
   "exhaustive" fits. `Exhaustive` holds full-carrier sweeps and expensive
   finite combinatorial-prime checks, which take their own basis rather than a
   `Domain` and never imply a full-carrier proof.
3. State the claim, subject, operand domain and evidence legs explicitly.
4. Instantiate the existing `Laws.cs` combinators and shared domains instead of
   re-implementing their loops.
5. Give the claim independent evidence. A second expression of the same
   implementation is not an oracle: reference arithmetic lives in `Oracles.cs`,
   computed in `BigInteger`, and never calls a `Puck.Maths` kernel.
6. Classify every affected public Maths member in the coverage ratchet with a
   covering law (`Members` in the declaration) or a precise waiver
   (`Coverage.WaiverDeclarations`). The ratchet fails on every run for a member
   classified nowhere and cannot heal itself.
7. Regenerate the machine-owned registers with `puck baselines maths-ledger`
   and commit them; never hand-edit `coverage-manifest.json`, `leg-ledger.md`,
   `frontier.json` or `RESULTS.md`. A test run writes only to the build output.
8. When it is in doubt whether a new law can fail, apply a plausible one-line
   mutation in a worktree of your own and watch the law go red, or run
   `puck laws prove <law> --fix <commit>` (`--file-list` for an uncommitted
   fix). Most laws are rows of one theory, which `puck laws prove` cannot
   select one at a time; prove such a row with an exact mutation, failing at
   the intended assertion, and name the mutation in the commit. Never
   hand-revert files in a shared tree.

Deleting a law removes its JSON row, its binding and any oracle only it used,
then regenerates the registers. The frontier writer drops keys whose Domain
fields no longer exist in LawRegistry; active domains a filtered run skips
keep their counters.

## Run a tier

Build the test project with `-nodeReuse:false`, then run it:

```text
dotnet build tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release -nodeReuse:false
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --no-build
```

`dotnet test` forwards switches it does not know to the test host, which exits
5, so `-nodeReuse:false` belongs to the build step only. The plain run is the
Default tier (about half a minute) and includes the ratchet and leg gates,
which only a run that includes the Default tier checks. Deep and Exhaustive
cases are explicit:
`--explicit on --filter-trait tier=<tier>`, and without `--explicit on` such a
filter selects no test. One law runs with
`--explicit on --filter-display-name '<law-id>*'`; the trailing wildcard is
required.

## Load the full reference selectively

Read [references/complete-reference.md](references/complete-reference.md) for
the exact `Case` shape and combinators, tier budgets, the leg taxonomy and its
markers (`ENVELOPE:`, `OWED:`, `MIRROR of`, `DOC GAP:`), oracle standards,
domain construction, coverage categories and waivers, claim-body rules, the
mutation procedure and deliberate-correction rules.

## Route adjacent work

| Skill | Use when |
|---|---|
| [`maths-usage`](../maths-usage/SKILL.md) | Selecting a Maths primitive, applying consumer determinism rules, or deciding which verification tier an implementation change owes. |
| [`content-search`](../content-search/SKILL.md) | Finding law ids, declarations, member names or textual patterns across the suite. |
| [`symbol-analysis`](../symbol-analysis/SKILL.md) | Resolving semantic C# references, overloads, implementers or rename and deletion safety. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | Verifying a Maths change that reaches emulator code and its dedicated batteries. |
| [`verification`](../verification/SKILL.md) | The landing bar, optional checks beyond the law suite, and the CLI copy. |
