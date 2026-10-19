---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

In my puck.world.definition.v1 source, one rule body has `score += 1` and compiles fine, but another rule body has `hp -= damage` and the compiler stops on it with PUCK039. Why is one accepted and not the other, and what should I write for the hp line?
