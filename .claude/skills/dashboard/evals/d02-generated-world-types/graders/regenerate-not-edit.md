---
type: llm
weight: 1
---

The response regenerates the file rather than editing it, and names the drift check.

PASS if it says the generated world types are written by a generator from the world document's schema (`puck schema`), must never be edited by hand, and that a check mode of the same verb (`--check`, run in CI) fails when the committed file no longer matches. Example: "Don't edit it: run `puck schema`, which rewrites the JSON Schemas and `worldDefinition.generated.ts` together, and commit both. CI runs `puck schema --check`, which fails on drift."

FAIL if it endorses the hand edit, even as a temporary fix, or names no way the drift is caught. Example: "Add the property by hand for now; regenerate later."
