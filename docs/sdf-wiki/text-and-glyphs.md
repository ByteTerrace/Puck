# Text and glyphs

Puck supports two text tiers with different correctness requirements:
marchable glyph geometry and material-level glyph decals. Both use the shared
glyph atlas, but they consume different representations of the data.

## Glyph atlas

`SdfGlyphAtlas` carries RGBA pixels, dimensions, distance range, and layout
metadata. A pre-baked MTSDF atlas is the preferred authored source. Its alpha
channel contains the true signed distance used by geometry; RGB channels may
support median reconstruction for coverage-oriented consumers.

`SdfCoverageAtlas.Generate` provides a deterministic fallback. It rasterizes
coverage and computes an exact separable Euclidean distance transform. The
fallback avoids a mandatory external toolchain but does not replace authored
font provenance and layout data.

## Marchable geometry

`SdfProgramBuilder.Text` uses `Puck.Text.TextLayout`, then emits a transformed
`Glyph` segment for each character. The shape stores packed atlas coordinates,
world dimensions, extrusion depth, and the atlas-to-world distance scale.

The shader samples the alpha distance only near the glyph's bounding quad. Far
from the surface it returns the conservative quad field, which keeps culling
and marching safe. Glyph geometry can be unioned, subtracted, embossed, or
engraved like other shapes.

Do not march the median of RGB channels. Median reconstruction is continuous
enough for coverage but is not guaranteed to be a conservative signed-distance
field at channel conflicts.

## Glyph decals

`GlyphDecal` samples text during shading on a `ScreenSlab`. It is intended for
dense labels and reading text where adding one geometry segment per glyph would
be wasteful. Decals do not participate in the distance field and therefore
cannot carve or cast geometric silhouettes.

Keep the carrier surface and decal frame aligned. Coplanar glyph geometry and a
slab should not be used as a substitute for decals because coincident zero sets
produce unstable material ownership.

## Layout and determinism

Font selection, shaping, kerning, atlas identity, and glyph metrics are input
data. Do not depend on an ambient system font for a deterministic replay.
Pre-baked assets should record their source license and generation settings.

The current layout path supports the metrics exposed by `Puck.Text`. Advanced
OpenType shaping, bidirectional text, and script-specific substitution require
an explicit shaping layer rather than ad hoc glyph remapping in the SDF VM.

## Choosing a tier

| Requirement | Use |
|---|---|
| Engraving, embossing, silhouette, or field composition | `Glyph` geometry |
| Dense labels on a known surface | `GlyphDecal` |
| Emulator or arbitrary framebuffer content | Screen source |
| Rich script shaping | Shape text before building SDF content |

Validate atlas changes with a deterministic fixture, absent-atlas fallback,
cross-backend image parity, and both near and minified views.
