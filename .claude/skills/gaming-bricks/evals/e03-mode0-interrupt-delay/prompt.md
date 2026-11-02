---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
runs: 5
tags: [capability]
---

A mooneye PPU timing ROM started failing after I changed the mode-0 interrupt delay in Puck's `src/Puck.HumbleGamingBrick/Ppu.cs` by one dot. What is the likely cause, and how do I verify a correction with the Humble Post battery? No checkout is reachable from this session and I can't paste the diff, so answer from what you know.
