---
type: llm
weight: 1
---

The response says an alias-table sample takes two generator advances on almost every draw but can take more (an exact wide draw on one word in 2^32), and that it reports the advances it consumed, so a seek adds the reported count rather than a fixed two.

PASS example: "The alias sample is two advances plus a rare exact wide draw; it returns `advances`, so sum those instead of assuming two."

FAIL if it calls the alias sample exactly two advances with no exception, calls it a rejection-heavy variable cost with no reported count, or never states its cost. Example: "An alias sample is always exactly two advances, so it is safe to skip with `Advance`."
