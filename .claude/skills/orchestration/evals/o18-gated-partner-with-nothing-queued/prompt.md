---
description: "orchestration 'Own tracks, not tasks' and 'Land in batches': a wait (a GPU leg, the next batch) blocks the lane, never the partner; a ready lane joins the next batch held for nothing else."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [regression]
---

As the lead of Puck's lanes, a finished lane is waiting for the GPU leg that shows its change running, and the partner who owns the lane reports that it has nothing queued. What happens?
