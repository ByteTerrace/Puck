---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

A new forge test steps the Color machine 30 frames with my own loop and then reads a cartridge variable out of WRAM. Most runs read the right value, but some read 0xFF. The cartridge logic looks right. What is going on and how should the test read it?
