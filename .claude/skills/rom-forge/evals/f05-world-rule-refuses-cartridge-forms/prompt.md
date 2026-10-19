---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

A rule in my `*.cgb.puck` cartridge uses `repeat 8 as i { ... break }` and a `key(left, held)` gate, and it compiles fine. I pasted the same rule into a world `.puck` document and `puck compile` reports PUCK037 and PUCK038. Why does the cartridge accept it, and what do I change in the world?
