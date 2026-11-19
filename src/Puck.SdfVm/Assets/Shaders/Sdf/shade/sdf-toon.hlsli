// A cel style's engine half: whether a material is styled and the ink it outlines with. The banding itself is the
// material's shade program (the standard's `cel`, worlds/standard/shading.puck), never engine code: a style row only
// marks its material styled and carries what a per-pixel program cannot express, the outline that reads neighbour depth
// and normal (sdf-toon-outline.hlsli). Screens and sky never call this module.
// A material that authors a style carries it in its rows 22 and 25 (SdfToon.WriteRows), and every other material
// inherits the world's default style from the pass block (render.toon). Both are laid out alike: outline (mode, width in
// output pixels, halving distance, crease cosine) and ink (tint; the pass block's w is the render grid the outline width
// scales by, a material's w its glass shadow density). A material's mode is 1 for on, -1 for off (enabled: false) and 0
// to inherit; the world default's is 1 for on and 0 for off, and minus one less that while the live world.toon off
// suppresses every style (-1 for a default authored off, -2 for one authored on).
#ifndef SHADE_SDF_TOON_HLSLI
#define SHADE_SDF_TOON_HLSLI

struct SdfToonStyle {
    float mode;
    float3 outline;
    float3 ink;
};

// Whether the live world.toon off suppresses every style, a material's own included.
bool sdfToonSuppressed() { return passGroup.toonOutline.x < 0.0; }

// Whether a material is styled as authored, the lever aside: its own style when it authors one, else the world's
// default. Every reader of a material's style row asks this, so the suppression and the inheritance have one rule.
bool sdfToonAuthored(float ownMode) {
    float fallback = passGroup.toonOutline.x;
    return (ownMode != 0.0) ? (ownMode > 0.0) : ((fallback > 0.0) || (fallback < -1.5));
}

// The style a material shades with: its own when it authors one, else the world's default; off while suppressed.
SdfToonStyle sdfToonStyle(SdfMaterialData material) {
    SdfToonStyle style;
    bool own = (material.toonOutline.x != 0.0);

    style.mode = ((!sdfToonSuppressed() && sdfToonAuthored(material.toonOutline.x)) ? 1.0 : 0.0);
    style.outline = (own ? material.toonOutline.yzw : passGroup.toonOutline.yzw);
    style.ink = (own ? material.toonInk : passGroup.toonInk.rgb);
    style.outline.x *= passGroup.toonInk.w;
    return style;
}

bool sdfToonEnabled(SdfToonStyle style) { return style.mode > 0.0; }

#endif
