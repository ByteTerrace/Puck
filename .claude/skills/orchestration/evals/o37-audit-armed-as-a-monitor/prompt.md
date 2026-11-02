---
description: "orchestration 'Audit the process before the owner does': arm the audit as a background monitor, measure landing latency, idle slots and the board's age, and act on what degraded in the same turn."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [regression]
---

As the lead of Puck's lanes, you want a recurring check on whether the fleet's process is working: how long lanes wait to land, idle implementation slots, the board. How do you arm it, what do you measure, and what do you do when it reports?
