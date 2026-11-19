---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the Orchestrator mode can be a lane because it is one agent (a planning model with Hydrogen making the edits), and that it takes no `--effort`.
Example PASS: "The Orchestrator model keeps one agent, with Hydrogen executing the planner's edits, so it fits a lane. It takes no `--effort`."

FAIL if the reply rules out the Orchestrator mode as a lane, or treats it as a sub-agent fan-out.
Example FAIL: "Neither can be a lane; both start extra agents."
