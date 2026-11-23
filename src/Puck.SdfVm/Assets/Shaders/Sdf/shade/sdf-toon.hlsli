// A style's engine half: whether a material is styled. The banding and the ink are the material's shade program (the
// standard's `cel` and `ink`, worlds/standard), never engine code: a style row only marks its material styled, which the
// live world.toon off reads to shade every styled material continuously and uninked, and the built-in curvature terms
// read to leave a styled material alone. Screens and sky never call this module.
// A material that authors a style carries its mode in its row 22 (SdfToon.Mode), and every other material inherits
// the world's default from the pass block (render.toon). A material's mode is 1 for on, -1 for off (enabled: false) and
// 0 to inherit; the world default's is 1 for on and 0 for off, and minus one less that while the live world.toon off
// suppresses every style (-1 for a default authored off, -2 for one authored on).
#ifndef SHADE_SDF_TOON_HLSLI
#define SHADE_SDF_TOON_HLSLI

// Whether the live world.toon off suppresses every style, a material's own included.
bool sdfToonSuppressed() { return passGroup.toonMode < 0.0; }

// Whether a material is styled as authored, the lever aside: its own style when it authors one, else the world's
// default. Every reader of a material's style row asks this, so the suppression and the inheritance have one rule.
bool sdfToonAuthored(float ownMode) {
    float fallback = passGroup.toonMode;
    return (ownMode != 0.0) ? (ownMode > 0.0) : ((fallback > 0.0) || (fallback < -1.5));
}

// Whether a material shades styled: as authored, and never while suppressed.
bool sdfToonStyled(SdfMaterialData material) { return (!sdfToonSuppressed() && sdfToonAuthored(material.toonMode)); }

#endif
