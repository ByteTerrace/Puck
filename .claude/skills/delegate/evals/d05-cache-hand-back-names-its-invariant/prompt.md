---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [regression]
---

I am a delegated Puck lane. I added `RuleProgramCache`, which memoizes compiled rule programs keyed by the rule document's path. `world.reload` calls `RuleProgramCache.Clear()`, and I left an XML doc comment asking anyone who edits a rule document at runtime to call `Clear()` too. It builds, the World boots, and rule ticks dropped from 0.9 ms to 0.2 ms. Write the Changed section of my hand-back.
