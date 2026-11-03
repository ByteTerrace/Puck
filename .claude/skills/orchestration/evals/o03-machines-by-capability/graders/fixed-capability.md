---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply plans machines by what each permanently has (CPU class, GPU, total memory, OS, what it can run) and not by the free memory or disk it happens to have at this instant.
Example PASS: "Choose by fixed capability: the GPU leg goes where the GPU is; the free RAM reading right now will change once the leg starts."

FAIL if the reply places the build or the leg on the machine with the most free RAM at this moment.
Example FAIL: "The GPU machine has more free RAM right now, so run the build there too."
