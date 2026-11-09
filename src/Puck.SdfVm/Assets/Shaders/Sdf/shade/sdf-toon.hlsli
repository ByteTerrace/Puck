// Cel material response in scene luminance. Screens and sky never call this module.
// A cel style is per material (SdfToon): a material that authors one carries it in its rows 22 to 25, and every other
// material inherits the world's default style from the pass block (render.toon, which world.toon overrides). Both are
// laid out alike: bands (mode, threshold, softness, specular size), shadow (tint, exposure gain), outline (width in
// output pixels, halving distance, crease cosine, shoulder) and ink (tint; the pass block's w is the render grid the
// outline width scales by). The mode is 0 for off (a material's own -1, so its zero still means inherit), 1 for the band
// shaping the light's cosine with its visibility kept as a separate factor, 2 for the band over cosine times visibility.
#ifndef SHADE_SDF_TOON_HLSLI
#define SHADE_SDF_TOON_HLSLI

struct SdfToonStyle {
    float4 bands;
    float4 shadow;
    float4 outline;
    float3 ink;
};

// The style a material shades with at a pixel: its own when it authors one, else the world's default. Curvature is the
// surface's mean curvature (1/m) and footprint the pixel's width on it (m): across one pixel the normal turns by about
// their product in radians, so a cosine moves by up to that much. The band's softness is widened to at least that, so
// the terminator of a small or distant curved shape spans a pixel instead of stepping between neighbours and crawling
// as the camera or the sun moves; a terminator wider than a pixel keeps the authored softness.
SdfToonStyle sdfToonStyle(SdfMaterialData material, float curvature, float footprint) {
    SdfToonStyle style;
    bool own = (material.toonBands.x != 0.0);

    style.bands = (own ? material.toonBands : passGroup.toonBands);
    style.shadow = (own ? material.toonShadow : passGroup.toonShadow);
    style.outline = (own ? material.toonOutline : passGroup.toonOutline);
    style.ink = (own ? material.toonInk : passGroup.toonInk.rgb);
    style.bands.x = max(style.bands.x, 0.0);
    style.bands.z = max(style.bands.z, min(abs(curvature) * footprint, 1.0));
    style.outline.x *= passGroup.toonInk.w;
    return style;
}

bool sdfToonEnabled(SdfToonStyle style) { return style.bands.x > 0.0; }

// The band over a response in [0, 1]: a soft step of the style's softness about its threshold.
float sdfToonStep(SdfToonStyle style, float response) {
    float halfWidth = max(style.bands.z * 0.5, 0.0005);
    return smoothstep(style.bands.y - halfWidth, style.bands.y + halfWidth, saturate(response));
}

// A light's banded diffuse share at a cosine (a wrapped one included) under its visibility. The default shape bands the
// cosine alone and multiplies the visibility after, so the cascades' filtered penumbra keeps its softness and the
// band's edge stays where the surface turns from the light; the combined shape (mode 2) bands both together, the harder
// look in which a penumbra snaps to the band's edge.
float sdfToonBand(SdfToonStyle style, float cosine, float visibility) {
    return ((style.bands.x > 1.5) ? sdfToonStep(style, saturate(cosine) * saturate(visibility))
        : (sdfToonStep(style, cosine) * saturate(visibility)));
}

float3 sdfToonSpecular(SdfToonStyle style, SdfMaterialData material, float3 normal, float3 view, float3 light, float radiance, float visibility) {
    float3 halfway = light + view;
    float lengthSquared = dot(halfway, halfway);
    float cosine = dot(normal, halfway * rsqrt(max(lengthSquared, 1.0e-8)));
    float size = style.bands.w * lerp(0.5, 1.5, material.roughness);
    // A narrow analytic edge survives subpixel movement without an unbounded GGX peak.
    float highlight = smoothstep(1.0 - size, 1.0 - size + min(size * 0.15, 0.003), cosine);
    float3 reflectance = lerp(material.specular.xxx, material.albedo, material.metal);
    float gain = (1.0 - 0.75 * material.roughness) * 2.0;
    return reflectance * (highlight * gain * radiance * sdfToonBand(style, dot(normal, light), visibility));
}

// Compress all channels by the same factor, preserving paint chroma. Emission is added afterwards.
float3 sdfToonExpose(SdfToonStyle style, float3 reflected) {
    float3 exposed = max(reflected, 0.0) * (passGroup.exposure * style.shadow.w);
    float peak = max(exposed.r, max(exposed.g, exposed.b));
    return exposed / ((1.0 + peak * style.outline.w) * max(passGroup.exposure, 1.0e-12));
}

#endif
