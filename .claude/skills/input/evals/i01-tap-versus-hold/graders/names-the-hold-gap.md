---
type: llm
weight: 1
---

The response identifies that a hold which never releases produces no closing event for a pattern step to match.

PASS if it says the pattern matcher matches events only, so a hold of about a second with no release cannot be completed by a pattern step alone, and that the duration needs a time condition (a guard such as `inputAge` or `inputHeldSince` in the reading rule, or a new matcher term satisfied by elapsed time while still held). Example: "A pattern step needs an event, and a held button sends none after its press, so either the rule adds an `inputAge >= 1s` guard or the matcher gains a held-for-time term."

FAIL if it presents the hold as fully expressible as ordinary pattern steps, or names no gap. Example: "Author the hold as a press step with `maximumDurationSeconds: 1`, and the matcher fires it after one second."
