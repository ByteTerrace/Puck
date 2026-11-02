---
description: "orchestration 'Land in batches' and verification: nothing edits or merges into a tree while a canary, parity or test run reads it, so an editing review pass runs in its own worktree."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [regression]
---

As the lead of Puck's lanes, a granted GPU canary run is building from a worktree. You want to launch a review pass that edits files (`--write`) and the quickest place is that same worktree. Is that fine?
