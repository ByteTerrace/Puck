---
type: llm
weight: 1
---

PASS if the reply states the invariant the cache rests on: a cached program is valid only while the rule document (the inputs) it was compiled from is unchanged.
Example PASS: "Invariant: a cached program must match the current contents of its rule document."

FAIL if the reply describes the cache and its speedup without stating what must stay true for a cached entry to be correct.
Example FAIL: "Added `RuleProgramCache`, keyed by path; ticks dropped from 0.9 ms to 0.2 ms; `world.reload` clears it."
