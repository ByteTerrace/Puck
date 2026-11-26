---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

A rule in a puck.world.definition.v1 document fails with PUCK037 when I write:
```
rule "advance-round" {
    repeat 3 as i {
        tableState[activeRound] += 1
    }
}
```
What's actually wrong, and how do I fix it?
