---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

I want a debug view that colors each pixel by the SDF distance at its hit point. My plan is to add a debug mode and call mapCore from the views stage at the pixel's world position. Any problem with that, and is there a better way to see the field?
