---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to replace the plan's current reading with the new numbers rather than adding them beside or below the old ones as a history.
Example PASS: "Overwrite the Current reading with 14.2 ms and 341 s; the old figures go."

FAIL if the reply keeps the old reading in the plan (a table of windows, a 'previous' line, a strikethrough).
Example FAIL: "Add a row for this window under the previous one so the trend is visible."
