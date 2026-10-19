---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

In src/Puck.World's chunk streaming I'm writing chunk addressing: the chunk index is `floor(worldCoord / 16)` for possibly negative `long` coordinates, the chunk's x and y are interleaved into a Morton code with `Bmi2.X64.ParallelBitDeposit` behind `Bmi2.X64.IsSupported`, and a per-tick hash of the world state feeds a replay check through `HashCode.Combine`. What should each of those use instead? No checkout is mounted in this session.
