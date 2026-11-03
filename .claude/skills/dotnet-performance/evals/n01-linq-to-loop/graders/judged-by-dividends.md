---
type: llm
weight: 1
---

The response keeps the rewrite only if it wins on a measured metric (time, bytes or work counters) or simplifies, unifies or opens a design the next step builds on, and drops it when it does neither.

PASS if it states that keep-or-drop rule, in any words (a small measured gain stays when it simplifies; a rewrite that moves nothing and adds nothing is dropped). Example: "Keep the loop if the bytes drop or it simplifies the code; if neither moves, revert it."

FAIL if it keeps the rewrite regardless of the result, or has no keep-or-drop decision. Example: "Rewrite it as a loop and ship it; hand-written loops are always better."
