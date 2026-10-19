---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

A kernel I added under src/Puck.SdfVm/Assets/Shaders/Sdf picks a tile with `int tile = cell % 16;`, and `cell` can be negative. On Direct3D 12 the tiles come out right; on Vulkan with an NVIDIA card the negative side is scrambled. Is this my bug or the driver's, and how should I write it?
