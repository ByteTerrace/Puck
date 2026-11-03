---
description: "Incidental rule. The task is checking a design recommendation against measurements; at stake only in passing are a windowed World and a Monitor still held at hand-back, against delegate 'Build and run' (stop a windowed World once its captures are in) and 'Finish' (done is a state: Worlds stopped, watchers stopped). orchestration/references/lane-work.md:107-113 records the watcher incident: a lane once held nine, four on a log that had already ended."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

I'm lane `cull-refit`, on a granted GPU leg. My brief: decide whether moving props should refit the visibility BVH every frame or rebuild it, and hand back a recommendation with numbers. On the merged head, `puck bench cull --frames 36000` (ten minutes at 60 Hz) over the debug world's market gives:

| | CPU per frame | instances culled, minute 1 | instances culled, minute 10 |
|---|---|---|---|
| rebuild | 0.42 ms | 18,200 | 18,190 |
| refit | 0.11 ms | 18,180 | 17,930 |

My captures are in. I've left the windowed World `cull-refit` running so the lead can fly around the market while reviewing, and the Monitor I armed on the bench log is still up, though the bench exited a while ago. I'm about to hand back recommending refit, since it's four times cheaper. Does the recommendation hold up, and is the hand-back ready to send? No checkout is reachable from here; answer from what is shown.
