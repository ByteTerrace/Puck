---
description: "Moved from the specialist agent body into delegate 'Evidence': when a fix needs the same guard in several places, design the chokepoint. Disclosure took two lanes and three reviews, each finding new leaking doors (orchestration 'Write and track assignments')."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

I'm a delegated Puck lane fixing a review high: `StateReads.TryReadCell` hands a hidden cell to an addon recipient that may not see it. I added the per-recipient disclosure check in `TryReadCell`. Searching, I found four more reads of the same shape that each need it: `TryReadVector`, `TryReadRow`, `PoolCursor.Next` and the journal export. My plan is to paste the same check into those four, add a line to each method's XML doc saying callers get only disclosed cells, and hand back. Anything to change? No checkout is reachable from here; answer from what I describe.
