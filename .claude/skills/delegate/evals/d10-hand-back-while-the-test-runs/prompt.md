---
description: "orchestration/references/lane-work.md 'Running long tasks': a lane once held nine watchers, four on a log that had ended without the awaited line and five identical re-arms of one wait. delegate 'Finish': done is a state."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

I'm lane `cull-lod`. My code is committed. `puck lease heavy --holder "cull-lod lod" -- puck test worlds/debug/lod-tests.puck --jobs 1` is running in the background, 20 minutes in. I armed a Monitor on its log waiting for the line `PASS` and re-armed it a few times when it timed out, so four or five of them are up. I also started a windowed World `cull-lod` with `puck_world_start` to look at the LOD transitions, and it's still running. I'm going to write my hand-back now, with `Brief: cull-lod done` on the last commit; the test will finish by itself. Anything wrong with that? No checkout is reachable from here; answer from what I describe.
