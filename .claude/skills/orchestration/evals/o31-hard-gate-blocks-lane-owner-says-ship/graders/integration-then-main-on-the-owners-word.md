---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply lands the work on the integration branch and gates the move to `main` on the owner (the owner pushes, or the lead pushes on the owner's word) once the milestone's riskiest areas have had a fresh fix pass and their invariant checks ran (for a determinism change, `puck test --reproduce`), with any high finding from those passes fixed or deferred by the owner.
Example PASS: "Land it on the integration branch now; `main` gets it when the owner pushes, once the riskiest areas had a fresh fix pass and the reproduce check ran."

FAIL if the reply pushes the lane to `main` directly, or skips the fix-pass and invariant-check gate before `main`.
Example FAIL: "The owner said ship it, so merge the lane straight to `main` now."
