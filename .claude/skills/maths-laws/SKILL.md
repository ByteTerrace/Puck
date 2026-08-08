---
name: maths-laws
description: Authors and validates tests/Puck.Maths.Tests law cases, subjects, oracles, domains, legs, waivers, claims, coverage classifications, and mutation proofs. Use for any edit under that project; when adding, renaming, or changing a public Puck.Maths member; or when pinning a Maths documentation and behavior divergence. Enforces declaration-first cases, honest independent evidence, generated-register ownership, tier budgets, and the coverage ratchet. maths-usage owns choosing primitives and routine tier routing; engine and emulator gates are out of scope.
---

# Writing a Puck.Maths law that bites

This skill owns authoring and validating `tests/Puck.Maths.Tests`. The suite's
current implementation and tests are authoritative; reconcile its README, XML
documentation, and this skill with them when guidance disagrees.

## Required workflow

1. Declare every law in TWO places and nowhere else: the declaration — id, tier,
   covered members, legs — as a JSON row in `tests/Puck.Maths.Tests/laws/<family>.json`,
   and one `Case(id, run)` binding in `LawRegistry.cs`. Never add an isolated
   `[Fact]` that bypasses registration. A declaration without a binding, or a
   binding without a declaration, fails the Default-tier parity gate by name.
2. Choose the cheapest honest tier and stay within its budget — by measured COST,
   not by whether the word "exhaustive" fits. `Exhaustive` means every value of a
   carrier, and its cases take their own basis rather than consuming a `Domain`.
3. State the claim, subject, operand domain, and evidence legs explicitly.
4. Use the existing `Laws.cs` combinators and shared domains rather than
   reimplementing their loops.
5. Give the claim independent evidence. A second expression of the same
   implementation is not an oracle.
6. Classify every affected public Maths member in the coverage ratchet with a
   real law or a precise waiver.
7. Regenerate machine-owned registers and reports; never hand-edit them.
8. Prove the new law bites by applying a plausible mutation, observing the
   intended failure, restoring the implementation, and observing the pass.

## Load the full reference selectively

Read [references/complete-reference.md](references/complete-reference.md) for
the exact `Case` shape and combinators, tier budgets, leg taxonomy, oracle
standards, domain construction, coverage categories and waivers, claim-body
rules, mutation procedure, or deliberate-correction rules.

## Route adjacent work

| Skill | Use when |
|---|---|
| [`maths-usage`](../maths-usage/SKILL.md) | Selecting a Maths primitive, applying consumer determinism rules, or deciding which verification tier an implementation change owes. |
| [`content-search`](../content-search/SKILL.md) | Finding law ids, declarations, member names, or textual patterns across the suite. |
| [`symbol-analysis`](../symbol-analysis/SKILL.md) | Resolving semantic C# references, overloads, implementers, or rename and deletion safety. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | Verifying a Maths change that reaches emulator code and its dedicated batteries. |

No repository skill currently owns the quarantined engine or document gates.
