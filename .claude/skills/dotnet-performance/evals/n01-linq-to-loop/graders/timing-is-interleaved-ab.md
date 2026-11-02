---
type: llm
weight: 1
---

The response says any timing comparison runs as interleaved A/B rounds (old and new alternating) on a quiet machine or under the heavy-work lease.

PASS example: "Then run interleaved A/B `puck bench world` rounds in a quiet window."

FAIL if it times the old build and then the new one once each, or gives no conditions for the timing. Example: "Benchmark the old version, apply the change, benchmark again."
