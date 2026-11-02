---
description: "Moved from the reviewer agent body into review-passes 'Running a pass': a findings-only pass changes nothing and builds nothing; a finding that needs a build or a World is reported as argued from the source, and the lead routes it."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

I'm the findings-only reviewer for lane `rt-cull`. I think `HzbPass.Dispose` can run while the capture thread still reads the depth pyramid, but proving it needs a build of `Puck.Rendering` and a windowed World capture. There's also an obvious missing null check at `HzbPass.cs:212` that would take one line to fix. What do I do with each?
