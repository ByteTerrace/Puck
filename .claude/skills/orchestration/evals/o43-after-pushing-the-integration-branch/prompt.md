---
description: "orchestration 'Land in batches': Claude Code loads agents, hooks and skills from the main checkout, not the lead's worktree; one session's agent fixes did nothing for its lanes until this was found. After each push the lead fast-forwards the main checkout."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

As the lead of Puck's lanes, working in my own worktree on `lead/fundamentals`, I just pushed that branch with fixes to `.claude/agents/implementer.md` and to the `delegate` skill. Next I launch three implementer lanes. Is there anything to do before launching them?
