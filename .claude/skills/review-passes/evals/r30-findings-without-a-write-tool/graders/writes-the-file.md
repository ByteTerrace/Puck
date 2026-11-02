---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to write the findings file anyway at the brief's path (through the PowerShell tool) and has the hand-back repeat them in short, because the hand-back alone can be lost.
Example PASS: "Write it with PowerShell `Set-Content -LiteralPath <path> -Value @'...'@`, then repeat the findings briefly in the hand-back."

FAIL if the reply accepts the hand-back as the only copy of the findings.
Example FAIL: "That's fine; the lead reads the hand-back."
