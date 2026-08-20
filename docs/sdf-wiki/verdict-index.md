# SDF technique index

This table summarizes how major technique families fit the current renderer.
It intentionally omits implementation chronology and review provenance.

| Technique family | Current applicability | Detail |
|---|---|---|
| Auto-relaxed sphere tracing | Shipped default with strict reference path | [Marching acceleration](marching-acceleration.md) |
| Fold-safe step bounds | Required for discontinuous domain folds | [Lipschitz correctness](lipschitz-and-field-correctness.md) |
| Per-composition chamfer bound | Required; a per-program or per-chain chamfer factor is unsound | [Lipschitz correctness](lipschitz-and-field-correctness.md) |
| Conservative non-convergence on CPU query verbs | Required; a Boolean that folds "gave up" into "clear" is unsound for simulation | [March scheduling](march-loop-scheduling.md) |
| Cell-local field evaluation behind a hierarchical position | Not applicable; a world-space seam must rebase or refuse | [Lipschitz correctness](lipschitz-and-field-correctness.md) |
| Uniform-grid instance culling | Shipped default | [Hierarchy and instances](hierarchical-and-instance-acceleration.md) |
| Per-region tape pruning | Not useful for ordinary flat room programs; reconsider inside large multi-segment instances | [Tape pruning](tape-pruning-and-inclusion.md) |
| Per-segment bounds | Open priority for placed creations | [LOD and bounds](lod-and-bounds.md) |
| BVH or TLAS/BLAS for analytic instances | Conditional on a workload the uniform grid cannot handle | [Hierarchy and instances](hierarchical-and-instance-acceleration.md) |
| Wavefront or persistent-thread marching | Conditional on measured divergence and portable scheduling semantics | [March scheduling](march-loop-scheduling.md) |
| Analytic forward-mode normals | Shipped default; four-tap comparison remains available | [Gradients and normals](gradients-and-normals.md) |
| Normal-ladder AO | Shipped three-tap ambient-only path | [Shading, AO, shadows](shading-ao-shadows.md) |
| Cone AO and bent normals | Optional quality tier; open | [Shading, AO, shadows](shading-ao-shadows.md) |
| Penumbra soft shadows | Shipped with per-pixel grid gather | [Shading, AO, shadows](shading-ao-shadows.md) |
| Material blending at smooth seams | Shipped hit-only shading path | [Materials and primitives](materials-and-primitives.md) |
| Non-orthogonal screen and text frames | Not supported; refused at every door that accepts a frame | [Materials and primitives](materials-and-primitives.md) |
| Host bounds on the screen material sentinel band | Required on both ends, with the shader bounding the decoded index | [Materials and primitives](materials-and-primitives.md) |
| Degenerate profile bounds sized to the fixed-point representation | Required wherever an exact core divides by its own dimensions; refused at the builder and the document validator | [Materials and primitives](materials-and-primitives.md) |
| Negative authored scale as a mirror | Not supported; refused at the creation document validator in favour of the symmetry domain op | [Conditional techniques](negative-results-and-rejections.md) |
| Closed-form copy counts before a domain fold expands | Required; an authored chain past the copy budget is refused in O(1) memory | [LOD and bounds](lod-and-bounds.md) |
| Coverage AA | Shipped footprint-aware path | [Antialiasing](antialiasing-and-filtering.md) |
| Ray-differential CRT filtering | Open when minification is visible | [Antialiasing](antialiasing-and-filtering.md) |
| Bound-preserving procedural noise | Open; requires integer hash and derivative bound | [Lipschitz correctness](lipschitz-and-field-correctness.md) |
| Sampled carve bricks | Shipped as an invalidatable render cache, not a core representation | [LOD and bounds](lod-and-bounds.md) |
| Global voxel or clipmap representation | Not a current fit; requires a distinct content-source boundary | [Conditional techniques](negative-results-and-rejections.md) |
| MTSDF alpha for marchable glyphs | Preferred field channel | [Text and glyphs](text-and-glyphs.md) |
| Glyph decals for dense reading text | Shipped material-level tier | [Text and glyphs](text-and-glyphs.md) |
| Coverage rasterizers as SDF geometry | Not applicable; they produce coverage, not a marchable distance | [Text and glyphs](text-and-glyphs.md) |

Open implementation work is tracked nowhere. The backlog that held it was
deleted on 2026-08-02 and nothing replaced it, so an "Open" row above is the
full record of that item: no owner, no sequencing, and no plan to start one.
