---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply refuses `/swarm` for a lane because it starts sub-agents and a lane never starts sub-agents (when work splits, the lead splits it into lanes), and says the brief states that no parallel mode is used.
Example PASS: "`/swarm` starts sub-agents, and a lane never does. If the rewrite splits, I split it into lanes, and the brief says so."

FAIL if the reply accepts `/swarm`, or accepts it for a lane with conditions.
Example FAIL: "Swarm is fine if the files are independent and the lane merges the results."
