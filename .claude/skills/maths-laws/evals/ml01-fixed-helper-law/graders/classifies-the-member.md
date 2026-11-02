---
type: llm
weight: 1
---

The response lists the new public `FixedQ4816` helper in the law declaration's `Members`, so the structural law-completeness check counts it as covered.

PASS example: "Add one row to `laws/<family>.json` with the id, `Default` tier, `Members` listing the new helper so the completeness check credits it, and its legs."

FAIL if the member is never named in the declaration, or is classified only by a test attribute or a waiver. Example: "Add a `[Fact]` that calls the helper and compares it with the oracle."
