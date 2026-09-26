// The environment and screen-light rows, the screen surfaces and sources, the decals and the bounded volumes.
#ifndef SHADE_SDF_ENVIRONMENT_HLSLI
#define SHADE_SDF_ENVIRONMENT_HLSLI
// The ENVIRONMENT block: SdfEnvironment's lanes, row for row, after the far-field row. KEEP IN SYNC with
// SdfEnvironment (row layout, blend kinds) and SdfWorldEngine.PackEnvironment (the host bakes: unit directions, the
// sun-disc exponent, the twinkle period, the integrated cloud offsets and spin).
static const uint SdfEnvBase = 40u;
static const uint SdfEnvControl = (SdfEnvBase + 0u);     // x light count, y shadow light index (-1 none), z sky enabled, w fog density
static const uint SdfEnvLights = (SdfEnvBase + 1u);      // 3 rows per light: (direction.xyz - a position for a point light, weight) (color.rgb, kind) (param, shadows, dynamicSlot - point only else 0, 0)
static const uint SdfEnvMaxLights = 8u;
static const uint SdfEnvRowsPerLight = 3u;
static const uint SdfEnvCurvatureA = (SdfEnvBase + 25u); // cavity, rim, ink, ink band low
static const uint SdfEnvCurvatureB = (SdfEnvBase + 26u); // ink color.rgb, ink band high
static const uint SdfEnvSkyControl = (SdfEnvBase + 27u); // x gradient stop count, y sun-disc light index (-1 none), z sun-disc pow() exponent, w sun-disc intensity
static const uint SdfEnvSkyStops = (SdfEnvBase + 28u);   // 4 rows: color.rgb, elevation in [-1, 1], ascending
static const uint SdfEnvStars = (SdfEnvBase + 32u);      // density, brightness, seed, 0
static const uint SdfEnvTwinkle = (SdfEnvBase + 33u);    // share, depth, period in engine ticks, 0
static const uint SdfEnvCloudsA = (SdfEnvBase + 34u);    // color.rgb, coverage
static const uint SdfEnvCloudsB = (SdfEnvBase + 35u);    // softness, scale, seed, 0
static const uint SdfEnvCloudsC = (SdfEnvBase + 36u);    // layer offset.xy, shaping offset.xy
static const uint SdfEnvCloudsD = (SdfEnvBase + 37u);    // spin angle, curl, 0, 0
static const uint SdfEnvSoftboxControl = (SdfEnvBase + 38u); // x softbox count, y tonemap mode (0 none, 1 filmic), 0, 0
static const uint SdfEnvSoftboxes = (SdfEnvBase + 39u);   // 3 rows per softbox: (direction.xyz, weight) (color.rgb, sizeW) (sizeH, blur, 0, 0)
static const uint SdfEnvMaxSoftboxes = 4u;
static const uint SdfEnvRowsPerSoftbox = 3u;
static const uint SdfEnvHorizonLow = (SdfEnvBase + 51u);  // studio reflection horizon low (ground-ward) color.rgb
static const uint SdfEnvHorizonHigh = (SdfEnvBase + 52u); // studio reflection horizon high (sky-ward) color.rgb
static const uint SdfEnvLightDirectional = 0u;
static const uint SdfEnvLightHemisphere = 1u;
static const uint SdfEnvLightRim = 2u;
static const uint SdfEnvLightPoint = 3u;
static const uint SdfEnvLightOccluder = 4u;
static const uint SdfTonemapNone = 0u;
static const uint SdfTonemapFilmic = 1u;

#ifdef SDF_SCREEN_SOURCES
// A declared ScreenSlab instance's world-space front-face frame (see Puck.SignedDistance.SdfScreenSurface) — Stage 1 ONLY
// (binding 10/11 are not part of the beam prepass's descriptor set). Indexed DIRECTLY by screen index
// (0..31, the same slot SetScreenSource/screenSources binds) — not by declaration order — so a hit resolves its
// surface with no search; an unfilled slot's entry is never read (no material id can address it: the host packs an
// entry only when SdfProgramBuilder registers that screen index).
struct ScreenSurfaceData {
    float4 right;   // xyz = unit world-space U axis, w = half-width
    float4 up;      // xyz = unit world-space V axis (V=0 at top), w = half-height
    float4 origin;  // xyz = world-space front-face center, w = unused (pad)
};
static const uint WorldScreenSurfaceRows = 3u;
ScreenSurfaceData worldScreenSurface(uint screenIndex) {
    uint row = (screenIndex * WorldScreenSurfaceRows);
    ScreenSurfaceData data;
    data.right = screenSurfaces[row];
    data.up = screenSurfaces[(row + 1u)];
    data.origin = screenSurfaces[(row + 2u)];
    return data;
}
// The screenSurfaces[] / sdfDecalCells[] / screenSourceN entry count — the width every screen index is bounded
// against before it indexes one. KEEP IN SYNC with SdfProgramBuilder.MaxScreenSurfaces.
static const uint SdfScreenSurfaceCount = 32u;
// The screen source images — one sampled image per screen index (screenSource0..screenSource31), all read through the
// views set's one nearest screenSampler, so emulator pixels stay crisp. Slots with no source bound this frame
// (passGroup.screenMask bit clear) hold a valid filler view; the shader never samples an unbound slot (screenSourceBound
// gates it), so the filler's content never reaches the image. (screenMask is a single uint, so exactly 32 screen bits
// fit — raising past 32 needs a second mask word.)
// Per-frame screen LIGHT records (sdfScreenLights): entries 0..31 carry each
// screen's emitted light (rgb = the framebuffer's average color this frame, a = intensity gain), entry 32 is the
// ENVIRONMENT (x = ambient scale, y = sun scale — dim the room so the glow dominates; z/w = the SLICE debug view's
// plane selector: z = axis (0 camera-locked, 1/2/3 world X/Y/Z), w = the axis plane's signed offset — see
// SdfFrame.DebugSliceAxis; read only by debug view mode 7). A light's geometry
// (position/orientation/extent) is the SAME screenSurfaces[i] entry above — a screen is an area emitter, so it needs
// only its color here. KEEP IN SYNC with SdfWorldEngine's screen-light buffer packing.
static const uint SdfScreenLightEnv = SdfScreenSurfaceCount;

// Grid-lock overlay rows (grid-locking §4a): FOUR float4 rows AFTER the env entry (env stays at 32 — load-bearing as
// the screen-count loop bound above). KEEP IN SYNC with SdfWorldEngine.PackScreenLights + SdfFrame's Grid* fields.
static const uint SdfGridWorld = 33u;      // x = flags (bit0 world floor grid, bit1 object grid), y = floorY, zw = world pitch (X, Z)
static const uint SdfGridObjOrigin = 34u; // xyz = reference origin (world), w = object pitch X
static const uint SdfGridObjFrame = 35u;  // xyzw = reference frame quaternion
static const uint SdfGridObjParams = 36u; // x = object pitch Z, y = patch radius (reference-local), z = analytic-normal A/B, w = shadow-cull A/B
// Engine-bench shader-feature params: x = disable soft shadows, y = disable AO, z = shadow-distance
// scale (0 = the full 1.0 reach), w = disable screen lights. KEEP IN SYNC with SdfWorldEngine.PackScreenLights + SdfFrame's
// DisableSoftShadows/DisableAmbientOcclusion/ShadowDistanceScale/DisableScreenLights fields.
static const uint SdfBenchParams = 37u;
// The engine-bench SHADOW-PROXY params row (PATH B): x = enable the shadow proxy (shadow rays skip Subtraction-family
// carve instances and march the pre-carve union hull — sdf.shadow-proxy; 0 = OFF, the default, so an unset frame uploads
// 0 and is byte-identical); y = use the camera-tile shadow mask instead of the per-pixel shadow-grid gather; z = use the
// bounded-cost fast soft-shadow marcher; w
// reserved. A SEPARATE row from SdfBenchParams (whose four lanes are full). KEEP IN SYNC with
// SdfWorldEngine.PackScreenLights + SdfFrame's EnableShadowProxy/UseCameraTileShadowMask/UseFastSoftShadowMarch fields.
static const uint SdfShadowProxyParams = 38u;
// The F1 FAR-FIELD lever row: x = disable the beam-published per-tile far bound (1 = the A/B
// "off" side — the fine march ignores plane 3 and runs to the far distance exactly as pre-F1; 0 = the DEFAULT shipped
// behavior with the far bound ACTIVE, so an unset frame uploads 0 and the feature is ON); y = disable the F2 shadow
// light-side exit (RESERVED for F2, not yet consumed); zw reserved. A SEPARATE row from SdfShadowProxyParams (whose
// lanes carry the shadow proxy). KEEP IN SYNC with SdfWorldEngine.PackScreenLights + SdfFrame's DisableFarBound field.
static const uint SdfFarFieldParams = 39u;

float4 worldEnvRow(uint row) { return sdfScreenLights[row]; }

static const float GridFadeDistance = 32.0;                       // the world grid fades to flat past this (far-field anti-moire)
static const float GridGrazeCos = 0.30;                           // bands vanish as the view flattens against the plane
static const float3 GridWorldLineColor = float3(0.34, 0.56, 0.95);  // cool — the world floor lattice
static const float3 GridObjectLineColor = float3(0.96, 0.66, 0.28); // warm — the reference's own lattice

// CRT glass-face knobs. The tuned look is a FLAT SQUARE tube: no pincushion bulge, near-square corners, a thin crisp
// dark bezel, faint aperture-grille stripes, subtle native-line scanlines, and a soft bright-pixel bloom knee — so the
// screen reads dead-flat and even, and a game on it looks almost exactly like a real handheld panel scaled up.
// Everything is continuous (smoothstep/cos), so a cross-backend ±1-LSB UV delta never flips a hard edge.
//
// The three knobs currently at 0 (curvature, vignette, glint) are LIVE and free: DXC emits `fmul fast`, so each
// zero folds and its whole chain — including the glint's cross + normalize + pow — dead-code-eliminates on BOTH
// backends (measured: enabling all three grows the views kernel by 224 DXIL / 348 SPIR-V bytes). Raise one and the
// effect it names comes back. Do NOT #if them: that would trade a free runtime knob for a compile-time one.
static const float CrtCurvature = 0.0;       // pincushion bulge about the screen centre (0 = flat glass)
static const float CrtBezel = 0.03;          // a thin bezel
static const float CrtCornerRadius = 0.004;  // corner rounding of the bezel mask (near-square)
static const float CrtBezelSoft = 0.008;     // a crisp bezel edge
static const float CrtScanAmplitude = 0.06;  // subtle scanlines — a hint of CRT, not a filter
static const float CrtScanLines = 144.0;
static const float CrtApertureGrille = 0.05; // aperture-grille strength — barely there (0 = off, purely additive)
static const float CrtGrilleColumns = 160.0; // vertical RGB phosphor-stripe triads across the screen width
static const float CrtVignette = 0.0;        // radial corner darkening (0 = flat, even brightness)
static const float CrtBloomGain = 0.5;
static const float CrtBloomThreshold = 0.6;
static const float CrtGlint = 0.0;           // fresnel rim brighten at glancing angles (0 = no glass glint)
static const float CrtGlintPower = 3.0;
static const float ScreenLightFalloff = 0.28; // the room glow's inverse-square softening
// Rec.601 luma weights, for the bloom knee's brightness test.
static const float3 CrtLumaWeights = float3(0.299, 0.587, 0.114);
// The aperture grille's three phosphor stripes, 120 degrees apart (2pi/3, 4pi/3), so each channel peaks in its own
// column third. Spelled as literals rather than SDF_TAU/3: the divide would round differently by an ULP.
static const float3 CrtGrillePhase = float3(0.0, 2.0943951023931953, 4.1887902047863905);

// === GLYPH DECAL: the material-level text tier ======================================================================
// Dense reading text sampled AT THE HIT on a ScreenSlab carrier (like sampleScreenSurface samples a screen image), NOT
// marched as geometry (the SdfShapeType.Glyph op is that path — this is an ADDITIVE material flavor that leaves world
// glyphs completely untouched). The carrier is a per-screen DECAL TABLE bound to the SAME screen-surface frame the
// image path uses; a screen slot in decal mode samples a grid of glyph cells + colours instead of a bound image. This
// is the ONE tier where 2D coverage reconstruction is legitimate (its designed job): the atlas ALPHA is a
// single-channel coverage-SDF, sampled with a coverage threshold + a screen-projected AA half-width derived
// ANALYTICALLY from the hit's pixel footprint (NO fwidth — deterministic, from the same pixelFootprint*traveled the
// coverage-AA epilogue uses). KEEP IN SYNC with SdfWorldEngine's decal-buffer packing (SetDecalDescriptor/SetDecals)
// and SdfProgram. LAYOUT (sdfDecalCells, one uint4 per entry): the first SdfDecalDescriptorCount (== SdfWorldEngine.MaxScreenSurfaces)
// entries are the PER-SCREEN descriptors, then the shared CELL region.
//   descriptor[screenIndex] = (gridCols, gridRows, cellBase, asuint(distanceRange)); gridCols == 0 => that screen has
//                             NO decal this frame (the image/procedural path applies) — an all-zero buffer is inert, so
//                             a program that declares no decal renders byte-identically.
//   cell[i]                 = (packedUvTopLeft, packedUvBottomRight [unorm2x16, sdfGlyphUnpackUv], fgRgba8, bgRgba8);
//                             a BLANK cell packs uvTopLeft == uvBottomRight (a real glyph never has zero UV extent).
#if defined(SDF_GLYPH_ATLAS)
static const uint SdfDecalDescriptorCount = 32u; // == SdfWorldEngine.MaxScreenSurfaces (the per-screen descriptor band)
// Minimum AA half-width in encoded-coverage units. This keeps a 1:1 glyph edge from collapsing to a hard one-bit step.
static const float DecalMinAa = 0.03125;
float3 sdfDecalUnpackRgb(uint packed) {
    return (float3(float(packed & 0xFFu), float((packed >> 8u) & 0xFFu), float((packed >> 16u) & 0xFFu)) * (1.0 / 255.0));
}
// Samples the glyph-cell grid a decal-mode screen carries at the surface UV (v = 0 at top, matching sampleScreenSurface).
// footprintDiameter = the hit pixel's world diameter (pixelFootprint * traveled) — the analytic AA source. Returns the
// composed fg-over-bg colour; the caller treats it emissive exactly like a sampled screen image.
float3 sdfSampleGlyphDecal(uint4 descriptor, float2 uv, float halfWidth, float footprintDiameter) {
    float2 grid = float2(float(descriptor.x), float(descriptor.y));
    float2 cellF = (saturate(uv) * grid);
    int2 cell = clamp(int2(floor(cellF)), int2(0, 0), (int2(descriptor.xy) - int2(1, 1)));
    uint cellIndex = ((descriptor.z + ((uint)cell.y * descriptor.x)) + (uint)cell.x);
    uint4 c = sdfDecalCells[cellIndex];
    float3 background = sdfDecalUnpackRgb(c.w);

    if (c.x == c.y) {
        return background; // a blank cell (zero UV extent) — just the cell background.
    }

    float2 uvTopLeft = sdfGlyphUnpackUv(asfloat(c.x));
    float2 uvBottomRight = sdfGlyphUnpackUv(asfloat(c.y));
    float2 atlasUv = lerp(uvTopLeft, uvBottomRight, frac(cellF));
    // MEDIAN-OF-3 reconstruction — legitimate HERE because a decal is a shade-time coverage threshold, not marched
    // geometry (the C2 ruling bans median only from the march). A replicated single-channel atlas medians to exactly
    // its alpha; a true MTSDF atlas medians to sharp corners. 0.5 = edge, > 0.5 inside.
    float encoded = sdfGlyphSampleFieldMedian(atlasUv);

    // Analytic AA: the hit's world footprint projected into atlas texels, then into encoded-coverage units. A wider
    // footprint (far / grazing) ramps softer; a 1:1 walk-up ramps over ~one texel. distanceRange 0 (a raw coverage
    // atlas) treats one texel as the full 0..1 ramp; an SDF atlas ramps 1/distanceRange per texel.
    uint2 udims;
    sdfGlyphAtlas.GetDimensions(udims.x, udims.y);

    float cellWorldWidth = ((2.0 * halfWidth) / max(grid.x, 1.0));
    float texelsPerWorld = (((uvBottomRight.x - uvTopLeft.x) * float(udims.x)) / max(cellWorldWidth, 1.0e-6));
    float footprintTexels = (footprintDiameter * texelsPerWorld);
    float distanceRange = asfloat(descriptor.w);
    float encodedPerTexel = ((distanceRange > 0.0) ? (1.0 / distanceRange) : 1.0);
    float aaHalf = clamp((0.5 * footprintTexels * encodedPerTexel), DecalMinAa, 0.5);
    float coverage = smoothstep((0.5 - aaHalf), (0.5 + aaHalf), encoded);

    return lerp(background, sdfDecalUnpackRgb(c.z), coverage);
}
#endif

// Bounded emissive volumes (Puck.SignedDistance.SdfVolume — a participating medium, never a distance-field shape):
// sdfVolumes, an 11-float4-per-volume table. Stage 1 is the only kernel that shades, so it is the only one that reads it.
// Decoded and integrated by shade-volumes.hlsli in renderView and the sky prepass. KEEP IN SYNC with
// SdfWorldEngine.PackVolumes / SdfProgramBuilder.MaxVolumes.
static const uint SdfVolumeCount = 64u;
#include "shade-volumes.hlsli"

bool screenSourceBound(uint screenIndex) {
    return (0u != (passGroup.screenMask & (1u << screenIndex)));
}
// One past the highest bound screen slot (0 when screenMask is 0) — firstbithigh(0) is undefined, so that case is
// guarded explicitly rather than relied on to return -1.
uint screenLightLoopBound() {
    return ((0u == passGroup.screenMask) ? 0u : (firstbithigh(passGroup.screenMask) + 1u));
}
float4 sampleScreenSource(uint screenIndex, float2 uv) {
    // Every screenSamplerN carries the SAME filter (NEAREST) — the thirty-two-way split is purely to give DXC one
    // sampler symbol per register; there is exactly one LOGICAL sampler behavior on either backend.
    switch (screenIndex) {
        case 0:  return screenSource0.SampleLevel(screenSampler, uv, 0);
        case 1:  return screenSource1.SampleLevel(screenSampler, uv, 0);
        case 2:  return screenSource2.SampleLevel(screenSampler, uv, 0);
        case 3:  return screenSource3.SampleLevel(screenSampler, uv, 0);
        case 4:  return screenSource4.SampleLevel(screenSampler, uv, 0);
        case 5:  return screenSource5.SampleLevel(screenSampler, uv, 0);
        case 6:  return screenSource6.SampleLevel(screenSampler, uv, 0);
        case 7:  return screenSource7.SampleLevel(screenSampler, uv, 0);
        case 8:  return screenSource8.SampleLevel(screenSampler, uv, 0);
        case 9:  return screenSource9.SampleLevel(screenSampler, uv, 0);
        case 10: return screenSource10.SampleLevel(screenSampler, uv, 0);
        case 11: return screenSource11.SampleLevel(screenSampler, uv, 0);
        case 12: return screenSource12.SampleLevel(screenSampler, uv, 0);
        case 13: return screenSource13.SampleLevel(screenSampler, uv, 0);
        case 14: return screenSource14.SampleLevel(screenSampler, uv, 0);
        case 15: return screenSource15.SampleLevel(screenSampler, uv, 0);
        case 16: return screenSource16.SampleLevel(screenSampler, uv, 0);
        case 17: return screenSource17.SampleLevel(screenSampler, uv, 0);
        case 18: return screenSource18.SampleLevel(screenSampler, uv, 0);
        case 19: return screenSource19.SampleLevel(screenSampler, uv, 0);
        case 20: return screenSource20.SampleLevel(screenSampler, uv, 0);
        case 21: return screenSource21.SampleLevel(screenSampler, uv, 0);
        case 22: return screenSource22.SampleLevel(screenSampler, uv, 0);
        case 23: return screenSource23.SampleLevel(screenSampler, uv, 0);
        case 24: return screenSource24.SampleLevel(screenSampler, uv, 0);
        case 25: return screenSource25.SampleLevel(screenSampler, uv, 0);
        case 26: return screenSource26.SampleLevel(screenSampler, uv, 0);
        case 27: return screenSource27.SampleLevel(screenSampler, uv, 0);
        case 28: return screenSource28.SampleLevel(screenSampler, uv, 0);
        case 29: return screenSource29.SampleLevel(screenSampler, uv, 0);
        case 30: return screenSource30.SampleLevel(screenSampler, uv, 0);
        default: return screenSource31.SampleLevel(screenSampler, uv, 0);
    }
}
// For a screen-instance material id (> SDF_SCREEN_MATERIAL, from SdfProgramBuilder's screen-surface ScreenSlab
// overload), resolves the surface UV at the hit and shades it. Two tiers, decal-first: a screen slot carrying a GLYPH
// DECAL (a per-screen cell grid — see sdfSampleGlyphDecal) samples TEXT at the hit (no screenMask bit needed — a decal
// terminal has no bound image); otherwise, when a source is bound THIS FRAME, samples it (NEAREST) through the CRT
// glass. outColor is valid only when this returns true; the caller falls back to today's flat/procedural screen
// shading otherwise (the plain sentinel, or a declared surface with neither a decal nor a bound source this frame).
// footprintDiameter = the hit pixel's world diameter (pixelFootprint * traveled) — the decal's analytic AA source.
bool sampleScreenSurface(int material, float3 hitPoint, float3 rayDirection, float footprintDiameter, out float3 outColor) {
    outColor = float3(0.0, 0.0, 0.0);

    if (material <= SDF_SCREEN_MATERIAL) {
        return false; // the plain sentinel: no declared instance, so no screen table lookup.
    }

    uint screenIndex = (uint)(material - SDF_SCREEN_MATERIAL - 1);

    // An out-of-bounds structured-buffer read is zeroed on Direct3D 12 by spec but only defined under robustBufferAccess
    // on Vulkan, so the bound makes both backends agree by construction rather than by driver luck. Falling back to the
    // material-shaded path is the same answer a zeroed entry would produce here (no decal, no bound source), and the host
    // refuses such an id, so no valid program reaches this branch and no composed pixel moves.
    if (screenIndex >= SdfScreenSurfaceCount) {
        return false;
    }

    ScreenSurfaceData surface = worldScreenSurface(screenIndex);
    float3 local = (hitPoint - surface.origin.xyz);
    float2 uv = float2(
        (0.5 + (0.5 * (dot(local, surface.right.xyz) / surface.right.w))),
        (0.5 - (0.5 * (dot(local, surface.up.xyz) / surface.up.w)))
    );

#if defined(SDF_GLYPH_ATLAS)
    // The GLYPH DECAL tier wins first: a screen slot with an active per-screen descriptor (gridCols > 0) samples its
    // glyph-cell grid + colours instead of an image — dense reading text, resolution-independent at walk-up distance.
    uint4 decal = sdfDecalCells[screenIndex];

    if ((decal.x > 0u) && (decal.y > 0u)) {
        outColor = sdfSampleGlyphDecal(decal, uv, surface.right.w, footprintDiameter);

        return true;
    }
#endif

    if (!screenSourceBound(screenIndex)) {
        return false; // declared, but neither a decal nor a bound source this frame — the material-shaded fallback applies.
    }

    // When CrtCurvature is non-zero, bulge the image out about the screen centre (pincushion) so it reads as curved
    // tube glass rather than a decal. At the tuned 0 this is the identity and folds away.
    float2 centered = (uv - 0.5);
    float radiusSquared = dot(centered, centered);
    float2 curved = (0.5 + (centered * (1.0 + (CrtCurvature * radiusSquared))));

    // Bezel: a smooth rounded-rect mask (an SDF on the screen-local uv) that fades to black just inside the slab edge.
    // Under a non-zero curvature the bulge pushes the corners past it, giving a real tube's dark rounded corners.
    float2 edgeDistance = ((abs(curved - 0.5) - float2((0.5 - CrtBezel), (0.5 - CrtBezel))) + CrtCornerRadius);
    float outside = (length(max(edgeDistance, 0.0)) - CrtCornerRadius);
    float bezel = (1.0 - smoothstep(0.0, CrtBezelSoft, outside));

    // The image fills the area INSIDE the bezel rather than being masked by it: a bezel frames a screen, it never
    // eats picture. Sampling the slab's whole face and then blackening its rim would crop CrtBezel of every edge —
    // half a tile column on a 160-wide handheld image. Folds to the identity at CrtBezel = 0.
    float2 image = (0.5 + ((curved - 0.5) / (1.0 - (2.0 * CrtBezel))));

    float3 sampled = sampleScreenSource(screenIndex, saturate(image)).rgb;

    // Aperture grille — faint vertical RGB phosphor stripes: three cosines 120 degrees apart. Continuous (cos), so a
    // cross-backend UV delta never flips a hard edge; the period rides the screen-local UV, so the stripe stays on the
    // image. CrtApertureGrille = 0 is a no-op.
    float3 grille = (0.5 + (0.5 * cos(((image.x * CrtGrilleColumns) * SDF_TAU) - CrtGrillePhase)));
    sampled *= (1.0 - (CrtApertureGrille * (1.0 - grille)));

    // Native-line scanlines (soft cosine), and a radial vignette when CrtVignette is non-zero.
    float scanline = (1.0 - (CrtScanAmplitude * (0.5 - (0.5 * cos(((image.y * CrtScanLines) * SDF_TAU))))));
    float vignette = (1.0 - (CrtVignette * radiusSquared));

    // Bloom knee: bright pixels bleed a little (single-pixel fake — no neighborhood pass).
    float luminance = dot(sampled, CrtLumaWeights);
    sampled += ((CrtBloomGain * smoothstep(CrtBloomThreshold, 1.0, luminance)) * sampled);

    // Fresnel glass glint when CrtGlint is non-zero: a faint rim brighten at glancing view angles. The pair is
    // orthonormal by contract (SdfScreenSurface), so the normalize only absorbs the uploaded table's float drift.
    float3 screenNormal = normalize(cross(surface.right.xyz, surface.up.xyz));
    float glint = pow((1.0 - saturate(dot(-rayDirection, screenNormal))), CrtGlintPower);

    outColor = ((((sampled * scanline) * vignette) * bezel) + (CrtGlint * glint));

    return true;
}
#else
// The environment reader's no-screen-sources half: the pinned sun and hemisphere an unauthored world renders, so a
// kernel that binds no screen-light buffer still parses the lit path and agrees with the bound one whenever a world
// authors no lighting. KEEP IN SYNC with SdfEnvironment.Default.
float4 worldEnvRow(uint row) {
    uint offset = (row - SdfEnvBase);

    if (offset == 0u) { return float4(2.0, 0.0, 0.0, 0.015); }
    if (offset == 1u) { return float4(SdfSunDirection, 0.85); }
    if (offset == 2u) { return float4(1.0, 1.0, 1.0, 0.0); }
    if (offset == 3u) { return float4((1.0 / 9.0), 1.0, 0.0, 0.0); }
    if (offset == 4u) { return float4(0.0, 0.0, 0.0, 0.25); }
    if (offset == 5u) { return float4(1.0, 1.0, 1.0, 1.0); }
    if (offset == 6u) { return float4(0.25, 0.0, 0.0, 0.0); }
    if (offset == 25u) { return float4(0.0, 0.0, 0.0, 6.0); }
    if (offset == 26u) { return float4(0.02, 0.02, 0.03, 16.0); }
    if (offset == 27u) { return float4(0.0, -1.0, 1.0, 0.0); }

    return float4(0.0, 0.0, 0.0, 0.0);
}
#endif

#endif
