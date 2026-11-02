---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply tells the lane to name its own World (the `world` argument) on every `puck` call, since the server also drives the owner's World.
Example PASS: "Pass your `world` on every call, or a call can reach the owner's World."

FAIL if the reply does not say the lane names its World on each call.
Example FAIL: "Start your own World with `puck_world_start` and run your checks."
