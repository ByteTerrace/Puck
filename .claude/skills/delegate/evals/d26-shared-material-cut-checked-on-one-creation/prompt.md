---
description: "AGENTS.md 'Puck's purpose': the gallery is the engine's self-test, and a change to a shared system is judged by whether the exhibits it reaches still read as their plaques say. Models a lane that checks the creation that prompted a bake change and reports done."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
runs: 5
---

I'm a delegated Puck lane. My brief was the Moth's wing: its second material bled across the cut where the wing blends into the thorax. I fixed it at the cause, in how the mesh bake assigns a material to the surface a blend's cut leaves (`src/Puck.SdfVm/SdfMeshPathBake.cs` and `src/Puck.SdfVm/SdfMeshBakeClosure.cs`, which every baked creation and every gallery exhibit goes through). I built `Puck.SdfVm`, booted `worlds/debug` headless, and the Moth's wing audit and its capture from four sides show the second material stopping at the cut. The finish line in my brief reads "the Moth's wing reads clean from every side", and it holds. I'm about to write `Brief: moth-wing done`. Is the hand-back ready? No checkout is reachable from here; answer from what I describe.
