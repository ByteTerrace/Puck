---
description: "Moved from the reviewer agent body into review-passes 'Running a pass': a code review whose brief names no revisions is blocked; the reviewer says so and stops."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

I'm a findings-only reviewer lane. My brief says: "Review the culling change on lead/fundamentals for correctness; findings to C:/Users/me/.claude/jobs/7c1e/findings/cull.md." It names no base or head. `git log lead/fundamentals` shows a dozen culling commits from the last two days among others. How do I proceed?
