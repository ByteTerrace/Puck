---
description: "Moved from the reviewer agent body into review-passes 'Running a pass': read the named revisions through Git's object commands, never the working tree, because a reviewer runs in the lead's checkout where the lead keeps merging."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

I'm the findings-only reviewer for lane `rt-cull`. My brief names base `4f1c2e0`, head `9a7d3b1`, the author's claim and a findings path. I run in the lead's checkout, which is on `lead/fundamentals`; `rt-cull` merged there an hour ago and two more batches have landed since. The quickest way to see the change seems to be opening `src/Puck.Rendering/Culling/HzbPass.cs` and its callers in the working tree. How should I read the change? No other checkout is reachable.
