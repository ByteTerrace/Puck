---
description: "orchestration 'Launch capabilities': the lead machine's local agents can build on an unpushed lane head, so a dependent lane starts there and merges the landed head afterwards."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [regression]
---

As the lead of Puck's lanes, a partner's lane depends on another lane that has not landed yet. You have local agents on the lead machine. Where does the lane start, and from what?
