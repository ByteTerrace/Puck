---
type: llm
weight: 1
---

The response places the rewind at the firing's single commit point rather than in the effect.

PASS if it says the firing is one journal scope that the evaluator opens and closes through one commit (`RuleEvaluator.Commit` or "the one door"), which rewinds a refused or faulted firing, so the effect does not handle the refusal itself. Example: "Refused and faulted firings rewind in `RuleEvaluator.Commit`; your effect only records before writing."

FAIL if it has the new effect detect a later refusal and undo its own write, or does not say where a refused firing is rewound. Example: "Have the effect check whether the firing succeeded and remove its tally if not."
