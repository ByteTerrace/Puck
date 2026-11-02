---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if any `principal` lane the reply proposes is a design review of the hashing approach, and the reply gives `principal` no reproducing, measuring, building or bisecting.
Example PASS: "`principal` reviews whether the hash covers what drives play; `specialist` or Astra reproduces and bisects."

FAIL if the reply gives `principal` any of the diagnosis: reproducing, measuring, comparing traces, building or bisecting.
Example FAIL: "`principal` takes the hash hunt: reproduce on both machines and bisect to the field."
