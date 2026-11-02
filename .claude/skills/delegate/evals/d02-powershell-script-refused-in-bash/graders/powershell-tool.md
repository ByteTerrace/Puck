---
type: llm
weight: 1
---

Grade what the reply recommends: the reply is advice and need not run the script itself, and a reply that adds "I have no PowerShell tool in this session" still recommends it.

PASS if the reply tells the lane to run the script from Claude Code's PowerShell tool rather than the Bash tool.
Example PASS: "Run it from the PowerShell tool instead: `& pwsh -File D:/.../lane-cull-collect.ps1`."

FAIL if the reply never names the PowerShell tool as the route, for example only suggesting that the owner allow-list the command, that the lane read the logs with Read and Grep instead, or that the lane retry from Bash with a different path form.
Example FAIL: "Ask whoever owns the guard to add `Bash(pwsh -File ...)` to the allow list, or read the frame logs with Grep."
