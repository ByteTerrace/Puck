---
type: llm
weight: 1
---

The response treats the hand-maintained surfaces that still describe the old `IndentSize` contract (project README, XML comments, inline schema comment, owning skill's run recipe) as part of this change, updated in the same change.

PASS if it says all four hand-maintained surfaces are updated in this change (they are staleness the change created), without asking the owner. Example: "All four are yours to update now: the XML comments, the README passage, the inline schema comment and the skill's run recipe."

FAIL if it calls them optional cleanup, defers them to a later change, or updates only some of them. Example: "Update the XML comments; the README, comment and skill recipe can wait for a docs pass."
