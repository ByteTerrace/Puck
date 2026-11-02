---
type: llm
weight: 1
---

The response distinguishes the published World, which is ReadyToRun-compiled and moves first-use JIT out of startup, from an ordinary build, which produces IL that tiers, and says which one the measurement or fix concerns.

PASS example: "A published World is ReadyToRun; an ordinary build is IL and tiers. Measure the exact published artifact with `puck bench startup --world-artifact <path>` separately from the IL build."

FAIL if it does not separate the two builds, or proposes enabling ReadyToRun as if the published World lacked it. Example: "Turn on ReadyToRun to fix the startup cost."
