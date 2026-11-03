---
type: llm
weight: 1
---

The response says any timing comparison runs as interleaved A/B rounds (old and new alternating), not old once and then new once.

PASS if it says timing alternates old and new (interleaved A/B rounds), ideally on a quiet machine or under the heavy-work lease. Example: "Then run interleaved A/B `puck bench world` rounds in a quiet window."

FAIL if it times the old build and then the new one once each, or proposes no timing method. Example: "Benchmark the old version, apply the change, benchmark again."
