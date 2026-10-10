---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

Replace a clear LINQ pipeline in a suspected hot path with a hand-written loop because it should allocate less on .NET 10.
