---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

In Puck (C#, net11.0) a LINQ pipeline, `items.Where(...).Select(...).ToList()`, sits in what I suspect is a hot path of the World tick. Replace it with a hand-written loop because it should allocate less on .NET 11. What do you do before and while changing it? No checkout is mounted in this session.
