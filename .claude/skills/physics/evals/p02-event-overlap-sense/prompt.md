---
description: "An event feed once kept its own capsule/box overlap test over world bounds, which reported overlaps the narrowphase never did; events must ask the contact kernel's overlap sense."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

I'm adding a trigger to Puck's `WorldEventFeed`: when one body's capsule overlaps another body's box, fire an event. Since it only drives events, not physics, I planned to write a quick capsule-versus-box test there using the two bodies' world-space bounding boxes, and count a touching pair as overlapping. Is that a good approach in Puck, and what should the event use?
