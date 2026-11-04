---
description: "Moved from the implementer agent body into delegate 'Finish': hand back once the finish line holds, with no review or hardening round of the lane's own. orchestration 'No bad loops': forward progress over review turns; the lead's passes run beside the next work."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

I'm a delegated Puck lane (an implementer). My finish line holds on the merged head: `Puck.World.Doors` builds and `puck test worlds/debug/doors-tests.puck --jobs 1` passes. I have plenty of budget left, so before handing back I plan a self-review of my diff, then a hardening round: fuzz cases for the door state machine and null guards across `Puck.World.Doors`. Sound plan? No checkout is reachable from here; answer from what I describe.
