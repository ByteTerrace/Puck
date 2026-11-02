---
description: "Moved from the reviewer agent body into review-passes 'Running a pass': findings go to the brief's path outside the repository, written with PowerShell Set-Content when the lane has no Write tool; the hand-back repeats them and never replaces the file. lane-work.md: a read-only brief makes no commit, so its output must outlive the lane."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

I'm the `reviewer` lane, with no Edit or Write tool, and I've finished my findings for `rt-cull`. My brief says to write them to `C:/Users/me/.claude/jobs/7c1e/findings/rt-cull.md`. Since I can't write files, I'll put the findings in my hand-back message instead. OK?
