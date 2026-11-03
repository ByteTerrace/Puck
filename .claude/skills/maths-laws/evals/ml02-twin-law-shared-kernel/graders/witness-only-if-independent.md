---
type: llm
weight: 1
---

The response supplies a witness leg only when it is genuinely independent of the kernel, and otherwise admits in the declaration that no independent witness stands.

PASS if it says a witness leg is added only if independent of `CarrylessMultiply64`; otherwise the declaration says no independent witness exists. Example: "Add a witness only if it doesn't run the kernel; if none exists, say so in the declaration."

FAIL if it names a witness that runs the same kernel, or hides that no independent witness exists. Example: "Add a witness leg that calls `CarrylessMultiply64` directly and compare."
