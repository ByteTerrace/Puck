---
description: "orchestration 'Schedule the shared machine': at most three GPU-granted lanes hold windowed work at once, and each windowed World holds 12 to 20 GB; a fourth lane works on what needs no window and takes a slot the lead schedules by name; no lane polls for the lease; windowed checks owed by several lanes batch into one lead window. A lead once ran six windowed lanes at once and a lane was refused after a 30-minute wait."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
runs: 5
---

As the lead of Puck's lanes, three lanes hold GPU grants right now: `moth-wings` (a look lane iterating on a creation's lamps and palette in a windowed World), `bake-bytes` (a memory attribution under a windowed World) and `shadow-refresh` (a GPU pass measurement). The owner asks for a fourth lane at once: `glacier-seams`, whose author expects to check the seams in a windowed World as it works. Two building lanes are also compiling on the same machine. How do you brief `glacier-seams`, and what do you tell it about the GPU? No checkout is reachable from here; answer from what I describe.
