// Cel material response in scene luminance. Screens and sky never call this module.
// A cel style is per material (SdfToon): a material that authors one carries it in its rows 22 to 25, and every other
// material inherits the world's default style from the pass block (render.toon, which world.toon overrides). Both are
// laid out alike: bands (mode, threshold, softness, form radius), shadow (tint), outline (width in output pixels, halving
// distance, crease cosine) and ink (tint; the pass block's w is the render grid the outline width scales by). The mode is
// 0 for off (a material's own -1, so its zero still means inherit), 1 for the band shaping the light's cosine with its
// shadow visibility kept as it was filtered, 2 for the band also stepping that visibility (SdfToonBand).
//
// The lit side is the continuous response, untouched: a style never darkens, desaturates or compresses the key. Each
// direct light's band, b = step(cosine) x shadow, read on the surface's form (its macro normal and form-filtered shadow,
// sdfToonForm) decides where the light's shade side begins; there the surface shows the colour it would have lit, the
// light's continuous diffuse restored to full visibility together with the share of the sky and bounced light that light
// owns, times the tint: one darker, coloured step of the surface's whole light rather than of the key alone
// (sdfToonShade). A light's step reaches only as far as the light reaches the surface's surroundings (its region): a
// form or cast shadow in the open air steps, and a room the light never enters keeps its continuous light.
#ifndef SHADE_SDF_TOON_HLSLI
#define SHADE_SDF_TOON_HLSLI

// The projected cascade texel, in pixels, at which the shadow step keeps the whole filtered penumbra: the filter is at
// least a texel wide, so a sharp step of it draws the texel grid's corners, and once a texel spans this many pixels (a
// far cascade on a receiver grazing the light) those corners read as blocks.
static const float SdfToonShadowBlockPixels = 64.0;

// A style as a pixel shades with it: the authored rows; the band softness, widened at this pixel to what the surface
// resolves; the width of the shadow step for each shadow slot, in visibility; the form the band reads (its normal and
// each slot's shadow visibility filtered over it); and whether the surface stands in the open air.
struct SdfToonStyle {
    float4 bands;
    float4 shadow;
    float4 outline;
    float3 ink;
    float2 shadowStep;
    float3 normal;
    float4 shadows;
    float open;
};

// The style a material shades with: its own when it authors one, else the world's default, as authored. A pixel that
// shades with it reads its form first (sdfToonResolve).
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
    style.shadows = float4(1.0, 1.0, 1.0, 1.0);
    style.open = 0.0;
    return style;
}

bool sdfToonEnabled(SdfToonStyle style) { return style.bands.x > 0.0; }

// The style at one pixel, given its form (sdfToonForm): the normal the band reads and the shadow visibilities it steps.
// The band's softness widens to the larger of the authored value and how fast the band's cosine changes across a
// pixel: `spread`, how far the normals about the pixel part as a cosine change across it, over the form's radius in
// pixels, since a normal read over that many pixels turns that many times more slowly. `shadowTexels` is each shadow
// slot's cascade texel in pixels at the surface (sdfToonShadowTexels): the shadow step keeps at least a pixel's width of
// penumbra, and keeps all of it as the texel grows toward SdfToonShadowBlockPixels.
SdfToonStyle sdfToonResolve(SdfToonStyle style, float3 formNormal, float4 formShadows, float formPixels, float spread, float2 shadowTexels) {
    style.normal = formNormal;
    style.shadows = formShadows;
    style.bands.z = max(style.bands.z, min(spread / max(formPixels, 1.0), 1.0));
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
// visibility as the filter left it; mode 2 steps it about one half over `width`, so a cast shadow is a held shape with a
// soft edge no sharper than the cascade resolves.
float sdfToonBand(SdfToonStyle style, float cosine, float visibility, float width) {
    float shadow = saturate(visibility);
    if (style.bands.x > 1.5) {
        float halfWidth = max(width * 0.5, 0.0005);
        shadow = smoothstep(0.5 - halfWidth, 0.5 + halfWidth, shadow);
    }
    return sdfToonStep(style, cosine) * shadow;
}

// The share of the sky above a surface its probes see open (sdfMeshIrradianceAt, about straight up) at which the surface
// counts as in the open air: a cast shadow there, under a tree or in a building's shadow on the lawn, steps as a form
// shadow does, while a room, which sees almost no sky, keeps its continuous light. The probes sit a storey cell apart, so
// the share varies in broad soft patches; a share past this one reads as open air whole, so those patches never print.
static const float SdfToonOpenSky = 0.25;
float sdfToonOpen(float skyVisibility) {
    return smoothstep(0.0, SdfToonOpenSky, skyVisibility);
}

// One light's cel step at a surface (SdfLightResponse.toon...): `light` is what the light delivers at unit cosine and
// full visibility (its colour, strength, reach and the occlusion the continuous response keeps), `lambert` the
// continuous response's own cosine term at the pixel, `band` the light's banded share and `region` how far the light
// reaches the surface's surroundings. The light's restored diffuse is the continuous one at full visibility, so a cast
// shadow shows the colour its surface would have lit and a form shadow its own continuous light, and no shade side is
// ever brighter than its unshadowed continuous light times the tint; `shade` is how far the step applies, the band's
// shade share within the region; `potential` the light's luminance within the region, by which it owns a share of the
// fill.
struct SdfToonLight {
    float3 restored;
    float shade;
    float potential;
};
SdfToonLight sdfToonLight(float3 light, float lambert, float band, float region) {
    SdfToonLight step;
    float reached = saturate(region);
    step.restored = light * max(lambert, 0.0);
    step.shade = ((1.0 - saturate(band)) * reached);
    step.potential = (dot(light, SDF_LUMINANCE) * reached);
    return step;
}

// The lights' cel steps summed over a walk (sdfToonAdd), and the change they make to the diffuse light a surface reflects
// (sdfToonShade).
struct SdfToonSteps {
    // Each light's step on its own direct light: shade x (tint x restored - diffuse).
    float3 direct;
    // Each light's shade share of its potential, and the potentials summed.
    float shaded;
    float potential;
    // Each light's step on the light the local adaptation meters: shade x (restored - diffuse).
    float3 metered;
};
SdfToonSteps sdfToonStepsStart() {
    return (SdfToonSteps)0;
}
void sdfToonAdd(inout SdfToonSteps steps, SdfToonStyle style, SdfToonLight step, float3 diffuse) {
    steps.direct += (step.shade * ((style.shadow.rgb * step.restored) - diffuse));
    steps.shaded += (step.shade * step.potential);
    steps.potential += step.potential;
    steps.metered += (step.shade * (step.restored - diffuse));
}
// The change to the diffuse light (before albedo) that turns the continuous response into the cel one, given `fill`, the
// sky and bounced light the surface receives. A light owns the share of the fill its potential holds of the lights'
// summed potential, or of the fill itself where the fill outshines every light (a room lit mostly by bounce keeps most of
// its fill whatever one lamp's band says), and its shade side keeps the tint's share of its direct light and of that
// fill: where one light reaches alone, the shade side is the tint times the whole light the surface would have lit.
float3 sdfToonShade(SdfToonStyle style, SdfToonSteps steps, float3 fill) {
    float owned = (steps.shaded / max(max(steps.potential, dot(fill, SDF_LUMINANCE)), 1.0e-6));
    return (steps.direct + ((style.shadow.rgb - 1.0) * (fill * owned)));
}

#endif
