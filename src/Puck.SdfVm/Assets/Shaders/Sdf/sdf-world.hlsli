// Shared data contract + shading for the SDF world compositor compute kernels: sdf-beam.comp (the tile-cull
// prepass) and sdf-world-views.comp (Stage 1, per-view rendering; sdf-world-composite.comp does Stage 2). Both run
// the generic VM (sdf-vm.hlsli) over the scene program and a table of viewports/cameras supplied as DATA. KEEP IN
// SYNC with SdfWorldEngine's packing.
#ifndef SDF_WORLD_HLSLI
#define SDF_WORLD_HLSLI
#include "sdf-tile.hlsli"
#include "sdf-vm.hlsli"

// The viewport table — cameras + regions — as DATA (binding 2). sdf-vm.hlsli binds the scene program at binding 1.
struct ViewportData {
    float4 position;    // xyz = world position, w = time (seconds)
    float4 right;       // xyz = right basis,   w = tan(fov / 2)
    float4 up;          // xyz = up basis,      w = aspect ratio
    float4 forward;     // xyz = forward basis, w = debug view mode (0 = final)
    float4 region;      // xy = normalized origin, zw = normalized size (of the output image)
};
[[vk::binding(2, 0)]] StructuredBuffer<ViewportData> viewports : register(t1);

struct CompositeParams {
    uint2 imageExtent;   // output image size in pixels
    uint2 tileGrid;      // tiles per viewport (row, column) — the cull buffer's per-viewport stride
    uint viewportCount;
    uint childMask;      // bit v set => viewport v is backed by a CHILD node's surface, not an SDF camera
    uint screenMask;     // bit s set => screen source slot s is bound this frame (Stage 1 only; unused elsewhere)
    uint instanceMaskWordCount; // the LIVE uploaded program's derived per-tile mask width (SdfProgram.InstanceMaskWordCount), pushed per frame
};
[[vk::push_constant]] ConstantBuffer<CompositeParams> params;

// Whether viewport v is a hosted child surface (its source[] slot holds another node's output): the beam prepass
// and Stage 1 skip such slots so the SDF render never overwrites the child's pixels.
bool isChildViewport(uint viewportIndex) {
    return (0u != (params.childMask & (1u << viewportIndex)));
}
uint worldInstanceMaskBase(uint tileIndex) {
    return (params.instanceMaskWordCount * tileIndex);
}

#ifdef SDF_SCREEN_SOURCES
// A declared ScreenSlab instance's world-space front-face frame (see Puck.SdfVm.SdfScreenSurface) — Stage 1 ONLY
// (binding 10/11 are not part of the beam prepass or Stage 2's descriptor sets). Indexed DIRECTLY by screen index
// (0..7, the same slot SetScreenSource/screenSources binds) — not by declaration order — so a hit resolves its
// surface with no search; an unfilled slot's entry is never read (no material id can address it: the host packs an
// entry only when SdfProgramBuilder registers that screen index).
struct ScreenSurfaceData {
    float4 right;   // xyz = unit world-space U axis, w = half-width
    float4 up;      // xyz = unit world-space V axis (V=0 at top), w = half-height
    float4 origin;  // xyz = world-space front-face center, w = unused (pad)
};
[[vk::binding(10, 0)]] StructuredBuffer<ScreenSurfaceData> screenSurfaces : register(t4);
// The screen source images (nearest-filtered, so emulator/child pixels stay crisp) — one per screen index (0..7), EIGHT
// separate combined-image-sampler bindings (12..19; DXC's vk::combinedImageSampler does not support an ARRAY texture,
// only a scalar one, so a true single Vulkan combined-image-sampler array isn't expressible in this HLSL — see the
// C# side for the derived binding indices). Each Texture2D+SamplerState pair shares one binding (fusing into ONE
// Vulkan combined-image-sampler descriptor) and needs its OWN sampler register (s0..s7) — DXC rejects two distinct
// sampler declarations aliased onto one register — so Direct3D 12 bakes in EIGHT static samplers, one per
// SampledImage binding, ALL with the identical requested filter (NEAREST): logically one shared sampler, materialized
// as eight registers because the shading language has no array-of-combined-image-sampler here. Direct3D 12 assigns
// t#/s# registers in the C# binding-array order (DirectXGpuComputePipelineFactory), so these register(tN)/register(sN)
// annotations must mirror SdfWorldEngine's viewsBindings order exactly — currently t5..t12 / s0..s7. Slots with no
// source bound this frame (params.screenMask bit clear) duplicate a valid filler view; the shader never samples an
// unbound slot (screenSourceBound gates it), so the filler's content never reaches the image.
[[vk::combinedImageSampler]] [[vk::binding(12, 0)]] Texture2D<float4> screenSource0 : register(t5);
[[vk::combinedImageSampler]] [[vk::binding(12, 0)]] SamplerState screenSampler0 : register(s0);
[[vk::combinedImageSampler]] [[vk::binding(13, 0)]] Texture2D<float4> screenSource1 : register(t6);
[[vk::combinedImageSampler]] [[vk::binding(13, 0)]] SamplerState screenSampler1 : register(s1);
[[vk::combinedImageSampler]] [[vk::binding(14, 0)]] Texture2D<float4> screenSource2 : register(t7);
[[vk::combinedImageSampler]] [[vk::binding(14, 0)]] SamplerState screenSampler2 : register(s2);
[[vk::combinedImageSampler]] [[vk::binding(15, 0)]] Texture2D<float4> screenSource3 : register(t8);
[[vk::combinedImageSampler]] [[vk::binding(15, 0)]] SamplerState screenSampler3 : register(s3);
[[vk::combinedImageSampler]] [[vk::binding(16, 0)]] Texture2D<float4> screenSource4 : register(t9);
[[vk::combinedImageSampler]] [[vk::binding(16, 0)]] SamplerState screenSampler4 : register(s4);
[[vk::combinedImageSampler]] [[vk::binding(17, 0)]] Texture2D<float4> screenSource5 : register(t10);
[[vk::combinedImageSampler]] [[vk::binding(17, 0)]] SamplerState screenSampler5 : register(s5);
[[vk::combinedImageSampler]] [[vk::binding(18, 0)]] Texture2D<float4> screenSource6 : register(t11);
[[vk::combinedImageSampler]] [[vk::binding(18, 0)]] SamplerState screenSampler6 : register(s6);
[[vk::combinedImageSampler]] [[vk::binding(19, 0)]] Texture2D<float4> screenSource7 : register(t12);
[[vk::combinedImageSampler]] [[vk::binding(19, 0)]] SamplerState screenSampler7 : register(s7);
// Per-frame screen LIGHT records (binding 11, register t14 — the LAST SRV in the views set): entries 0..7 carry each
// screen's emitted light (rgb = the framebuffer's average color this frame, a = intensity gain), entry 8 is the
// ENVIRONMENT (x = ambient scale, y = sun scale — dim the room so the glow dominates; zw pad). A light's geometry
// (position/orientation/extent) is the SAME screenSurfaces[i] entry above — a screen is an area emitter, so it needs
// only its color here. KEEP IN SYNC with SdfWorldEngine's screen-light buffer packing.
[[vk::binding(11, 0)]] StructuredBuffer<float4> sdfScreenLights : register(t14);
static const uint SdfScreenLightEnv = 8u;

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

bool screenSourceBound(uint screenIndex) {
    return (0u != (params.screenMask & (1u << screenIndex)));
}
float4 sampleScreenSource(uint screenIndex, float2 uv) {
    // Every screenSamplerN carries the SAME filter (NEAREST) — the eight-way split is purely to give DXC one sampler
    // symbol per register; there is exactly one LOGICAL sampler behavior on either backend.
    switch (screenIndex) {
        case 0:  return screenSource0.SampleLevel(screenSampler0, uv, 0);
        case 1:  return screenSource1.SampleLevel(screenSampler1, uv, 0);
        case 2:  return screenSource2.SampleLevel(screenSampler2, uv, 0);
        case 3:  return screenSource3.SampleLevel(screenSampler3, uv, 0);
        case 4:  return screenSource4.SampleLevel(screenSampler4, uv, 0);
        case 5:  return screenSource5.SampleLevel(screenSampler5, uv, 0);
        case 6:  return screenSource6.SampleLevel(screenSampler6, uv, 0);
        default: return screenSource7.SampleLevel(screenSampler7, uv, 0);
    }
}
// For a screen-instance material id (> SDF_SCREEN_MATERIAL, from SdfProgramBuilder's screen-surface ScreenSlab
// overload) whose screen index has a source bound THIS FRAME, samples it (NEAREST) at the hit point's UV. outColor
// is valid only when this returns true; the caller falls back to exactly today's flat/procedural screen shading
// otherwise (the plain sentinel, or a declared surface with no source bound this frame).
bool sampleScreenSurface(int material, float3 hitPoint, float3 rayDirection, out float3 outColor) {
    outColor = float3(0.0, 0.0, 0.0);

    if (material <= SDF_SCREEN_MATERIAL) {
        return false; // the plain sentinel: no declared instance, so no screen table lookup.
    }

    uint screenIndex = (uint)(material - SDF_SCREEN_MATERIAL - 1);

    if (!screenSourceBound(screenIndex)) {
        return false; // declared, but no source bound this frame — the material-shaded fallback applies.
    }

    ScreenSurfaceData surface = screenSurfaces[screenIndex];
    float3 local = (hitPoint - surface.origin.xyz);
    float2 uv = float2(
        (0.5 + (0.5 * (dot(local, surface.right.xyz) / surface.right.w))),
        (0.5 - (0.5 * (dot(local, surface.up.xyz) / surface.up.w)))
    );

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

    float3 sampled = sampleScreenSource(screenIndex, saturate(curved)).rgb;

    // Aperture grille — faint vertical RGB phosphor stripes: three cosines 120 degrees apart. Continuous (cos), so a
    // cross-backend UV delta never flips a hard edge; the period rides the screen-local UV, so the stripe stays on the
    // image. CrtApertureGrille = 0 is a no-op.
    float3 grille = (0.5 + (0.5 * cos(((curved.x * CrtGrilleColumns) * SDF_TAU) - CrtGrillePhase)));
    sampled *= (1.0 - (CrtApertureGrille * (1.0 - grille)));

    // Native-line scanlines (soft cosine), and a radial vignette when CrtVignette is non-zero.
    float scanline = (1.0 - (CrtScanAmplitude * (0.5 - (0.5 * cos(((curved.y * CrtScanLines) * SDF_TAU))))));
    float vignette = (1.0 - (CrtVignette * radiusSquared));

    // Bloom knee: bright pixels bleed a little (single-pixel fake — no neighborhood pass).
    float luminance = dot(sampled, CrtLumaWeights);
    sampled += ((CrtBloomGain * smoothstep(CrtBloomThreshold, 1.0, luminance)) * sampled);

    // Fresnel glass glint when CrtGlint is non-zero: a faint rim brighten at glancing view angles. The normalize is
    // load-bearing — the surface's right/up pair is unit but NOT guaranteed orthogonal (see SdfScreenSurface).
    float3 screenNormal = normalize(cross(surface.right.xyz, surface.up.xyz));
    float glint = pow((1.0 - saturate(dot(-rayDirection, screenNormal))), CrtGlintPower);

    outColor = ((((sampled * scanline) * vignette) * bezel) + (CrtGlint * glint));

    return true;
}
#endif

static const int MaxSteps = 160;
static const float MaxDistance = 60.0;
static const float SurfaceEpsilon = 0.001;
static const float SphereTraceOmega = 1.2; // Keinert over-relaxation factor (1 = plain sphere tracing; [1, 2))
static const int ConeMarchSteps = 96;
static const float ConeNear = 0.02;
static const float ConeEpsilon = 0.002;
// WorldTileSize / TileEmpty / worldTileIndex live in sdf-tile.hlsli — shared with sdf-world-composite.comp.

// Shading weights of the world's one directional-sun-plus-hemisphere model.
static const float AmbientBase = 0.25;
static const float AmbientHemisphere = 0.25; // scales normal.y: sky above, darker below
static const float SunWeight = 0.85;
static const float FogDensity = 0.015;       // exponential distance fog toward skyColor
// The procedural test-card face (an unbound screen): its own emitter, tinted faintly by the sun.
static const float ScreenCardBase = 0.85;
static const float ScreenCardSunTint = 0.15;
// Keeps a screen light's inverse-square attenuation finite for a surface point on the emitter's own face.
static const float ScreenLightMinDistanceSquared = 1.0e-4;
// The 8-bit dither quantum: +-0.5 LSB of R2 noise before the store (see sdfR2Dither).
static const float DitherQuantum = (1.0 / 255.0);
// Soft-shadow march toward the sun: the step budget, the penumbra sharpness, the reach, the surface-offset bias that
// keeps the march from immediately self-hitting the origin surface, and the per-step advance clamp.
static const int ShadowSteps = 48;
static const float ShadowSharpness = 9.0;
// Half the RT path's 24-unit reach: this compute march has no TLAS to fast-forward to the occluder, so every unit of
// reach is marched per lit pixel. Contact/self shadows (the visual win) are near; 12 covers every realistic case while
// halving the worst-case empty-space step count on dense scenes.
static const float ShadowMaxDistance = 12.0;
static const float ShadowBias = 0.02;
static const float ShadowStepMin = 0.02;  // an occluder thinner than this can be stepped through (IQ-inherited)
static const float ShadowStepMax = 0.6;
// The gradient probe's finite-difference offset. Small enough that the tetrahedron's O(eps) curvature error is
// sub-LSB, large enough to stay clear of the field's own float noise.
static const float NormalProbeEpsilon = 0.0006;

// The 4-tap TETRAHEDRON normal probe, MASKED (world path): estimates the field gradient from 4 samples at the corners
// of a tetrahedron (offset directions k.xyy/k.yyx/k.yxy/k.xxx = the alternating cube corners) instead of 6 axis-aligned
// samples. The taps are isotropic — Σ dᵢdᵢᵀ = 4·I and Σ dᵢ = 0 — so weighting each sample by its own direction
// reconstructs the SAME first-order gradient as the 6-tap central difference, from 4 evaluations instead of 6.
// Visually identical for lit shading (the O(ε) vs O(ε²) curvature error is sub-LSB at this ε), at 2/3 the cost of the
// kernel's hottest call. Every tap shares the pixel's tile instance mask — sound because a masked-out instance is
// exactly as absent from a nearby tap as it is from the hit itself (the beam prepass's tile cone covers the whole
// tile, taps included at this epsilon). The per-program stepScale (D1) is a common factor that cancels under
// normalize, so the Lipschitz clamp leaves normals untouched.
float3 calculateNormal(float3 p, uint instanceMaskBase) {
    const float2 k = float2(1.0, -1.0);
    const float e = NormalProbeEpsilon;

    return normalize(
        (k.xyy * mapMasked(p + (k.xyy * e), instanceMaskBase).distance) +
        (k.yyx * mapMasked(p + (k.yyx * e), instanceMaskBase).distance) +
        (k.yxy * mapMasked(p + (k.yxy * e), instanceMaskBase).distance) +
        (k.xxx * mapMasked(p + (k.xxx * e), instanceMaskBase).distance)
    );
}
// Procedural placeholder for a SCREEN_SLAB face: an animated test-card.
float3 screenContent(float3 p, float time) {
    float bars = (0.5 + (0.5 * sin((p.y * 26.0) - (time * 5.0))));
    float3 baseColor = lerp(float3(0.02, 0.04, 0.09), float3(0.10, 0.80, 1.00), bars);
    float sweep = smoothstep(0.49, 0.5, frac((p.x * 1.3) + (time * 0.4)));

    return (baseColor + (0.35 * float3(0.95, 0.45, 0.12) * sweep));
}
float3 skyColor(float3 direction) {
    float t = clamp((0.5 * (direction.y + 1.0)), 0.0, 1.0);

    return lerp(float3(0.04, 0.05, 0.07), float3(0.10, 0.13, 0.20), t);
}
// A distinct, stable hue per material id (an HSV hue ramp), not the table albedo — so id boundaries read clearly
// in the material-id debug view.
float3 materialPalette(int material) {
    float hue = frac(float(material) * 0.61803399);
    float3 ramp = (abs((frac(hue + float3(0.0, 0.33333333, 0.66666667)) * 6.0) - 3.0) - 1.0);

    return saturate(ramp);
}

// The perspective ray for a viewport-local UV (pixel centers in [0,1] within the viewport's region; screen-up maps
// to the camera's +up).
float3 cameraRayDirection(ViewportData view, float2 localUv) {
    float2 ndc = ((localUv * 2.0) - 1.0);

    ndc.y = -ndc.y;

    float tanHalfFov = view.right.w;
    float aspect = view.up.w;

    return normalize(
        view.forward.xyz +
        (((ndc.x * aspect) * tanHalfFov) * view.right.xyz) +
        ((ndc.y * tanHalfFov) * view.up.xyz)
    );
}

struct TileCone {
    float3 centerDirection;
    float chord;
    float inverseAperture; // 1/sqrt(1 - chord^2), the exact sphere-vs-cone bound's correction (see collectInstanceMaskWord)
};

TileCone buildTileCone(ViewportData view, float2 localUvMin, float2 localUvMax) {
    TileCone cone;

    cone.centerDirection = cameraRayDirection(view, (0.5 * (localUvMin + localUvMax)));
    cone.chord = 0.0;
    cone.chord = max(cone.chord, length(cameraRayDirection(view, localUvMin) - cone.centerDirection));
    cone.chord = max(cone.chord, length(cameraRayDirection(view, float2(localUvMax.x, localUvMin.y)) - cone.centerDirection));
    cone.chord = max(cone.chord, length(cameraRayDirection(view, float2(localUvMin.x, localUvMax.y)) - cone.centerDirection));
    cone.chord = max(cone.chord, length(cameraRayDirection(view, localUvMax) - cone.centerDirection));
    // Once per tile, not once per instance. The max() guards a degenerate wide cone; a 16 px tile's chord is ~0.02
    // (fullscreen) to ~0.044 (a 2x2 quad viewport), so this lands just above 1.
    cone.inverseAperture = rsqrt(max((1.0 - (cone.chord * cone.chord)), 1.0e-6));

    return cone;
}

// Conservative cone (beam) march over the distance field for a tile's UV rect. map() is a true distance field, so
// the cone of half-spread `chord` clears ALL of its rays while map(center) - chord*t > 0, and a 1-Lipschitz-safe
// step is clearance / (1 + chord). Returns the earliest t at which any ray in the tile could hit — a march-start
// lower bound shared by every pixel in the tile — or TileEmpty when the cone clears the field out to MaxDistance.
float coneMarchTile(ViewportData view, TileCone cone) {
    float3 origin = view.position.xyz;

    float t = ConeNear;

    [loop]
    for (int i = 0; (i < ConeMarchSteps); i++) {
        float clearance = (map(origin + (cone.centerDirection * t)).distance - (cone.chord * t));

        if (clearance <= ConeEpsilon) {
            return t;
        }

        t += (clearance / (1.0 + cone.chord));

        if (t > MaxDistance) {
            return TileEmpty;
        }
    }

    return t;
}

// Per-tile instance cull (the beam prepass, world path only — requires SDF_DYNAMIC_TRANSFORMS for a DYNAMIC
// instance's bound to resolve): tests the 32 instances of one mask WORD's index range against the tile's cone (the
// same center ray + chord coneMarchTile already computed) and sets an instance's bit when its world-space bounding
// sphere may be visible to ANY ray in the tile — the beam kernel calls this once per derived mask word
// (sdfInstanceMaskWordCount) and writes each word straight to the mask buffer. `instanceOffset` is the instance
// directory's offset (sdfInstanceDirectoryOffset), resolved ONCE by the caller so the per-instance bound loads skip
// the loop-invariant offset chain.
//
// THE BOUND. A ray of the tile satisfies |d_i - d0| <= chord = c, so a hit at parameter t requires the CENTER ray to
// pass within (r + c*t) of the sphere: |p(t) - C| <= r + c*t for some t >= 0. With a = dot(C - o, d0) and h the
// distance from C to the center-ray line, that is
//     g(t) = (1 - c^2)t^2 - 2(a + rc)t + (a^2 + h^2 - r^2) <= 0
// whose minimum over t sits at t* = (a + rc)/(1 - c^2) — NOT at t = a — and yields, after the numerator collapses to
// (r + ac)^2, the exact necessary condition
//     h <= (r + c*a) / sqrt(1 - c^2).
// Testing `h <= r + c*a` alone evaluates g at t = a only, so it CULLS SPHERES A REAL TILE RAY GRAZES. Measured: at a
// 2x2 quad viewport (chord ~0.044) a sphere tangent to a corner ray at t = 60 is rejected by up to 0.0028 world units,
// well past the host's ~0.0011 bound inflation (SdfProgram.BoundRadiusPadding/Scale). inverseAperture >= 1, so the
// corrected test only ever ADDS bits — it can never lose an instance the old one kept.
uint collectInstanceMaskWord(uint instanceOffset, uint wordIndex, uint instanceCount, float3 rayOrigin, float3 centerDirection, float chord, float inverseAperture) {
    uint bits = 0u;
    uint first = (wordIndex << 5u);
    uint end = min((first + 32u), instanceCount);

    [loop]
    for (uint i = first; (i < end); i++) {
        float4 bound = sdfInstanceBoundAt(instanceOffset, i);

        // A PARKED instance (a reserved-pool slot carrying no live content this rebuild) packs a negative-radius
        // sentinel host-side (SdfProgram.ParkedBoundRadius): skip its sphere-vs-cone test with this single branch —
        // no sqrt, no dot, mask bit left 0 — so a full pool's worth of hidden slots costs one comparison each instead
        // of the full cull. A real bound radius is always non-negative (float-safety-padded), so this never misfires.
        if (bound.w < 0.0) {
            continue;
        }

        float3 toCenter = (bound.xyz - rayOrigin);
        float alongRay = max(dot(toCenter, centerDirection), 0.0);
        float axisDistance = length(toCenter - (centerDirection * alongRay));

        if (axisDistance <= ((bound.w + (chord * alongRay)) * inverseAperture)) {
            bits |= (1u << (i - first));
        }
    }

    return bits;
}

// March + shade one viewport's ray for a pixel at the viewport-local UV, starting the march at `marchStart` (the
// tile-cull lower bound; TileEmpty skips the march entirely → background) and resolving the debug view mode.
// `instanceMaskBase` is the pixel's tile mask base in the mask buffer (SDF_INSTANCE_MASK_ALL when the beam prepass
// never resolved one, e.g. a consumer that skips it) — the WHOLE march (and its normal probe) uses the SAME mask
// throughout, so the masked field a ray marches through is self-consistent start to finish.
// The debug-view-mode wire contract: viewport forward.w carries the mode index into DebugViewModes.Names
// (src/Puck.Demo/DebugView.cs — the list's ORDER is the wire value; KEEP IN SYNC, including the switch below).
// Mode 0 / >= DebugViewModeCount render final shading.
static const int DebugViewModeCount = 6;
static const int DebugViewModeNormals = 2;

// A soft shadow toward the (directional) sun: an IQ-style penumbra march of the field from the surface point up toward
// the light, tracking the closest-approach ratio (ShadowSharpness · clearance / traveled) so grazing occluders cast a
// soft penumbra. Uses the pixel's TILE instance mask — cheap (the same cull the primary march already narrowed) and it
// captures self- and contact-shadows; distant inter-object occlusion (an occluder outside this tile) is the RT path's
// TLAS-accelerated domain (sdf-world-rt-debug's lightShadow). The sun is above, so the infinite ground plane never
// self-occludes an upward ray.
//
// mapMasked returns the Lipschitz-CLAMPED distance (d_true · stepScale). Stepping with it is fine — an under-step is
// conservative — but the penumbra ratio and the occlusion test COMPARE it against world-space quantities, so they must
// divide the clamp back out. Without that, a program carrying any chamfer blend (stepScale = 1/√2) darkened every soft
// shadow in the scene by ~30% for no geometric reason; a Displace/DomainWarp/eccentric-Ellipsoid program darkens more.
// stepScale == 1.0 for an isometric program and x*1.0f == x to the bit, so those scenes stay byte-identical.
float softShadow(float3 surfacePoint, float3 surfaceNormal, float3 lightDirection, uint instanceMaskBase) {
    float3 origin = (surfacePoint + (surfaceNormal * ShadowBias));
    float traveled = ShadowBias;
    float result = 1.0;
    float stepScale = sdfStepScale();

    [loop]
    for (int step = 0; (step < ShadowSteps); step++) {
        float clearance = mapMasked(origin + (lightDirection * traveled), instanceMaskBase).distance;

        if (clearance < (SurfaceEpsilon * stepScale)) {
            return 0.0; // fully occluded
        }

        result = min(result, ((ShadowSharpness * clearance) / (traveled * stepScale)));
        // The step clamp is IQ's: the floor keeps a near-tangent march from stalling (at the cost of stepping through
        // occluders thinner than it), the ceiling keeps the penumbra from over-marching past a grazing silhouette.
        traveled += clamp(clearance, ShadowStepMin, ShadowStepMax);

        if (traveled > ShadowMaxDistance) {
            break;
        }
    }

    return result;
}
float3 renderView(ViewportData view, float2 localUv, float marchStart, uint instanceMaskBase, float pixelFootprint) {
    float3 rayOrigin = view.position.xyz;
    float3 rayDirection = cameraRayDirection(view, localUv);
    int viewMode = (int)round(view.forward.w);
    float time = view.position.w;

    float traveled = max(marchStart, 0.0);
    bool hitSurface = false;
    int material = 0;
    int marchStep = 0;

    if (marchStart >= 0.0) {
        // Keinert Enhanced Sphere Tracing: OVER-RELAXED steps (omega * radius) with a disjoint-sphere step-back, and a
        // footprint-ADAPTIVE hit threshold — these are one algorithm, not two. The field mapMasked returns is already
        // Lipschitz-clamped (SdfProgram stepScale, D1 keystone), so over-relaxing it stays safe: if an over-relaxed step
        // lands where the new unbounding sphere no longer overlaps the previous one, we overshot — retreat into the
        // verified-empty overlap and fall to plain sphere tracing (omega = 1) for the rest of this ray.
        float omega = SphereTraceOmega;
        float previousRadius = 0.0;
        float stepLength = 0.0;

        [loop]
        for (marchStep = 0; (marchStep < MaxSteps); marchStep++) {
            SdfHit hit = mapMasked(rayOrigin + (rayDirection * traveled), instanceMaskBase);
            float radius = hit.distance;
            bool overshoot = ((omega > 1.0) && ((radius + previousRadius) < stepLength));

            if (overshoot) {
                stepLength -= (omega * stepLength);
                omega = 1.0;
            }
            else {
                stepLength = (radius * omega);
            }

            previousRadius = radius;

            // Footprint-adaptive termination: the surface is "hit" once the field is smaller than the pixel's world
            // footprint at this distance (an absolute floor keeps near-camera precision bounded) — resolution-independent
            // and cheaper at range than a fixed epsilon. Never on an overshoot-retreat step (that step isn't a hit).
            //
            // Two deliberate conservative biases, both toward the camera (they can only make a silhouette fatter, never
            // drop geometry). (1) pixelFootprint * traveled is the pixel's full world DIAMETER at that depth — 2x
            // Keinert's pixel RADIUS. (2) `radius` is the Lipschitz-clamped distance, so the test fires at true distance
            // threshold/stepScale. Undoing either lengthens every march and risks the MaxSteps ceiling on warped scenes.
            if (!overshoot && (radius < max(SurfaceEpsilon, (pixelFootprint * traveled)))) {
                hitSurface = true;
                material = hit.material;
                break;
            }

            traveled += stepLength;

            if (traveled > MaxDistance) {
                break;
            }
        }
    }

    float3 normal = float3(0.0, 0.0, 0.0);
    float3 color = skyColor(rayDirection);

    if (hitSurface) {
        float3 surfacePoint = (rayOrigin + (rayDirection * traveled));
        bool useFinalShading = ((viewMode <= 0) || (viewMode >= DebugViewModeCount));
        bool sampledScreen = false;
#ifdef SDF_SCREEN_SOURCES
        if (useFinalShading) {
            // A bound screen source wins over BOTH the flat sentinel and the procedural test-card: emissive/unlit
            // (the diegetic screen is its own light source, like a real display — no scene lighting dims or tints it),
            // but shaped by the CRT glass face (curvature/bezel/scanlines/vignette/glint/bloom in sampleScreenSurface)
            // before the shared distance fog. The screen ALSO lights the room — see the screen-light loop below.
            sampledScreen = sampleScreenSurface(material, surfacePoint, rayDirection, color);
        }
#endif

        bool needsLitColor = (useFinalShading && !sampledScreen);
        bool needsNormal = ((viewMode == DebugViewModeNormals) || needsLitColor);

        if (needsNormal) {
            normal = calculateNormal(surfacePoint, instanceMaskBase);
        }

        if (needsLitColor) {
            float sunDiffuse = max(dot(normal, SdfSunDirection), 0.0);
            float ambient = (AmbientBase + (AmbientHemisphere * normal.y));

            // The environment scales dim the room so the diegetic screen glow dominates. They default to 1 outside the
            // world-views path (every other path shades exactly as before); the overworld sets them low per frame.
            float ambientScale = 1.0;
            float sunScale = 1.0;
#ifdef SDF_SCREEN_SOURCES
            float4 environment = sdfScreenLights[SdfScreenLightEnv];
            ambientScale = environment.x;
            sunScale = environment.y;
#endif

            // Soft-shadow the SUN contribution (the ambient term still fills shadowed regions, so shadows read soft,
            // not black). Skip the march where the surface already faces away from the sun (sunDiffuse == 0).
            // The procedural screen branch below consumes sunDiffuse too, so this march is NOT dead there.
            if (sunDiffuse > 0.0) {
                sunDiffuse *= softShadow(surfacePoint, normal, SdfSunDirection, instanceMaskBase);
            }

            if (material >= SDF_SCREEN_MATERIAL) {
                // The procedural test-card face: a declared screen with no source bound this frame (or the plain
                // sentinel). Unlit apart from a faint sun tint — it is its own emitter, so the radiance accumulation
                // below would be discarded. Test the whole sentinel RANGE, never `==`: a screen-instance id is
                // SDF_SCREEN_MATERIAL + 1 + screenIndex and must never index the material table.
                color = (screenContent(surfacePoint, time) * (ScreenCardBase + (ScreenCardSunTint * sunDiffuse)));
            } else {
                float3 radiance = (float3(1.0, 1.0, 1.0) * ((ambient * ambientScale) + ((SunWeight * sunDiffuse) * sunScale)));

#ifdef SDF_SCREEN_SOURCES
                // Every BOUND diegetic screen is a colored area light: its position/orientation come from the
                // screen-surface table, its color from the per-frame framebuffer average. The dot(screenNormal, -L) gate
                // is the "light through the glass" cue — a screen only lights what sits in front of its face.
                // SdfScreenLightEnv doubles as the screen-slot COUNT (the environment entry sits right after 0..count-1).
                // The normalize is load-bearing: right/up are unit but not guaranteed orthogonal (see SdfScreenSurface).
                for (uint lightIndex = 0u; (lightIndex < SdfScreenLightEnv); lightIndex++) {
                    if (!screenSourceBound(lightIndex)) {
                        continue;
                    }

                    ScreenSurfaceData lightSurface = screenSurfaces[lightIndex];
                    float3 screenNormal = normalize(cross(lightSurface.right.xyz, lightSurface.up.xyz));
                    float3 toLight = (lightSurface.origin.xyz - surfacePoint);
                    float distanceSquared = max(dot(toLight, toLight), ScreenLightMinDistanceSquared);
                    float3 lightDirection = (toLight * rsqrt(distanceSquared));
                    float facing = (max(dot(normal, lightDirection), 0.0) * saturate(dot(screenNormal, -lightDirection)));
                    float attenuation = (1.0 / (1.0 + (ScreenLightFalloff * distanceSquared)));

                    radiance += (sdfScreenLights[lightIndex].rgb * ((sdfScreenLights[lightIndex].a * facing) * attenuation));
                }
#endif

                color = sdfMaterialShade(sdfMaterialLoad(material), radiance, normal, rayDirection, SdfSunDirection, sunScale);
            }
        }

        if (useFinalShading) {
            float fog = (1.0 - exp(-FogDensity * traveled));
            color = lerp(color, skyColor(rayDirection), fog);
        }
    }

    float3 viewColor = color;

    switch (viewMode) {
        case 1: { // depth
            float depth = saturate(traveled / MaxDistance);
            viewColor = float3(depth, depth, depth);
            break;
        }
        case 2: { // surface normals
            viewColor = (hitSurface ? ((normal * 0.5) + 0.5) : float3(0.0, 0.0, 0.0));
            break;
        }
        case 3: { // ray direction
            viewColor = ((rayDirection * 0.5) + 0.5);
            break;
        }
        case 4: { // material id palette
            viewColor = (hitSurface ? materialPalette(material) : float3(0.0, 0.0, 0.0));
            break;
        }
        case 5: { // iteration count ramp (after the cull fast-forward — empty tiles read ~0)
            float ramp = (float(marchStep) / float(MaxSteps));
            viewColor = float3(ramp, ramp, ramp);
            break;
        }
    }

    return viewColor;
}

#endif
