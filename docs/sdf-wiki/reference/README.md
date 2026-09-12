# SDF Technique Reference & Encyclopedia

This section provides in-depth technical references, mathematical proofs, algorithm evaluations, and implementation verdicts for Puck's interpreted signed-distance field (SDF) VM and shader pipelines.

---

## Performance & Raymarching Acceleration

Techniques for accelerating sphere tracing, bounding steps, and culling candidate geometry across GPU compute passes:

- **[Marching Acceleration](marching-acceleration.md)**: Over-relaxation, conservative step bounding, curvature-based stepping, and non-linear ray tracing.
- **[Hierarchical & Instance Acceleration](hierarchical-and-instance-acceleration.md)**: Cone tracing prepasses, uniform spatial grids, ray pyramids, BVH acceleration, and GPU work graphs.
- **[Tape Pruning & Inclusion](tape-pruning-and-inclusion.md)**: Per-region VM specialization, Lipschitz interval evaluation, and synchronized ray bundle tracing.
- **[LOD & Bounds](lod-and-bounds.md)**: Proxy nodes, segment-level bounding intervals, segment tracing, and distance-dependent level-of-detail.
- **[March-Loop Scheduling](march-loop-scheduling.md)**: Wavefront ray scheduling, compaction, persistent shader threads, and primary march / shading separation.

---

## Correctness, Mathematics & Norms

Ensuring that distance fields satisfy metric properties, avoid ray tunneling, and yield accurate surface gradients:

- **[Lipschitz & Field Correctness](lipschitz-and-field-correctness.md)**: Distance estimate bounds, operation norms ($\|\nabla f\| \le 1$), repetition boundaries, and bound-preserving procedural detail.
- **[Gradients & Normals](gradients-and-normals.md)**: Analytic forward-mode normal calculation, central differences, and tetrahedral sampling paths.
- **[Antialiasing & Filtering](antialiasing-and-filtering.md)**: Geometric coverage antialiasing, beam-footprint termination, and ray-differential texture filtering.

---

## Materials, Shading & Content Primitives

Evaluating visual presentation and diegetic surfaces within the SDF renderer:

- **[Shading, AO & Shadows](shading-ao-shadows.md)**: Multi-tap ambient occlusion, penumbra soft shadows via cone marching, curvature highlights, and clustered light culling.
- **[Materials & Primitives](materials-and-primitives.md)**: Smooth polynomial composition (`smin`), material parameter ownership, lifted geometric primitives, and domain distortion.
- **[Text & Glyphs](text-and-glyphs.md)**: Marchable 3D glyph geometry, decal engraving/embossing, Multi-channel Signed Distance Field (MSDF) atlases, and text layout integration.

---

## Decision Support & Verdicts

- **[Technique Verdict Index](verdict-index.md)**: Compact applicability and status matrix across all evaluated signed-distance rendering techniques.
- **[Negative Results & Rejections](negative-results-and-rejections.md)**: Documented architectural dead-ends, non-goals, and concrete triggers required for reconsideration.

---

## Standing Engine Constraints

1. **Interpreter Dual-Contract**: The C# simulation evaluator (`Puck.SdfVm`) and HLSL presentation shaders (`Puck.Shaders`) form one packed, bit-exact contract.
2. **Conservative Cull Bounds**: Any segment or instance skipped during raymarching must be mathematically conservative; culling paths must evaluate bit-identical distances when active.
3. **Step Scaling (`stepScale`)**: The `map()` function applies the scene's global `stepScale`. Any system comparing distances against world-space metrics must account for this scaling factor.
4. **Authoritative Representation**: The analytic instruction stream and authored carve lists are the ground truth. `SampledRegion` bricks and 3D textures are bounded, invalidatable render caches—never the simulation representation.
