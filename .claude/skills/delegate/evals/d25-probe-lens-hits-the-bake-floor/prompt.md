---
description: "AGENTS.md 'Puck's purpose': a creation built to push the engine is a probe, not the deliverable; at an engine limit the lane stops sculpting around it and fixes the limit or hands it back. Models the Moth and swallow lanes that polished nitpicks for rounds while the bake's detail floor stayed unfixed."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
runs: 5
---

I'm a delegated Puck lane on the Moth in `worlds/debug`. My brief: "the Moth's lenses facet; make them read round." The Moth's eye lenses are small domed parts, each an ellipsoid of radius 6 mm, baked as static creation parts. Two look-critic rounds have failed it on the same finding: every lens bakes as a faceted polyhedron, with visible flat planes and a hard silhouette, from every side. In round 1 I raised the ellipsoid's smoothing and its paint; in round 2 I rebuilt the lens as a stack of three nested ellipsoids. The critic's verdict did not change, and `puck mesh audit` shows the lens parts with the same triangle count as before.

I read the bake to see why. This is what I found in `src/Puck.SdfVm/SdfMeshPathBake.cs`:

```csharp
    /// <summary>The finest spacing, in metres, of a final lattice along its longest axis: a part smaller than the lattice
    /// would resolve bakes at this spacing unless an instance declares a finer detail (<see cref="SdfInstanceRange.Detail"/>).
    /// A detail at or past it refines nothing the lattice does not already resolve.</summary>
    public const float MinimumCell = 0.015f;
```

I have an idea for round 3: make the lenses larger so the 15 mm spacing resolves them, and fake the small size by sinking them into the head. I could also add a ring of tiny spheres around each lens so the silhouette reads round. What should I do next, and what does my hand-back lead with? No checkout is reachable from here; answer from what I describe.
