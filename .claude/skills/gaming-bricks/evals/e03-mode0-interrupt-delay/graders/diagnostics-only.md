---
type: llm
weight: 1
---

The response does not treat a diagnostic flag's output (`--stat-trace`, `--cosim`, `--render`) as the proof of the correction: the verdict comes from the Humble Post stages and battery.

PASS if it confirms the correction by the Post stage for the failing ROM and the battery, and may use a diagnostic flag only to investigate. Example: "`--stat-trace` and `--render` explain a run but aren't a verdict; confirm the fix with the stage for the failing ROM and the battery."

FAIL if it makes a diagnostic's output the proof of the fix, or names no Post stage that confirms it. Example: "Compare the `--stat-trace` output before and after; if mode 0 starts on the right dot, it's fixed."
