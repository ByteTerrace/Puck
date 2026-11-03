---
type: llm
weight: 1
---

The response leaves the `docs/development/ci.md` line unfixed in this change and says it goes in the final report with its file and line.

PASS if it answers no, the line is not fixed in this change even though it is one line, and says to record it in the final report as `docs/development/ci.md:88` with what is stale. Example: "No: ci.md is not part of this change, so leave it and list `docs/development/ci.md:88` under Found."

FAIL if it says to fix the ci.md line in this change, or to leave it without reporting it. Example: "Yes, it is one line, fix it while you are here."
