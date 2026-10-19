---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

In Puck's `src/Puck.HumbleGamingBrick` SM83 core, the STOP opcode (`0x10`) mis-times only when an interrupt is already pending at the moment it executes. I want to fix it without splitting GB and GBC into separate CPU implementations. Where does the fix belong, what is the exact rule for STOP's pad byte, which external test corpus in the Humble Post battery will disagree with the corrected behavior and how do I treat that, and what do I run to show it works?
