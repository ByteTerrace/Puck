---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

I want the mesh bake to sample the field at four extra points around each lattice sample. The simplest way is four more calls to mapCore in the bake kernel. Is that fine?
