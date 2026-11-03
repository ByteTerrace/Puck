---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply merges the new integration head into each open lane, and has each lane built and shown working on the merged head before it lands.
Example PASS: "Each open lane merges the landed head, resolves its conflicts (regenerating the generated files), rebuilds and shows its change working before it lands."

FAIL if the reply lands the open lanes without merging the new head into them, or lands them without rebuilding on the merged head.
Example FAIL: "The other lanes passed their gates before the first landed, so land them as they are."
