// The screen surfaces' shading: the grid overlay colors, the CRT glass, the glyph decals, the bounded volumes and the
// screen sources a screen hit samples.
#ifndef SHADE_SDF_ENVIRONMENT_HLSLI
#define SHADE_SDF_ENVIRONMENT_HLSLI
#include "../frame/sdf-environment.hlsli"

#ifdef SDF_SCREEN_SOURCES
static const float GridFadeDistance = 32.0;                       // the world grid fades to flat past this (far-field anti-moire)
static const float GridGrazeCos = 0.30;                           // bands vanish as the view flattens against the plane
static const float3 GridWorldLineColor = float3(0.34, 0.56, 0.95);  // cool — the world floor lattice
static const float3 GridObjectLineColor = float3(0.96, 0.66, 0.28); // warm — the reference's own lattice

// CRT glass-face knobs. The glass's warp, the bezel's width among it, is each screen's published mapping
// (ScreenMappingData); these are its look: near-square corners, a crisp dark bezel edge, faint aperture-grille stripes,
// subtle native-line scanlines, and a soft bright-pixel bloom knee, so a game on it looks almost exactly like a real
// handheld panel scaled up. The bezel's mask is continuous (smoothstep), so a cross-backend UV delta never flips it.
//
// The two knobs at 0 (vignette, glint) are live and free: DXC emits `fmul fast`, so each zero folds and its whole
// chain, the glint's cross, normalize and pow included, is eliminated on both backends. Raising one brings the effect it
// names back; an #if would trade a free runtime knob for a compile-time one.
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
// coverage-AA epilogue uses). KEEP IN SYNC with SdfWorldTables's decal-buffer packing (SetDecalDescriptor/SetDecals)
// and SdfProgram. LAYOUT (sdfDecalCells, one uint4 per entry): the first SdfDecalDescriptorCount (== SDF_MAX_SCREEN_SURFACES)
// entries are the PER-SCREEN descriptors, then the shared CELL region.
//   descriptor[screenIndex] = (gridCols, gridRows, cellBase, asuint(distanceRange)); gridCols == 0 => that screen has
//                             NO decal this frame (the image or unbound-glass path applies) — an all-zero buffer is inert, so
//                             a program that declares no decal renders byte-identically.
//   cell[i]                 = (packedUvTopLeft, packedUvBottomRight [unorm2x16, sdfGlyphUnpackUv], fgRgba8, bgRgba8);
//                             a BLANK cell packs uvTopLeft == uvBottomRight (a real glyph never has zero UV extent).
#if defined(SDF_GLYPH_ATLAS)
static const uint SdfDecalDescriptorCount = SDF_MAX_SCREEN_SURFACES; // the per-screen descriptor band
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
// SdfWorldTables.PackVolumes / SdfProgramBuilder.MaxVolumes.
static const uint SdfVolumeCount = 64u;
#include "shade-volumes.hlsli"

// Samples a screen's source through the sampler its row names. A descriptor array is indexed only by a dynamically
// uniform value, so each pass of the loop takes the first active lane's screen, samples it for every lane showing that
// screen, and retires them; a wave spanning one screen passes once.
float4 sampleScreenSource(uint screenIndex, float2 uv) {
    float4 sampled = float4(0.0, 0.0, 0.0, 0.0);

    [loop]
    for (;;) {
        uint screen = WaveReadLaneFirst(screenIndex);

        if (screen == screenIndex) {
            uint filter = (uint)worldScreenMapping(screen).state.y;

            sampled = screenSources[screen].SampleLevel(samplers[filter], uv, 0);

            break;
        }
    }

    return sampled;
}
// For a screen-instance material id (> SDF_SCREEN_MATERIAL, from SdfProgramBuilder's screen-surface ScreenSlab
// overload), resolves the surface UV at the hit and shades it. Two tiers, decal-first: a screen slot carrying a GLYPH
// DECAL (a per-screen cell grid — see sdfSampleGlyphDecal) samples TEXT at the hit (no bound source needed — a decal
// terminal has no bound image); otherwise, when a source is bound and mapped this frame, draws it from the screen's
// mapping through the CRT glass and the sampler its row names. outColor is valid only when this returns true; the
// caller falls back to the unbound glass otherwise (the plain sentinel, or a declared surface with neither a decal nor a bound source this frame).
// footprintDiameter = the hit pixel's world diameter (pixelFootprint * traveled) — the decal's analytic AA source.
bool sampleScreenSurface(int material, float3 hitPoint, float3 rayDirection, float footprintDiameter, out float3 outColor) {
    outColor = float3(0.0, 0.0, 0.0);

    if (!sdfScreenSurfaceShades(material)) {
        return false;
    }

    uint screenIndex = (uint)(material - SDF_SCREEN_MATERIAL - 1);

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

    // No decal, so sdfScreenSurfaceShades found the slot's source bound and mapped: the face is drawn from its mapping.
    ScreenMappingData mapping = worldScreenMapping(screenIndex);
    float2 centered = (uv - 0.5);
    float radiusSquared = dot(centered, centered);

    // The glass's warp: the face point the glass samples. The image fills the area inside the bezel rather than being
    // masked by it, so a bezel frames a screen and never eats picture.
    float3 face = float3(uv, 1.0);
    float2 image = float2(dot(mapping.warpU.xyz, face), dot(mapping.warpV.xyz, face));

    // Bezel: a smooth rounded-rect mask on the face that fades to black where the warped point leaves the unit square,
    // each axis's overshoot measured in face units.
    float2 edgeDistance = (((abs(image - 0.5) - 0.5) * float2(mapping.warpU.w, mapping.warpV.w)) + CrtCornerRadius);
    float outside = (length(max(edgeDistance, 0.0)) - CrtCornerRadius);
    float bezel = (1.0 - smoothstep(0.0, CrtBezelSoft, outside));

    // The layout, the fit and the crop take the warped point to the source; outside the crop of a letterboxing fit, the
    // half-open [left, right) x [top, bottom) SourceMapping.MapRay holds a hit to, lies a black bar.
    float3 warped = float3(image, 1.0);
    float2 source = float2(dot(mapping.imageU.xyz, warped), dot(mapping.imageV.xyz, warped));
    bool letterbox = ((mapping.imageV.w != 0.0) && (any(source < mapping.crop.xy) || any(source >= mapping.crop.zw)));
    float3 sampled = (letterbox
        ? float3(0.0, 0.0, 0.0)
        : sampleScreenSource(screenIndex, clamp(source, mapping.sampleClamp.xy, mapping.sampleClamp.zw)).rgb);

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
#endif

#endif
