---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

A commercial GBA multiplayer game never leaves its link lobby on Puck's `src/Puck.AdvancedGamingBrick` core: it polls the multiplayer SIOCNT ready bits to detect a partner. Where is this limitation recorded today, where would the fix go, what must the new status bits be derived from, and what else has to change when it lands?
