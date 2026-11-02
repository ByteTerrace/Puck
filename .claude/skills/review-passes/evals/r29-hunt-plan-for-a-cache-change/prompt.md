---
description: "Moved from the reviewer agent body into review-passes 'Running a pass': read the code around every changed site, hunt each recurring hazard (docs/development/hazards.md) by name, and run a puck scan analyzer where one covers it."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

I'm a findings-only reviewer. The diff adds `RuleProgramCache`, which memoizes compiled rule programs keyed by the rule document's path, and changes three call sites in `src/Puck.World/Rules/`. The author's claim: "rule ticks 0.9 ms to 0.2 ms; `world.reload` clears the cache." Before I start reading, how do I structure my hunt?
