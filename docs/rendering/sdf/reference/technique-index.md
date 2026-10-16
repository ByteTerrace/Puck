# SDF technique index

This table summarizes how major technique families fit the current engine.
It intentionally omits implementation chronology and review provenance.

| Technique family | Current applicability | Detail |
|---|---|---|
| Fold-safe step bounds | Required for discontinuous domain folds on every CPU marcher | [Lipschitz and field correctness](lipschitz-and-field-correctness.md) |
| Per-composition chamfer bound | Required; a per-program or per-chain chamfer factor is unsound | [Lipschitz and field correctness](lipschitz-and-field-correctness.md) |
| Conservative non-convergence on CPU query verbs | Required, and directed per verb by what its true half asserts: an obstruction verb folds "gave up" to a hit, a surface verb to "not found" | [Lipschitz and field correctness](lipschitz-and-field-correctness.md) |
| Step scale on every marcher over the stream | Required; a rigid-only op subset does not license a raw advance, because chamfer blends overestimate | [Lipschitz and field correctness](lipschitz-and-field-correctness.md) |
| Iteration budget derived from the step scale | Required; a fixed budget shortens a clamped march's reach in proportion | [Lipschitz and field correctness](lipschitz-and-field-correctness.md) |
| Cell-local field evaluation behind a hierarchical position | Not applicable; a world-space seam must rebase or refuse | [Lipschitz and field correctness](lipschitz-and-field-correctness.md) |
| Per-segment bounds | Open priority for placed creations | [Level of detail and bounds](lod-and-bounds.md) |
| Tetrahedral bake gradients and interpolated vertex normals | Shipped: the bake measures gradients only at sign-change corners | [Gradients and normals](gradients-and-normals.md) |
| Screen-space hemisphere AO | Shipped in the surface pass, ambient-only | [Shading, ambient occlusion, and shadows](shading-ao-shadows.md) |
| Irradiance volume traced on the CPU against the baked meshes | Shipped | [The mesh path's irradiance volume](../handbook/lighting-and-shading.md#the-mesh-paths-irradiance-volume); [P6-GI plan](../../../plans/rendering.md#p6-gi-global-illumination) |
| Cascaded percentage-closer soft shadows | Shipped: two cascades for the key light, one for the second stable slot | [Shading, ambient occlusion, and shadows](shading-ao-shadows.md) |
| Material blending at smooth seams | Tracked by the interpreter, not drawn: the bake point-samples each quad's winning material | [Materials and primitives](materials-and-primitives.md) |
| Non-orthogonal screen and text frames | Not supported; refused at every door that accepts a frame | [Materials and primitives](materials-and-primitives.md) |
| Host bounds on the screen material sentinel band | Required on both ends, with the shader bounding the decoded index | [Materials and primitives](materials-and-primitives.md) |
| Degenerate profile bounds sized to the fixed-point representation | Required wherever an exact core divides by its own dimensions; refused at the builder, the packed program constructor, and the document validator | [Materials and primitives](materials-and-primitives.md) |
| Finiteness of packed operand lanes | Required at the packed door, skipping exactly the lanes that carry reinterpreted integer fields | [Materials and primitives](materials-and-primitives.md) |
| Instance ranges as an ownership partition | Required; overlap is refused at the door and the first-match resolve stays as defence in depth | [Instance bounds and ownership](instance-bounds.md) |
| One effective-scale rule shared by bound analysis and emission | Required; an analyzer reading the authored scale disagrees with an emission that clamps it | [Level of detail and bounds](lod-and-bounds.md) |
| Negative authored scale as a mirror | Not supported; refused at the creation document validator in favour of the symmetry domain op | [Rejected and conditional SDF techniques](negative-results-and-rejections.md) |
| Closed-form copy counts before a domain fold expands | Required; an authored chain past the copy budget is refused in O(1) memory | [Level of detail and bounds](lod-and-bounds.md) |
| Rasterized coverage with temporal reconstruction | Shipped; no partial silhouette coverage | [Antialiasing and filtering](antialiasing-and-filtering.md) |
| Temporal reconstruction | Shipped as a per-view quality: a jittered sample a frame, resolved over the view's own history | [Temporal reconstruction](../handbook/frame-rendering.md#temporal-reconstruction) |
| Bound-preserving procedural noise | Shipped as `NoiseDisplace` (integer-hash lattice fBm, quintic-slope derivative bound folded into the step clamp) | [Lipschitz and field correctness](lipschitz-and-field-correctness.md) |
| Sampled carve bricks | Shipped as an invalidatable render cache, not a core representation | [Level of detail and bounds](lod-and-bounds.md) |
| Global voxel or clipmap representation | Not a current fit; requires a distinct content-source boundary | [Rejected and conditional SDF techniques](negative-results-and-rejections.md) |
| MTSDF alpha for marchable glyphs | Preferred field channel | [Text and glyphs](text-and-glyphs.md) |
| Glyph decals for dense reading text | Shipped material-level tier | [Text and glyphs](text-and-glyphs.md) |
| Coverage rasterizers as SDF geometry | Not applicable; they produce coverage, not a marchable distance | [Text and glyphs](text-and-glyphs.md) |

The detail links distinguish implemented behavior from remaining work. The
P6-GI plan tracks the irradiance volume's open work.
