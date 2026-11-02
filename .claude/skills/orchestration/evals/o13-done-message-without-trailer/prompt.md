---
description: "orchestration 'Write and track assignments': the message is the notification and git is the record; only the assignment's last commit carries `Brief: <id> done`, and the lead inspects the diff and what the lane ran before merging."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [regression]
---

As the lead of Puck's lanes, a lane messages you "[L7] done: all green". Its last commit carries no Brief trailer and you have not looked at the diff or its results. Do you merge it?
