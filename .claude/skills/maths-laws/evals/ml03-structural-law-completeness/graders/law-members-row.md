---
type: llm
weight: 1
---

The response classifies `FixedVector3.ProjectOnto` by naming it in the `Members` list of a law declaration in `laws/<family>.json`, with a matching binding in `LawRegistry`.

PASS example: "Add a JSON row in `tests/Puck.Maths.Tests/laws/<family>.json` with `Members` listing `FixedVector3.ProjectOnto`, and exactly one binding in `LawRegistry.cs`."

FAIL if the arithmetic method is waived, covered by a bare test attribute, or listed somewhere other than a law declaration's `Members`. Example: "Add a waiver for `ProjectOnto` until the law exists."
