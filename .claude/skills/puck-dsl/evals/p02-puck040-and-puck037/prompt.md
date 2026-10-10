---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

Two of my .puck edits are failing to compile and I don't understand either error:

1) `host: {\n    width: 1280\n}` fails with a PUCK040 error pointing at the `host:` line.
2) A rule in a puck.world.definition.v1 document fails with PUCK037 when I write:
```
rule "advance-round" {
    repeat 3 as i {
        tableState[activeRound] += 1
    }
}
```
What's actually wrong with each one, and how do I fix them?
