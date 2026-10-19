---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

My world source board.puck had a bunch of `let` constants, a `template` for each piece kind, and `for` loops that stamped the board's rows. I lost the .puck file, but I still have board.world.json, which puck compile wrote from it. If I run puck decompile on the JSON, do I get my source back as it was? And once I have a .puck again, which file do I edit from then on, and what do I run to check an edit before I boot it?
