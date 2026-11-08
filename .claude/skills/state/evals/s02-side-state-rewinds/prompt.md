---
description: "A host store beside the arena kept a refused firing's write; the plan was a per-firing snapshot and restore instead of the journal participant the arena already rewinds."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

In `Puck.World.Server`, a new rule effect writes into a `Dictionary<int, long>` of tallies that `WorldRuleHost` keeps beside the `StateArena`. It is simulation state: it goes into the checkpoint and the hash. When a later effect in the same firing is refused, the tally write stays behind, so the world diverges from what the rule meant. I was going to copy the dictionary at the start of every firing and restore the copy when the firing is refused. Does Puck already have a mechanism for this, and how would the store use it?
