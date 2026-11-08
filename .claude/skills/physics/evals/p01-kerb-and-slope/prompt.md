---
description: "A walker stopped at a kerb and floated down slopes; the plan was a new step-up routine and slope raycast in WorldBody, when kit motion.stepHeight and motion.groundSnap drive FixedStepUp already."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

In the Puck debug world the Moth walks into a 0.2 m kerb and stops dead, and walking down a gentle hill she leaves the ground for a few frames at a time. I want to add a step-up routine to `WorldBody` that raises her capsule, retries the move and drops her back down, plus a downward raycast each tick that glues her to the slope. Which existing Puck mechanisms cover these two behaviours, and where would the change actually be made?
