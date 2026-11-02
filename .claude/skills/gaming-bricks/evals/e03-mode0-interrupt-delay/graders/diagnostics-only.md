---
type: llm
weight: 1
---

The response uses diagnostic flags (`--stat-trace`, `--cosim` or `--render`) to investigate the failure and confirms the correction with the Humble Post stages that check it, not with a diagnostic's output.

PASS example: "`--stat-trace` and `--render` explain a run but aren't a verdict; the fix is confirmed by the stage for the failing ROM and the battery."

FAIL if a diagnostic's output is the proof of the fix, or no Post stage confirms it. Example: "Compare the `--stat-trace` output before and after; if mode 0 starts on the right dot, it's fixed."
