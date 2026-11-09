// Cel material response in scene luminance. Screens and sky never call this module.
// A cel style is per material (SdfToon): a material that authors one carries it in its rows 22 to 25, and every other
// material inherits the world's default style from the pass block (render.toon, which world.toon overrides). Both are
// laid out alike: bands (mode, threshold, softness, specular size), shadow (tint, exposure gain), outline (width in
// output pixels, halving distance, crease cosine, shoulder) and ink (tint; the pass block's w is the render grid the
// outline width scales by). The mode is 0 for off (a material's own -1, so its zero still means inherit), 1 for the band
// shaping the light's cosine with its shadow visibility kept as it was filtered, 2 for the band also stepping that
// visibility (SdfToonBand).
//
// A light's banded share is band = step(cosine) x shadow, and the light reaches the surface as
// light x (band + (1 - band) x region x tint): its lit side takes the light whole, and its shade side, form shadow and
// cast shadow alike, keeps the tint's share of it, the painted shadow colour, as far as the light reaches the region
// around the surface at all (sdfToonRegion). Sky and bounced light are added as they are, so a room no direct light
// reaches shades as it would continuously and only a light's own shadow takes the tint.
#ifndef SHADE_SDF_TOON_HLSLI
#define SHADE_SDF_TOON_HLSLI

// The projected cascade texel, in pixels, at which the shadow step keeps the whole filtered penumbra: the filter is at
// least a texel wide, so a sharp step of it draws the texel grid's corners, and once a texel spans this many pixels (a
// far cascade on a receiver grazing the light) those corners read as blocks.
static const float SdfToonShadowBlockPixels = 64.0;

// A style as a pixel shades with it: the authored rows; the band softness, widened at this pixel to what the surface
// resolves; the width of the shadow step for each shadow slot, in visibility; and the normal the band reads.
struct SdfToonStyle {
    float4 bands;
    float4 shadow;
    float4 outline;
    float3 ink;
    float2 shadowStep;
    float3 normal;
    float open;
};

// The style a material shades with: its own when it authors one, else the world's default, as authored. A pixel that
// shades with it widens it first (sdfToonResolve).
SdfToonStyle sdfToonStyle(SdfMaterialData material) {
    SdfToonStyle style;
    bool own = (material.toonBands.x != 0.0);

    style.bands = (own ? material.toonBands : passGroup.toonBands);
    style.shadow = (own ? material.toonShadow : passGroup.toonShadow);
    style.outline = (own ? material.toonOutline : passGroup.toonOutline);
    style.ink = (own ? material.toonInk : passGroup.toonInk.rgb);
    style.bands.x = max(style.bands.x, 0.0);
    style.outline.x *= passGroup.toonInk.w;
    style.shadowStep = float2(1.0, 1.0);
    style.normal = float3(0.0, 0.0, 0.0);
    style.open = 0.0;
    return style;
}

bool sdfToonEnabled(SdfToonStyle style) { return style.bands.x > 0.0; }

// The style at one pixel. The band reads `bandNormal`, the surface's normal averaged over the pixel's neighbourhood
// (sdfToonGather): a cosine stepped on a normal that turns within a pixel or two (a rock's relief, a baked surface's
// fine detail) draws speckle, which the average leaves to the albedo and the continuous terms. Its softness widens to
// the larger of what the authored value draws and what the surface resolves: `spread`, how far the normals in that
// neighbourhood part (their mean's shortfall from unit length, as a cosine change), so a terminator over a normal that
// varies faster than the pixels spans them instead of stepping between neighbours and crawling as the camera or the
// sun moves; and `triangleSpread`, how far the hit triangle's vertex normals part (sdfMeshNormalSpread), since between
// them the cosine is only interpolated and a sharper step follows the triangle's edges. `shadowTexels` is each shadow
// slot's cascade texel in pixels at the surface (sdfToonShadowTexels): the shadow step keeps at least a pixel's width of
// penumbra, and keeps all of it as the texel grows toward SdfToonShadowBlockPixels.
SdfToonStyle sdfToonResolve(SdfToonStyle style, float3 bandNormal, float spread, float triangleSpread, float2 shadowTexels) {
    style.normal = bandNormal;
    style.bands.z = max(style.bands.z, min(max(spread, triangleSpread), 1.0));
    float2 resolved = max(0.5 / max(shadowTexels, 1.0e-3), shadowTexels / SdfToonShadowBlockPixels);
    style.shadowStep = saturate(max(style.bands.zz, resolved));
    return style;
}

// The band over a response in [0, 1]: a soft step of the style's softness about its threshold.
float sdfToonStep(SdfToonStyle style, float response) {
    float halfWidth = max(style.bands.z * 0.5, 0.0005);
    return smoothstep(style.bands.y - halfWidth, style.bands.y + halfWidth, saturate(response));
}

// A shadow slot's step width for a light (sdfToonResolve), one for a light that holds no slot.
float sdfToonShadowWidth(SdfToonStyle style, int lightIndex) {
    return ((lightIndex >= 0) && (lightIndex == passGroup.shadowSlots.x)) ? style.shadowStep.x
        : (((lightIndex >= 0) && (lightIndex == passGroup.shadowSlots.y)) ? style.shadowStep.y : 1.0);
}

// A light's banded share at a cosine (a wrapped one included) under its filtered shadow visibility. Mode 1 keeps the
// visibility as the cascades' filter left it; mode 2 steps it about one half over `width`, so a cast shadow is a held
// shape with a soft edge no sharper than the cascade resolves.
float sdfToonBand(SdfToonStyle style, float cosine, float visibility, float width) {
    float shadow = saturate(visibility);
    if (style.bands.x > 1.5) {
        float halfWidth = max(width * 0.5, 0.0005);
        shadow = smoothstep(0.5 - halfWidth, 0.5 + halfWidth, shadow);
    }
    return sdfToonStep(style, cosine) * shadow;
}

// The share of the sky above a surface its probes see open (sdfMeshIrradianceAt, about straight up) at which the surface
// counts as in the open air: a cast shadow there, under a tree or in a building's shadow on the lawn, keeps the shade
// side's tint as a form shadow does, while a room, which sees almost no sky, does not. The probes sit a storey cell apart,
// so the share varies in broad soft patches; a share past this one reads as open air whole, so those patches never print.
static const float SdfToonOpenSky = 0.25;
float sdfToonOpen(float skyVisibility) {
    return smoothstep(0.0, SdfToonOpenSky, skyVisibility);
}

// What a light delivers at a surface per unit of its strength: the whole on the lit side, the tint's share on the shade
// side as far as `region`, the share of the light reaching the surface's surroundings, goes.
float3 sdfToonShare(SdfToonStyle style, float band, float region) {
    return (band + ((1.0 - band) * saturate(region)) * style.shadow.rgb);
}

float3 sdfToonSpecular(SdfToonStyle style, SdfMaterialData material, float3 normal, float3 view, float3 light, float radiance, float band) {
    float3 halfway = light + view;
    float lengthSquared = dot(halfway, halfway);
    float cosine = dot(normal, halfway * rsqrt(max(lengthSquared, 1.0e-8)));
    float size = style.bands.w * lerp(0.5, 1.5, material.roughness);
    // A narrow analytic edge survives subpixel movement without an unbounded GGX peak.
    float highlight = smoothstep(1.0 - size, 1.0 - size + min(size * 0.15, 0.003), cosine);
    float3 reflectance = lerp(material.specular.xxx, material.albedo, material.metal);
    float gain = (1.0 - 0.75 * material.roughness) * 2.0;
    return reflectance * (highlight * gain * radiance * band);
}

// Compress all channels by the same factor, preserving paint chroma. Emission is added afterwards.
float3 sdfToonExpose(SdfToonStyle style, float3 reflected) {
    float3 exposed = max(reflected, 0.0) * (passGroup.exposure * style.shadow.w);
    float peak = max(exposed.r, max(exposed.g, exposed.b));
    return exposed / ((1.0 + peak * style.outline.w) * max(passGroup.exposure, 1.0e-12));
}

#endif
