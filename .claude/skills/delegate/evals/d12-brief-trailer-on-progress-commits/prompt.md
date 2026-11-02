---
description: "orchestration 'Write and track assignments': only the assignment's last commit carries `Brief: <id> done|blocked`; progress and WIP commits carry none. `puck lanes` counts a Brief trailer as delivery, so a trailer on a progress commit shows a running lane as delivered."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

I'm lane `bars-and-pick`, brief id `bars-and-pick`. I'll make three more commits: a WIP that sets my fix aside before a fault injection, then the fix, then the docs update that closes the brief. I've been ending each commit with `Brief: bars-and-pick done`, since each one finishes a step and it shows the lead my progress. What should each of the three commit messages end with? No checkout is reachable from here; answer from what I describe.
