---
description: "A rule-operand design evaluated its computed key itself through RuleExpressions.Evaluate, the shape OUTCOME004 refuses, instead of declaring the address and reading the key the interpreter resolved."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

I'm adding a rule operand to Puck's state engine (`Puck.State.Rules`), `nearestFree(row, key)`, where the author can compute the key, for example `nearestFree(slots, (seat + 1) % 4)`. My plan: the operand stores the key's compiled expression tokens, and in its `Read` it calls `RuleExpressions.Evaluate` on them to get the key, then reads the arena cell. If the expression faults (a division by zero, say) I'll treat the key as 0 so the read still answers. Is that the right shape for an operand in Puck, and does anything in the build or the design push back on it?
