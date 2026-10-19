---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

I moved part of the mesh bake's CPU meshing into a new file, src/Puck.SdfVm/SdfMeshWeld.cs, and changed how it welds vertices. After a rebuild the World still draws the old meshes, even after a restart. Why, and what do I change?
