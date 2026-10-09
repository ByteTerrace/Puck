---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

This cartridge array in my .puck source is 180 hand-typed zeroes and it's painful to review or change: `arrays [ { name: "field", initial: [0, 0, 0, ... 180 times ... , 0] } ] }`. Is there a way to write this that doesn't require 180 literal lines?
