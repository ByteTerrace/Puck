---
type: llm
weight: 1
---

The response says a surface that could not be updated or synchronized in this run is reported (in the hand-back, or as an open item) rather than left silently stale.

PASS if it names a surface that may stay stale in this run (typically the generated docfx pages) and says to report it, with the verb that would refresh it, or to record it. Example: "If the docfx pages cannot be regenerated here, say so under Found and name what regenerates them."

FAIL if it never says what to do with a surface that cannot be updated, or says to leave it without telling anyone. Example: "Update what you can; the generated pages will sort themselves out."
