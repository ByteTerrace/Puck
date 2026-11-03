---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says only one GPU leg runs on a GPU at a time.
Example PASS: "One GPU leg per GPU at a time; a second waits or goes to another GPU."

FAIL if the reply starts a second GPU leg on the same GPU, or does not limit GPU legs per GPU.
Example FAIL: "Run both GPU legs on the GPU at once to save time."
