// The screen surfaces' shading: the screen materials, the glyph decals, the bounded volumes and the screen sources a
// screen hit samples.
#ifndef SHADE_SDF_ENVIRONMENT_HLSLI
#define SHADE_SDF_ENVIRONMENT_HLSLI
#include "../frame/sdf-environment.hlsli"

#ifdef SDF_SCREEN_SOURCES
// The face kinds ScreenMappingData.state.w carries — KEEP IN SYNC with Puck.SdfVm.SdfScreenFace: 0 an opening, which
// shows the world beyond it; 1 a display, which shows its picture as its source made it, filling the face; 2 a CRT
// display, which draws it through the tube's glass below.
static const float SdfScreenFaceCrt = 2.0;
// The CRT material's knobs, which only a screen that opts into the material reaches. The glass's warp, the bezel's
// width among it, is the screen's published mapping (ScreenMappingData); these are its look: near-square corners, a
// crisp dark bezel edge, faint aperture-grille stripes, subtle native-line scanlines, and a soft bright-pixel bloom
// knee. The bezel's mask is continuous (smoothstep), so a cross-backend UV delta never flips it.
//
// The two knobs at 0 (vignette, glint) are live and free: DXC emits `fmul fast`, so each zero folds and its whole
// chain, the glint's cross, normalize and pow included, is eliminated on both backends. Raising one brings the effect it
// names back; an #if would trade a free runtime knob for a compile-time one.
static const float CrtCornerRadius = 0.004;  // corner rounding of the bezel mask (near-square)
static const float CrtBezelSoft = 0.008;     // a crisp bezel edge
static const float CrtScanAmplitude = 0.06;  // subtle scanlines, one a source row — a hint of CRT, not a filter
static const float CrtApertureGrille = 0.05; // aperture-grille strength, one triad a source column — barely there (0 = off)
// The share of the scanlines' and the grille's strength that survives a pixel whose footprint spans this many source
// texels: whole while a row or column covers four or more pixels, none once it covers two or fewer, where the pattern
// would alias against the pixel grid and the temporal jitter would move that alias every frame.
float sdfCrtPatternGain(float footprintTexels) {
    return (1.0 - smoothstep(0.25, 0.5, footprintTexels));
}
static const float CrtVignette = 0.0;        // radial corner darkening (0 = flat, even brightness)
static const float CrtBloomGain = 0.5;
static const float CrtBloomThreshold = 0.6;
static const float CrtGlint = 0.0;           // fresnel rim brighten at glancing angles (0 = no glass glint)
static const float CrtGlintPower = 3.0;
// Rec.601 luma weights, for the bloom knee's brightness test.
static const float3 CrtLumaWeights = float3(0.299, 0.587, 0.114);
// The aperture grille's three phosphor stripes, 120 degrees apart (2pi/3, 4pi/3), so each channel peaks in its own
// column third. Spelled as literals rather than SDF_TAU/3: the divide would round differently by an ULP.
static const float3 CrtGrillePhase = float3(0.0, 2.0943951023931953, 4.1887902047863905);

// === OPENING RIPPLE: an event horizon ===============================================================================
// An opening that opts in (ScreenRippleData.state.x) refracts the image it shows: slow travelling waves that interfere
// all the time, a faint caustic highlight riding them, and rings that spread from each contact across the whole glass and
// fade within RippleLifetime. Distances are metres on the glass, so the rings are round on a tall canvas. Presentation
// only: the clock and the contacts are what the presenter wrote (SdfWorldTables.SetScreenRipple).
static const float RippleClockPeriod = 256.0;     // the seconds the host wraps the clock at (SdfScreenRipple.ClockPeriod)
static const float RippleLifetime = 1.4;          // the seconds a contact's rings last (SdfScreenRipple.Lifetime)
static const float RippleIdleAmplitude = 0.022;   // metres of image displacement the idle waves add up to at most
static const float RippleContactAmplitude = 0.2;  // metres of image displacement a fresh contact's ring reaches
static const float RippleFrontSpeed = 1.9;        // metres a second a ring's front travels
static const float RippleRingLength = 0.3;        // metres between a ring's crests
static const float RippleEdge = 0.06;             // the share of the glass at each edge the refraction fades out over
static const float RippleGlow = 0.09;             // the highlight's brightness
static const float3 RippleGlowColor = float3(0.75, 0.9, 1.0);
static const float2 RippleIdleDirection[3] = { float2(0.8, 0.6), float2(-0.5, 0.866025), float2(0.965926, -0.258819) };
static const float RippleIdleLength[3] = { 0.62, 0.43, 0.91 }; // metres between crests
static const float RippleIdleCycles[3] = { 90.0, -131.0, 61.0 }; // whole cycles each wave turns in a clock period, so the wrap is seamless
// Returns the surface UV an opening's image is read at, and the highlight the waves add. `extent` is the glass's width and
// height in metres.
float2 sdfOpeningRipple(ScreenRippleData ripple, float2 uv, float2 extent, out float glow) {
    float2 p = ((uv - 0.5) * extent);
    float turn = (SDF_TAU * (ripple.state.y / RippleClockPeriod));
    float2 offset = float2(0.0, 0.0);
    float height[3];

    [unroll]
    for (int wave = 0; (wave < 3); wave++) {
        float phase = ((dot(p, RippleIdleDirection[wave]) * (SDF_TAU / RippleIdleLength[wave])) + (turn * RippleIdleCycles[wave]));

        height[wave] = sin(phase);
        offset += (RippleIdleDirection[wave] * (cos(phase) * (RippleIdleAmplitude * 0.5)));
    }
    // The caustic net: bright where two waves crest together, thin lines rather than spots.
    float ridge0 = pow((1.0 - abs(height[0])), 5.0);
    float ridge1 = pow((1.0 - abs(height[1])), 5.0);
    float ridge2 = pow((1.0 - abs(height[2])), 5.0);

    glow = (RippleGlow * ((ridge0 * ridge1) + (0.6 * (ridge1 * ridge2))));

    [unroll]
    for (int contact = 0; (contact < 4); contact++) {
        float4 c = ripple.contacts[contact];
        float2 away = (p - ((c.xy - 0.5) * extent));
        float radius = length(away);
        float behind = ((RippleFrontSpeed * c.z) - radius);
        float envelope = (smoothstep(0.0, 0.1, behind) * exp(-1.4 * max(behind, 0.0)));
        float fade = (saturate(1.0 - (c.z / RippleLifetime)) * saturate(1.0 - (c.z / RippleLifetime)) * c.w * rsqrt(1.0 + (3.0 * radius)));
        float amount = (sin(behind * (SDF_TAU / RippleRingLength)) * envelope * fade);

        offset += ((away / max(radius, 1.0e-3)) * (amount * RippleContactAmplitude));
        glow += (RippleGlow * 9.0 * max(amount, 0.0));
    }

    float edge = min(min(uv.x, (1.0 - uv.x)), min(uv.y, (1.0 - uv.y)));

    return (uv + ((offset / extent) * smoothstep(0.0, RippleEdge, edge)));
}

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

// Samples a screen's source through the filter its row names, and reports the source's size in texels. A Nearest source
// keeps each texel flat and blends into its neighbour only across the pixel footprint's width at the texel's edge
// (`footprint`, in source UV units a pixel spans), through the linear sampler: a texel boundary a pixel straddles shows
// both texels' share, so a one-texel line never breaks into dashes where the samples fall either side of it, and never
// crawls as the camera moves a fraction of a pixel. A descriptor array is indexed only by a dynamically uniform value,
// so each pass of the loop takes the first active lane's screen, samples it for every lane showing that screen, and
// retires them; a wave spanning one screen passes once.
float4 sampleScreenSource(uint screenIndex, float2 uv, float2 footprint, out float2 size) {
    float4 sampled = float4(0.0, 0.0, 0.0, 0.0);

    size = float2(1.0, 1.0);

    [loop]
    for (;;) {
        uint screen = WaveReadLaneFirst(screenIndex);

        if (screen == screenIndex) {
            uint filter = (uint)worldScreenMapping(screen).state.y;

            uint width;
            uint height;

            screenSources[screen].GetDimensions(width, height);
            size = float2(width, height);
            if (filter == SDF_FILTER_NEAREST) {
                float2 texel = (uv * size);
                float2 cell = floor(texel);
                float2 fromCenter = ((texel - cell) - 0.5);
                float2 reach = saturate((((abs(fromCenter) - 0.5) / max((footprint * size), 1.0e-4)) + 0.5));

                uv = ((cell + 0.5 + (sign(fromCenter) * reach)) / size);
                filter = SDF_FILTER_LINEAR;
            }
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
// mapping, through its material (untreated, or a CRT's glass), and the sampler its row names. outColor is valid only
// when this returns true; the caller falls back to the unbound glass otherwise (the plain sentinel, or a declared surface
// with neither a decal nor a bound source this frame).
// footprintDiameter = the hit pixel's world diameter (pixelFootprint * traveled) — the decal's analytic AA source.
// `picture` is the share of the face the picture covers rather than a CRT's bezel: zero over the bezel band, so the
// face's own edge, which the temporal jitter moves, is never part of a reactive screen.
bool sampleScreenSurface(int material, float3 hitPoint, float3 rayDirection, float footprintDiameter, out float3 outColor, out float picture) {
    outColor = float3(0.0, 0.0, 0.0);
    picture = 1.0;

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
    // An opening that opts in reads its image through the ripple: the refracted UV, and the highlight added after.
    float rippleGlow = 0.0;
    float2 imageUv = uv;

    if (mapping.state.w == 0.0) {
        ScreenRippleData ripple = worldScreenRipple(screenIndex);

        if (ripple.state.x > 0.0) {
            imageUv = sdfOpeningRipple(ripple, uv, (2.0 * float2(surface.right.w, surface.up.w)), rippleGlow);
        }
    }

    // The mapping's warp: the face point the picture samples, the identity on all but a CRT's glass. The image fills
    // the area inside a CRT's bezel rather than being masked by it, so a bezel frames a screen and never eats picture.
    float3 face = float3(imageUv, 1.0);
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
    // The pixel's footprint in source UV units along each axis: its world width in face units, through the warp and the
    // layout's linear part.
    float2 sourcePerFace = float2(
        abs((mapping.imageU.x * mapping.warpU.x) + (mapping.imageU.y * mapping.warpV.x)),
        abs((mapping.imageV.x * mapping.warpU.y) + (mapping.imageV.y * mapping.warpV.y))
    );
    float2 footprint = (((0.5 * footprintDiameter) / float2(surface.right.w, surface.up.w)) * sourcePerFace);
    float2 sourceSize;
    float3 sampled = sampleScreenSource(screenIndex, clamp(source, mapping.sampleClamp.xy, mapping.sampleClamp.zw), footprint, sourceSize).rgb;

    sampled = (letterbox ? float3(0.0, 0.0, 0.0) : sampled);

    // An opening (a portal's window) is no display: it shows its image as the world beyond it, with no grille,
    // scanlines, bloom, bezel or glint.
    if (mapping.state.w == 0.0) {
        outColor = (sampled + (rippleGlow * RippleGlowColor));

        return true;
    }

    // A plain display shows its picture untreated: its mapping has no warp, so the picture fills the face. With no
    // bezel band, the pixel or so along the face's own edge, which the temporal jitter moves, is kept out of `picture`.
    if (mapping.state.w < SdfScreenFaceCrt) {
        float2 edge = ((min(uv, (1.0 - uv)) * 2.0) * float2(surface.right.w, surface.up.w));

        outColor = sampled;
        picture = saturate(((min(edge.x, edge.y) / max(footprintDiameter, 1.0e-6)) - 1.0));

        return true;
    }

    // How many source texels the pixel's footprint spans along each axis.
    float2 footprintTexels = (footprint * sourceSize);
    // The view's screen fidelity (SdfViewSnapshot.ScreenFidelity) scales every term of the CRT glass's treatment below:
    // at one the picture's pixels leave exactly as sampled and the bezel band is a hard edge, which a framed seat's matte
    // hides, so a framed screen shows its source as a desktop emulator does; at zero the glass is the in-world tube.
    float treatment = (1.0 - saturate(passGroup.screenFidelity));
    bool inPicture = all(image >= 0.0) && all(image <= 1.0);

    // Aperture grille — faint vertical RGB phosphor stripes, one triad a source column: three cosines 120 degrees apart.
    // Continuous (cos), so a cross-backend UV delta never flips a hard edge; the period rides the source, so the stripe
    // stays on the image. CrtApertureGrille = 0 is a no-op.
    float3 grille = (0.5 + (0.5 * cos(((source.x * sourceSize.x) * SDF_TAU) - CrtGrillePhase)));
    sampled *= (1.0 - (((CrtApertureGrille * treatment) * sdfCrtPatternGain(footprintTexels.x)) * (1.0 - grille)));

    // Native-line scanlines (soft cosine, dark between source rows), and a radial vignette when CrtVignette is non-zero.
    float scanline = (1.0 - (((CrtScanAmplitude * treatment) * sdfCrtPatternGain(footprintTexels.y)) * (0.5 + (0.5 * cos(((source.y * sourceSize.y) * SDF_TAU))))));
    float vignette = (1.0 - ((CrtVignette * treatment) * radiusSquared));

    // Bloom knee: bright pixels bleed a little (single-pixel fake — no neighborhood pass).
    float luminance = dot(sampled, CrtLumaWeights);
    sampled += (((CrtBloomGain * treatment) * smoothstep(CrtBloomThreshold, 1.0, luminance)) * sampled);

    // Fresnel glass glint when CrtGlint is non-zero: a faint rim brighten at glancing view angles. The pair is
    // orthonormal by contract (SdfScreenSurface), so the normalize only absorbs the uploaded table's float drift.
    float3 screenNormal = normalize(cross(surface.right.xyz, surface.up.xyz));
    float glint = pow((1.0 - saturate(dot(-rayDirection, screenNormal))), CrtGlintPower);

    bezel = lerp((inPicture ? 1.0 : 0.0), bezel, treatment);
    outColor = ((((sampled * scanline) * vignette) * bezel) + ((CrtGlint * treatment) * glint));
    picture = bezel;

    return true;
}
#endif

#endif
