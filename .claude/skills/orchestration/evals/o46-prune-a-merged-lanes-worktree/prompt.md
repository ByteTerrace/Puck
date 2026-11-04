---
description: "orchestration 'Land in batches': keep a merged lane's worktree until its invariant review, if it has one, is back; most review highs go back to the lane that wrote the code, which resumes in place with its context."
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

As the lead of Puck's lanes, lane `rt-cull`, which changed when GPU buffers are retired, landed in the last batch an hour ago and its adversarial Codex review of those lifetimes is still running. The disk is filling up with lane worktrees, so I am pruning merged ones. Do I remove `rt-cull`'s worktree now?
