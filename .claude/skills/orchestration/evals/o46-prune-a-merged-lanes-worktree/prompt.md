---
description: "orchestration 'Land in batches': keep a merged lane's worktree until its cross-family review is back; most review highs go back to the lane that wrote the code, which resumes in place with its context."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

As the lead of Puck's lanes, lane `rt-cull` landed in the last batch an hour ago and its cross-family Codex review is still running. The disk is filling up with lane worktrees, so I am pruning merged ones. Do I remove `rt-cull`'s worktree now?
