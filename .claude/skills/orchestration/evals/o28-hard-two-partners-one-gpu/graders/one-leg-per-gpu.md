---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says only one GPU leg runs on a GPU at a time, so one of the two legs stops or waits.
Example PASS: "One leg per GPU: one partner keeps the GPU, the other stops and queues."

FAIL if the reply lets both canary legs continue.
Example FAIL: "Both are running, so let each finish."
