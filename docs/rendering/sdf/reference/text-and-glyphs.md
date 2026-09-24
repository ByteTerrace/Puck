# Text and glyphs

Puck supports two text tiers with different correctness requirements:
marchable glyph geometry and material-level glyph decals. Both use the shared
glyph atlas, but they consume different representations of the data.

## The glyph atlas

`SdfGlyphAtlas` carries the one packed RGBA image the SDF engine binds.
`Puck.Text` keeps each logical font's metrics and glyph rectangles in a
`PackedFontAtlasCatalog`, with every rectangle remapped into that image.

`ManagedFontAtlasGenerator` is the production in-process source for author-provided
OpenType fonts and collections. Puck reads Unicode mappings, metrics, simple and
composite TrueType quadratic outlines, and CFF/CFF2 Type 2 cubic charstrings. It
packs per-glyph rectangles in shelves and evaluates a multi-channel signed-distance representation from
the outlines. The RGB median reconstructs sharp corners for coverage; neither
the median nor filtering should be treated as an exact or universally
1-Lipschitz marchable distance.
The generator resolves overlapping contours into filled boundaries before
coloring; its approximation and refusal limits are documented in
[the text reference](../../../reference/text.md#generation-options-and-glyph-selection).
World and CLI authors can select a zero-based TTC/OTC face; CFF2 variable outlines
currently use default design coordinates. Pair kerning is flattened from GPOS pair
positioning (the `kern` feature, PairPos formats 1 and 2, extension lookups included)
or, when GPOS yields none, the legacy horizontal `kern` table; contextual positioning
is not read. Pre-baked MTSDF atlases remain valid for fixed engine UI.

Coverage-only images are suitable for decals, not for marchable geometry. The
managed outline generator is the supported path for generated glyph fields.

The render node uploads an atlas when its immutable catalog reference changes.
Consequently, loading or reloading a world can replace or remove its font set
without rebuilding the SDF engine; unchanged frames do not repeat the upload.

## Marchable geometry

`SdfProgramBuilder.Text` uses `Puck.Text.TextLayout`, then emits a transformed
`Glyph` segment for each character. The shape stores packed atlas coordinates,
world dimensions, extrusion depth, the atlas-to-world distance scale, and a
sampling correction in `Data1.w`.
Layout options ride `TextLayoutOptions` (greedy wrap width, block alignment,
tracking, line-height scale), and a `dynamicSlot` argument prefixes each glyph
chain with `TransformDynamic`, so a whole run follows a dynamic transform's
per-frame pose—how World's replay stamp pool moves lettering with an
animated, inhabited, or attached placement while frame replay moves the
shapes.

The host computes a reciprocal derivative bound from the immutable decoded atlas
pixels and the actual unorm16-packed UV span divided by the cell's world size.
This includes quantization, bilinear filtering, and stretched cells; metadata-only
atlases use the worst-case RGBA8 slope. The shader applies this correction only
to the sampled alpha term, then takes its maximum with the exact cell-box field
and extrudes it. The correction bounds the reconstructed field's slope; it does
not restore the exact source outline or remove approximation error. The uploaded
pixels and dimensions must be those used to build the glyph instructions.

The shader samples the alpha distance only near the glyph's bounding quad. Far
from the surface it returns the conservative quad field, which keeps culling
and marching safe. Glyph geometry can be unioned, subtracted, embossed, or
engraved like other shapes.

Do not march the median of RGB channels. Median reconstruction is continuous
enough for coverage but is not guaranteed to be a conservative signed-distance
field at channel conflicts.

The fixed-grid overlay pack is a separate consumer: it accepts only equal-sized,
integer-aligned cells contained in the image and explicitly refuses variable-cell
atlases. Generated proportional text uses the layout-based path.

## Glyph decals

`GlyphDecal` samples text during shading on a `ScreenSlab`. It is intended for
dense labels and reading text where adding one geometry segment per glyph would
be wasteful. Decals do not participate in the distance field and therefore
cannot carve or cast geometric silhouettes. World documents author this tier as
a screen source (`{"$type": "text", ...}` on a `screens[]` row or a placement's
creation-face override): the frame source bakes the lines into per-cell words
against the packed catalog and the engine change-detects the upload
(`SetScreenDecal`).

Keep the carrier surface and decal frame aligned. Coplanar glyph geometry and a
slab should not be used as a substitute for decals because coincident zero sets
produce unstable material ownership.

## Layout and determinism

Font selection, atlas identity, and glyph metrics are input data. Do not depend
on an ambient system font for a deterministic replay. Document-authored fonts
must be relative to the document, content-pinned, and subset to declared Unicode
scalar ranges. Pre-baked assets should record their source license and generation
settings.

The current layout path maps one Unicode scalar at a time. Atlas rows retain the
source glyph identifier, and the model permits glyph-ID-only rows, so a future
generator can include a GSUB substitution closure without changing the atlas
contract. Today's generator includes only directly mapped glyphs. GSUB,
bidirectional reordering, script mark positioning, cluster positioning, and
fallback-chain selection still require an explicit shaping/fallback layer.
WOFF/WOFF2 and color-font inputs, and authored CFF2 variation axes, are not
implemented.

## Choosing a tier

| Requirement | Use |
|---|---|
| Engraving, embossing, silhouette, or field composition | `Glyph` geometry |
| Dense labels on a known surface | `GlyphDecal` |
| Emulator or arbitrary framebuffer content | Screen source |
| Rich script shaping | Shape text before building SDF content |

Validate atlas changes with a deterministic fixture, absent-atlas fallback,
cross-backend image parity, and both near and minified views.
