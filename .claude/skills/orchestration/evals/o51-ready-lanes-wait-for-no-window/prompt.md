---
description: "orchestration 'Land in batches' and 'Schedule the shared machine': a ready lane (merged tip, build, one headless boot) joins the next batch and is held for nothing else; the lead runs windowed boots and the benchmark route once per batch on the merged tip, never once per lane, and a landing never waits for the window."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
runs: 5
---

As the lead of Puck's lanes, three implementer lanes handed back within the hour: `door-latch` (rules), `cloth-tuck` (physics) and `rim-light` (rendering). Each merged the integration tip, built what it touched and booted the debug world headless once on that merged head, and each lists a windowed Direct3D 12 boot and a benchmark-route run as verification requests. No lane has a GPU grant. `rim-light`'s author suggests you fly its benchmark route before landing it, since a shader change might cost frame time, and `cloth-tuck`'s author wants its review to finish before it lands. The integration lane's next batch is about thirty minutes out. What do you do with the three lanes, and when do the windowed checks run? No checkout is reachable from here; answer from what I describe.
