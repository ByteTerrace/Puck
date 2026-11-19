---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply tells the lane not to poll or retry for the GPU lease while it waits, leaving the lead to schedule its slot.
Example PASS: "It never polls for the lease; I tell it when its slot is ready."

FAIL if the reply has the lane retry World starts or loop on the lease until it is admitted, or says nothing about polling.
Example FAIL: "Have it retry `puck_world_start` every few minutes until the lease admits it."
