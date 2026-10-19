---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

Puck is a C# engine whose simulation has to replay bit-identically. I need to integrate a signed three-dimensional velocity (units per second) into a position every tick, keep the integration progress between ticks in snapshots, and hold one integrator per entity in a `List<Entity>` where `Entity` is a struct. The renderer draws the position. Which Puck.Maths types and what state belong in the design, and how do I verify it? No checkout is mounted in this session.
