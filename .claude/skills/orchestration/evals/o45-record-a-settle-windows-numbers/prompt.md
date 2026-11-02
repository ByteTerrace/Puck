---
description: "orchestration 'Schedule machines', settle windows: the numbers replace the owning plan's current reading, with no date in the text and the commit in the message; earlier readings live only in commits. documentation.md: leave out dates and commit SHAs."
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

As the lead of Puck's lanes, a settle window just finished. The fixed recipe gave a D3D12 frame p95 of 14.2 ms at the hall stop and a 341 s cold bake. `docs/plans/culling.md` has a "Current reading" section from the previous window (16.8 ms, 392 s, with that window's date). How do I record the new numbers in the plan?
