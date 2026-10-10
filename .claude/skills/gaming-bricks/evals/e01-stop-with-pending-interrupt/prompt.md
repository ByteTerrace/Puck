---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

Fix an SM83 STOP timing bug that appears only when an interrupt is already pending, without splitting behavior into separate GB and GBC CPU implementations.
