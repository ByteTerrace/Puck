---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

In my puck.world.definition.v1 source one rule checks five rotation offsets for a falling piece. I wrote five near-identical `local fit0 = ...` through `local fit4 = ...` lines by hand, one per offset in `let kicks = [0, -1, 1, -2, 2]`. Can the rule generate them from `kicks`, and can I also put a `when` inside that loop so each offset adds its own condition?
