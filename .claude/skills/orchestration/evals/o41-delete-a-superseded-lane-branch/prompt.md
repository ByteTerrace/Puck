---
description: "orchestration 'Write and track assignments': before deleting a superseded branch, search docs/ and .claude/ for its name. open-items.md kept lane/companion-v1 as prior art for code no other branch held, and the lead deleted it anyway. AGENTS.md: deleting a remote branch that holds unmerged work is the owner's."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

As the lead of Puck's lanes, I'm cleaning up remote branches. `lane/hzb-cull-v1` on `origin` was superseded when the meshlet culling design landed on `lead/fundamentals` last week; v1 was never merged. `puck search` over `src/` and `tests/` finds none of its type names, and no open lane is based on it. Do I delete it now? No checkout is reachable from here; answer from what I describe.
