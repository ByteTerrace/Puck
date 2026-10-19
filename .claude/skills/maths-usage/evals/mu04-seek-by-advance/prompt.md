---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

In src/Puck.World's loot code I want to fast-forward a `Pcg32XshRr` generator past a replayed segment with `Advance(n)` instead of re-running the draws. The segment's drops use `NextUInt32(min, max)` for quantities, `Shuffle` for ordering and an alias-table sample for the item. Is seeking like that exact, and what should I do instead? No checkout is mounted in this session.
