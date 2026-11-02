---
description: "AGENTS.md 'When you are the delegate': merge the integration tip into your own branch before reporting and re-run checks; merge, never rebase. orchestration 'Land in batches': a ready lane has merged the integration tip."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [regression]
---

I'm lane `sleeping-islands`, branched from `lead/fundamentals` this morning. My change under `src/Puck.Physics/Islands/` is committed, `Puck.Physics` builds and `worlds/debug/islands-tests.puck` passes. Since I branched, `lead/fundamentals` has moved 14 commits: the lead landed a batch that also touched `src/Puck.Physics/Solver/`. I'm about to write my hand-back. Is there anything to do first? No checkout is reachable from here; answer from what I describe.
