---
type: llm
weight: 1
---

The response says the accumulator's sub-unit remainder (with its ticks-per-second) is authoritative simulation state that must be saved in snapshots and state hashes.

PASS if it says the accumulator's remainder is part of simulation state that is snapshotted and hashed, and restored on load. Example: "The accumulator's per-axis remainders are simulation state: save them in the snapshot and include them in the state hash."

FAIL if it saves only the position, or treats the remainder as a cache that can be recomputed. Example: "Snapshot the position; the progress between ticks is rebuilt at the next tick."
