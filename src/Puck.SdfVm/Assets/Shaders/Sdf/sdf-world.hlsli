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
// The tile cull buffer's plane layout (four-bound teleport, Larsson "The Gunk"). Plane 0 = the march-start lower
// bound (the classic beam output; sdf-cull-args + the compositor read ONLY this plane, so their worldTileIndex
// stride is unchanged). Planes 1/2 = the proven-empty gap [firstExit, secondEntry] a tile's cone cleared between
// two occupied bands: sdf-beam writes them, sdf-world-views teleports across them. Each plane is one entry per
// (viewport, tile) THIS frame — the same span worldTileIndex covers — so plane k of tile T sits at
// (k * stride + tileIndex). KEEP IN SYNC with SdfWorldEngine.TilePlaneCount.
static const uint WorldTilePlaneCount = 3u;
uint worldTilePlaneStride() {
    return (params.tileGrid.x * params.tileGrid.y * params.viewportCount);
}
// Plane 0 (march-start) needs no stride multiply — this accessor exists only for symmetry with the two below (see
// the layout comment above: sdf-cull-args and the compositor deliberately read plane 0 directly, unaffected by any
// plane-count change, so they do not call it).
uint worldTileMarchStartIndex(uint tileIndex) {
    return tileIndex;
}
uint worldTileFirstExitIndex(uint tileIndex) {
    return (worldTilePlaneStride() + tileIndex);
}
uint worldTileSecondEntryIndex(uint tileIndex) {
    return (((WorldTilePlaneCount - 1u) * worldTilePlaneStride()) + tileIndex);
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
// ENVIRONMENT (x = ambient scale, y = sun scale — dim the room so the glow dominates; z/w = the SLICE debug view's
// plane selector: z = axis (0 camera-locked, 1/2/3 world X/Y/Z), w = the axis plane's signed offset — see
// SdfFrame.DebugSliceAxis; read only by debug view mode 7). A light's geometry
// (position/orientation/extent) is the SAME screenSurfaces[i] entry above — a screen is an area emitter, so it needs
// only its color here. KEEP IN SYNC with SdfWorldEngine's screen-light buffer packing.
[[vk::binding(11, 0)]] StructuredBuffer<float4> sdfScreenLights : register(t14);
static const uint SdfScreenLightEnv = 8u;

// Grid-lock overlay rows (grid-locking §4a): FOUR float4 rows AFTER the env entry (env stays at 8 — load-bearing as
// the screen-count loop bound above). KEEP IN SYNC with SdfWorldEngine.PackScreenLights + SdfFrame's Grid* fields.
static const uint SdfGridWorld = 9u;      // x = flags (bit0 world floor grid, bit1 object grid), y = floorY, zw = world pitch (X, Z)
static const uint SdfGridObjOrigin = 10u; // xyz = reference origin (world), w = object pitch X
static const uint SdfGridObjFrame = 11u;  // xyzw = reference frame quaternion
static const uint SdfGridObjParams = 12u; // x = object pitch Z, y = patch radius (reference-local), zw = reserved
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
// Four-bound teleport (Larsson "The Gunk"): after the beam cone finds the tile's ENTRY (the classic marchStart), it
// keeps marching a bounded budget to detect ONE proven-empty gap between two occupied bands — [firstExit,
// secondEntry] — that sdf-world-views teleports the fine ray across. TileGapSteps caps the extra beam cost;
// TileGapMinStep floors the through-band advance so a near-zero cone clearance can't stall the search.
static const int TileGapSteps = 24;
static const float TileGapMinStep = 0.15;
// Early-abandon for the through-band phase: a ground/wall tile whose cone never re-clears would otherwise burn all
// TileGapSteps descending monotonically deeper into the half-space. After this many CONSECUTIVE in-band steps whose
// clearance stays below the open-threshold AND is non-increasing (the cone is only going deeper), give up proving a
// gap — a real gap re-clears within a few magnitude-stepped steps, so it resets the streak first. Missing a gap is
// SAFE: the teleport just does not arm, and the fine march is pixel-identical whether or not it teleports (the jump
// lands at secondEntry <= the true re-entry). Restores the beam cost a gap-less tile used to waste (~+0.33ms/room).
static const int TileGapStallLimit = 4;
// Bán & Valasek 2023 auto-relaxed sphere tracing (EG short paper). The fine march tracks the field's along-ray slope
// with an EMA `m` and over-relaxes adaptively — `omega = max(1, 2/(1 - m))`, so a planar (m -> 1) approach takes a big
// step and a concave (m -> -1) one degenerates to a plain step — instead of a fixed omega. This SUBSUMES the fixed
// DIST clear-space multiplier the first four-bound-teleport increment carried: auto-relaxed is the principled adaptive
// form of "step harder where it's safe." SlopeBeta is the paper's default (knob-insensitive: 0.2..0.3 within 2%);
// SlopeCap clamps `1 - m` away from 0 at tangency so `omega` stays finite (the field is stepScale-clamped to
// <= 1-Lipschitz, so the measured slope M is in [-1, 1] and m never legitimately exceeds SlopeCap).
static const float SlopeBeta = 0.3;
static const float SlopeCap = 0.8;   // omega <= 2 / (1 - 0.8) = 10
// STRICT-MARCH fallback (SDF_STRICT_MARCH). Defining it (a build-time flip, rebuild the kernels) replaces the default
// Bán 2023 auto-relaxed marcher with the PRE-WAVE Keinert over-relaxed marcher: a fixed omega = 1.2 with a
// disjoint-sphere step-back that LATCHES omega to 1 for the rest of the ray after an overshoot — and omega is NEVER
// re-armed thereafter, not even across a four-bound teleport (the teleport jump itself still runs — it is bound-proven
// on both paths — but it does not reset the latch). So strict is exactly "pre-wave Keinert marcher + the four-bound
// teleport, omega never re-armed." It is NOT byte-identical to any earlier build (the teleport rides it too); it is
// only the CONSERVATIVE, division-free reference marcher. Chosen as a compile-time #define, not a runtime toggle or env
// var, because the world kernels are AOT-compiled by DXC in-place at build and the engine has no runtime shader-variant
// selection — a runtime switch would mean shipping and selecting two pipelines, far past the "cheapest honest seam."
// It is NOT built by default and is exercised by NO gate — a hand-flip parity anchor only. The DEFAULT auto-relaxed
// step's DIVISION is the one new cross-backend hazard (FMA contraction amplified near tangency can flip the disjoint-
// sphere fallback compare), so the divided step and that compare are pinned `precise` on both backends and the strict
// path never rides the division. The four-bound teleport rides BOTH paths (branchless, no division).
// #define SDF_STRICT_MARCH
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
// A curvature-carrying variant of the tetrahedron normal probe: it reuses the SAME four taps to ALSO recover the
// field's discrete Laplacian. Because the tetrahedron offsets are isotropic (Σ dᵢdᵢᵀ = 4·I, Σ dᵢ = 0), the four tap
// distances minus four times the center distance is 2·e²·∇²d to first order — which for a metric SDF (|∇d| ≈ 1)
// approximates the mean curvature of the level set: concave creases read negative, convex ridges/silhouettes positive.
// One extra CENTER tap on top of the normal's four. The whole curvature chain — the center tap, the sum, the /(2 e²) —
// feeds ONLY `curvature`, which the stylization knobs below multiply by 0 in the default build, so DXC dead-code-
// eliminates it on both backends and the default lit path stays byte-identical to calculateNormal (the four taps and
// the normalize survive unchanged). The Laplacian is de-scaled by stepScale so the signal is world-unit curvature.
float3 calculateNormalCurvature(float3 p, uint instanceMaskBase, out float curvature) {
    const float2 k = float2(1.0, -1.0);
    const float e = NormalProbeEpsilon;

    float d0 = mapMasked(p + (k.xyy * e), instanceMaskBase).distance;
    float d1 = mapMasked(p + (k.yyx * e), instanceMaskBase).distance;
    float d2 = mapMasked(p + (k.yxy * e), instanceMaskBase).distance;
    float d3 = mapMasked(p + (k.xxx * e), instanceMaskBase).distance;
    float center = mapMasked(p, instanceMaskBase).distance;
    float stepScale = sdfStepScale();

    curvature = (((d0 + d1 + d2 + d3) - (4.0 * center)) / ((2.0 * e * e) * stepScale));

    return normalize(((k.xyy * d0) + (k.yyx * d1) + (k.yxy * d2) + (k.xxx * d3)));
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

// The four-bound teleport's per-tile output (Larsson "The Gunk"). `entry` is the classic conservative-cone marchStart
// (the earliest t at which any ray in the tile could hit — a march-start lower bound shared by every pixel in the tile,
// or TileEmpty when the cone clears the field out to MaxDistance), so plane 0 — and therefore the cull-args bbox, the
// compositor's empty-tile test, and the footprint-adaptive termination the ground-notch rides — is the exact classic
// beam output. `firstExit`/`secondEntry` bound ONE proven-empty gap the cone cleared between two occupied bands; when
// no gap is proven, firstExit = MaxDistance so the consumer's teleport is a total no-op (a total function, per the
// determinism pin). secondEntry >= firstExit always.
struct TileBounds {
    float entry;
    float firstExit;
    float secondEntry;
};

// map()-based cone march that additionally records the first proven-empty gap past the entry band. The GAP is
// conservative for the WHOLE tile cone: `firstExit` is a t at which the cone clearance is strictly positive (every
// ray in the tile is clear there) and the search then steps by <= clearance/(1+chord) — the sphere-trace guarantee —
// so it cannot skip the cone re-entering geometry; the first re-entry is `secondEntry`. Overstepping the interior of
// the first band (the through-band phase) can only MISS a gap (reporting firstExit = MaxDistance), never invent one,
// so a teleport is never unsafe. Reaching MaxDistance while clear yields secondEntry = MaxDistance (an empty tail —
// the ray teleports to the far plane and ends), the one far-bound benefit taken here.
TileBounds coneMarchTileBounds(ViewportData view, TileCone cone) {
    float3 origin = view.position.xyz;

    TileBounds b;
    b.entry = TileEmpty;
    b.firstExit = MaxDistance;   // no proven gap => teleport disabled (total function)
    b.secondEntry = MaxDistance;

    // Phase 1 — ENTRY (the classic conservative cone/beam march). map() is a true distance field, so the cone of
    // half-spread `chord` clears ALL of its rays while map(center) - chord*t > 0, and a 1-Lipschitz-safe step is
    // clearance / (1 + chord). Returns the earliest t at which the cone could hit (the shared per-tile marchStart), or
    // TileEmpty when the cone clears the field out to MaxDistance.
    float t = ConeNear;
    bool foundEntry = false;

    [loop]
    for (int i = 0; (i < ConeMarchSteps); i++) {
        float clearance = (map(origin + (cone.centerDirection * t)).distance - (cone.chord * t));

        if (clearance <= ConeEpsilon) {
            b.entry = t;
            foundEntry = true;
            break;
        }

        t += (clearance / (1.0 + cone.chord));

        if (t > MaxDistance) {
            return b; // TileEmpty entry, no gap — cull-args drops the tile
        }
    }

    if (!foundEntry) {
        b.entry = t; // step budget exhausted at the entry band — matches coneMarchTile's fallthrough `return t`

        return b;
    }

    // Phases 2/3 — walk PAST the entry band to prove one empty gap. `clear` flips true once the cone is provably
    // clear again (firstExit); the first time it dips back under ConeEpsilon after that is secondEntry.
    bool clear = false;
    int stall = 0;                    // consecutive in-band, non-increasing-clearance steps (the early-abandon streak)
    float previousClearance = 1.0e20; // seeded large so the first in-band step counts as non-increasing

    [loop]
    for (int j = 0; (j < TileGapSteps); j++) {
        float clearance = (map(origin + (cone.centerDirection * t)).distance - (cone.chord * t));

        if (!clear) {
            if (clearance > ConeEpsilon) {
                b.firstExit = t;               // start of a proven-clear span
                clear = true;
                t += (clearance / (1.0 + cone.chord)); // conservative step within the clear span
            }
            else {
                // Still inside/near the entry band. Early-abandon a cone that is only descending deeper (a ground/wall
                // tile with no gap): count consecutive non-increasing in-band clearances and give up once the streak
                // hits TileGapStallLimit. A real gap's cone re-clears within a few magnitude-stepped steps, resetting
                // the streak first; abandoning a non-gap tile only skips an unproven teleport (pixel-identical).
                stall = ((clearance <= previousClearance) ? (stall + 1) : 0);

                if (stall >= TileGapStallLimit) {
                    b.firstExit = MaxDistance;
                    b.secondEntry = MaxDistance;

                    return b;
                }

                // advance through the band (magnitude-stepped, floored so a near-zero clearance can't stall).
                // Overshooting the exit only shrinks/misses a gap, never invents one.
                t += (max(-clearance, TileGapMinStep) / (1.0 + cone.chord));
            }

            previousClearance = clearance;
        }
        else {
            if (clearance <= ConeEpsilon) {
                b.secondEntry = t;             // cone re-enters geometry — gap = [firstExit, secondEntry]

                return b; // one gap is the 90% win (Larsson clamps to one re-entry)
            }

            t += (clearance / (1.0 + cone.chord)); // stay conservative across the clear span
        }

        if (t > MaxDistance) {
            if (clear) {
                b.secondEntry = MaxDistance;   // proven clear to the far plane — teleport ends the ray (the far bound)
            }
            else {
                b.firstExit = MaxDistance;     // never cleanly exited the band — disable the teleport
            }

            return b;
        }
    }

    // Budget exhausted without a clean second entry: we cannot prove the span past firstExit stays empty, so DISABLE
    // the teleport (stay conservative — never teleport past unproven space).
    b.firstExit = MaxDistance;
    b.secondEntry = MaxDistance;

    return b;
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
// The exact sphere-vs-tile-cone necessary condition (the bound derivation above), factored out so the flat per-instance
// loop AND the uniform-grid cell walk decide a bit by the IDENTICAL arithmetic — the grid mask is then a pure
// SUBSET-selection of the flat mask (it only ever tests FEWER instances, never by a different float rule), so with a
// conservative cone footprint it equals the flat mask exactly. A PARKED instance (a reserved-pool slot with no live
// content this rebuild) packs a negative-radius sentinel host-side (SdfProgram.ParkedBoundRadius): reject it with the
// single leading branch — no sqrt, no dot, mask bit left 0. A real bound radius is always non-negative, so this never
// misfires.
bool sdfInstancePassesTileCone(float4 bound, float3 rayOrigin, float3 centerDirection, float chord, float inverseAperture) {
    if (bound.w < 0.0) {
        return false;
    }

    float3 toCenter = (bound.xyz - rayOrigin);
    float alongRay = max(dot(toCenter, centerDirection), 0.0);
    float axisDistance = length(toCenter - (centerDirection * alongRay));

    return (axisDistance <= ((bound.w + (chord * alongRay)) * inverseAperture));
}

uint collectInstanceMaskWord(uint instanceOffset, uint wordIndex, uint instanceCount, float3 rayOrigin, float3 centerDirection, float chord, float inverseAperture) {
    uint bits = 0u;
    uint first = (wordIndex << 5u);
    uint end = min((first + 32u), instanceCount);

    [loop]
    for (uint i = first; (i < end); i++) {
        float4 bound = sdfInstanceBoundAt(instanceOffset, i);

        if (sdfInstancePassesTileCone(bound, rayOrigin, centerDirection, chord, inverseAperture)) {
            bits |= (1u << (i - first));
        }
    }

    return bits;
}

// Accumulates one tile's per-instance mask via the UNIFORM GRID (the beam prepass's default when the grid is enabled),
// into `scratch` (the caller's per-thread SDF_MAX_INSTANCES/32-word accumulator, its first `maskWordCount` words
// pre-zeroed). Two sources, each setting a bit by the SAME sdfInstancePassesTileCone test the flat loop uses:
//   (1) the ALWAYS-tested list — dynamic, unmaskable, and sprawling instances the frozen grid cannot bin (an unmaskable
//       instance's 1e30 bound passes every tile, so it just sets its bit; a parked one is rejected inside the test).
//   (2) the grid cells the tile's cone FOOTPRINT overlaps — a conservative swept-cone rasterization. For each march
//       slab [t0, t1] (step = one cell edge, from the apex to where the cone leaves the grid AABB) the cone frustum is
//       bounded by a world AABB (the two disk centres +/- (chord*t1 + footprintPad)); that AABB is converted to a cell
//       range, EXPANDED by SDF_GRID_CELL_RING cells (the host/GPU floor may disagree by a cell), and every entry in
//       those cells is tested. CONSERVATIVENESS: an instance that passes the flat test touches the bare cone at some
//       point q within its own bound; q lies in the slab covering that depth, so q's cell is walked and the instance
//       (binned into that cell by its bound) is found. An instance in several overlapped cells sets its bit more than
//       once — idempotent (OR). So every flat-set bit is set here; and since only cell/always members are tested by the
//       identical rule, no extra bit is set: the grid mask equals the flat mask.
void collectInstanceGridMask(SdfInstanceGridHeader grid, uint instanceOffset, float3 rayOrigin, float3 centerDirection, float chord, float inverseAperture, inout uint scratch[SDF_INSTANCE_MASK_MAX_WORDS]) {
    // (1) The always-tested list.
    [loop]
    for (uint a = 0u; (a < grid.alwaysCount); a++) {
        uint index = sdfWordAt(grid.baseWord + grid.alwaysWord + a);
        float4 bound = sdfInstanceBoundAt(instanceOffset, index);

        if (sdfInstancePassesTileCone(bound, rayOrigin, centerDirection, chord, inverseAperture)) {
            scratch[index >> 5u] |= (1u << (index & 31u));
        }
    }

    // (2) The cone footprint's grid cells.
    float3 gridMin = grid.origin;
    float3 gridMax = (grid.origin + (float3(grid.dims) * grid.cellSize));
    // The far bound of the march: the largest projection of the grid AABB onto the ray (0 if the grid is behind it).
    // Past it no instance centre projects, so the cone need not be marched farther (a near-apex instance is still
    // covered by the t0 = 0 slab). Picking the per-axis corner that maximizes the projection avoids marching all 8.
    float3 farCorner = float3(
        ((centerDirection.x > 0.0) ? gridMax.x : gridMin.x),
        ((centerDirection.y > 0.0) ? gridMax.y : gridMin.y),
        ((centerDirection.z > 0.0) ? gridMax.z : gridMin.z)
    );
    float tEnd = max(dot((farCorner - rayOrigin), centerDirection), 0.0);
    int3 dimensionsMinusOne = (int3(grid.dims) - int3(1, 1, 1));

    float t0 = 0.0;

    [loop]
    for (uint slab = 0u; (slab < SDF_GRID_MAX_SLABS); slab++) {
        float t1 = min((t0 + grid.cellSize), max(tEnd, t0)); // t1 >= t0; a behind-grid ray degenerates to one apex slab
        float3 c0 = (rayOrigin + (centerDirection * t0));
        float3 c1 = (rayOrigin + (centerDirection * t1));
        float radius = ((chord * t1) + grid.footprintPad);
        float3 low = (min(c0, c1) - radius);
        float3 high = (max(c0, c1) + radius);

        // Skip the slab entirely when its AABB does not overlap the grid AABB (a cone leaving the grid, or one whose
        // apex sits outside it). clamp() would otherwise pin an off-grid AABB onto a boundary cell and test it spuriously
        // (harmless, but wasteful).
        if (all(high >= gridMin) && all(low <= gridMax)) {
            int3 cellLow = (int3(floor((low - grid.origin) * grid.invCellSize)) - SDF_GRID_CELL_RING);
            int3 cellHigh = (int3(floor((high - grid.origin) * grid.invCellSize)) + SDF_GRID_CELL_RING);

            cellLow = clamp(cellLow, int3(0, 0, 0), dimensionsMinusOne);
            cellHigh = clamp(cellHigh, int3(0, 0, 0), dimensionsMinusOne);

            [loop]
            for (int cz = cellLow.z; (cz <= cellHigh.z); cz++) {
                [loop]
                for (int cy = cellLow.y; (cy <= cellHigh.y); cy++) {
                    [loop]
                    for (int cx = cellLow.x; (cx <= cellHigh.x); cx++) {
                        uint cell = ((((uint)cz * grid.dims.y) + (uint)cy) * grid.dims.x) + (uint)cx;
                        uint entryStart = sdfWordAt(grid.baseWord + grid.cellStartWord + cell);
                        uint entryEnd = sdfWordAt(grid.baseWord + grid.cellStartWord + cell + 1u);

                        [loop]
                        for (uint k = entryStart; (k < entryEnd); k++) {
                            uint index = sdfWordAt(grid.baseWord + grid.entryWord + k);
                            float4 bound = sdfInstanceBoundAt(instanceOffset, index);

                            if (sdfInstancePassesTileCone(bound, rayOrigin, centerDirection, chord, inverseAperture)) {
                                scratch[index >> 5u] |= (1u << (index & 31u));
                            }
                        }
                    }
                }
            }
        }

        if (t1 >= tEnd) {
            break;
        }

        t0 = t1;
    }
}

// March + shade one viewport's ray for a pixel at the viewport-local UV, starting the march at `marchStart` (the
// tile-cull lower bound; TileEmpty skips the march entirely → background) and resolving the debug view mode.
// `instanceMaskBase` is the pixel's tile mask base in the mask buffer (SDF_INSTANCE_MASK_ALL when the beam prepass
// never resolved one, e.g. a consumer that skips it) — the WHOLE march (and its normal probe) uses the SAME mask
// throughout, so the masked field a ray marches through is self-consistent start to finish.
// The debug-view-mode wire contract: viewport forward.w carries the mode index into DebugViewModes.Names
// (src/Puck.Demo/DebugView.cs — the list's ORDER is the wire value; KEEP IN SYNC, including the switch below).
// Mode 0 / >= DebugViewModeCount render final shading.
static const int DebugViewModeCount = 8;
static const int DebugViewModeNormals = 2;
// Mode 7 (slice) is special-cased in TWO other places: renderView SKIPS the march for it (the slice never needs a
// hit), and the beam prepass FORCE-SURVIVES every in-viewport tile for it (sdf-beam.comp) so the indirect dispatch
// and Stage 2's empty-tile flatten cannot truncate the field picture — the slice must show the IDEAL field wall to
// wall. KEEP IN SYNC with DebugViewModes.Names in src/Puck.Demo/DebugView.cs.
static const int DebugViewModeSlice = 7;

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
// `stepScale` is the per-program Lipschitz clamp, HOISTED by renderView (one sdfStepScale() read shared across the lit
// path) and passed in — the sample it divides back out of the penumbra ratio is world-space (see below).
float softShadow(float3 surfacePoint, float3 surfaceNormal, float3 lightDirection, uint instanceMaskBase, float stepScale) {
    float3 origin = (surfacePoint + (surfaceNormal * ShadowBias));
    float traveled = ShadowBias;
    float result = 1.0;
    float previousTrue = 1.0e20; // Aaltonen's `ph`, seeded large so the first step's closest-approach y is ~0.

    [loop]
    for (int step = 0; (step < ShadowSteps); step++) {
        float clearance = mapMasked(origin + (lightDirection * traveled), instanceMaskBase).distance;

        if (clearance < (SurfaceEpsilon * stepScale)) {
            return 0.0; // fully occluded
        }

        // Aaltonen 2017 closest-approach refinement over IQ's classic k*h/t: treat the previous and current SDF samples
        // as a local parabola and recover the PERPENDICULAR miss distance `d` at the estimated closest point BETWEEN
        // samples (y = h^2/(2*ph), d = sqrt(h^2 - y^2)), so a sharp occluder no longer bands at the step frequency —
        // same per-step cost bar two ops and a sqrt, no extra map() evals. LOAD-BEARING pin: y and d are WORLD-space,
        // so the Lipschitz clamp is divided back out of the sample FIRST (clearanceTrue = clearance / stepScale) — the
        // denominator keeps the RAW `traveled` exactly as the classic form did, so in the far limit (y->0,
        // d->clearanceTrue) this reduces to the old k*clearance/(traveled*stepScale) to the bit and an isometric
        // program (stepScale == 1) stays byte-identical. Mixing a scaled clearance into y/d here resurrects the
        // ~30%-darkening chamfer bug a prior fix already closed.
        float clearanceTrue = (clearance / stepScale);
        float y = ((clearanceTrue * clearanceTrue) / (2.0 * previousTrue));
        // Closest-point-behind guard (Aaltonen): when the parabola places the estimated closest point AT OR BEYOND the
        // current sample (y >= clearanceTrue — a tight graze followed by an opening field, or the degenerate first
        // sample where previousTrue is seeded huge), d = sqrt(clearanceTrue^2 - y^2) collapses to 0 and result LATCHES
        // to full occlusion (the black-speck band). Fall back to IQ's classic k*clearanceTrue/traveled term for that
        // sample instead of the parabola. In the far limit (y -> 0) the parabola already reduces to this term to the
        // bit, so existing (isometric) shadows are unchanged — only the latch case flips.
        float sample;

        if (y >= clearanceTrue) {
            sample = ((ShadowSharpness * clearanceTrue) / max(traveled, 1.0e-4));
        }
        else {
            float d = sqrt(max(((clearanceTrue * clearanceTrue) - (y * y)), 0.0));
            sample = ((ShadowSharpness * d) / max((traveled - y), 1.0e-4));
        }

        result = min(result, sample);
        previousTrue = clearanceTrue;
        // The step clamp is IQ's: the floor keeps a near-tangent march from stalling (at the cost of stepping through
        // occluders thinner than it), the ceiling keeps the penumbra from over-marching past a grazing silhouette.
        traveled += clamp(clearance, ShadowStepMin, ShadowStepMax);

        if (traveled > ShadowMaxDistance) {
            break;
        }
    }

    return result;
}
// iq's 5-tap normal-ladder ambient occlusion (calcAO): from the hit, step a short ladder of fixed rungs OUTWARD along
// the surface normal; at each rung compare the distance expected to travel (h) against what the field actually reports
// (d) — where nearby geometry crowds the normal the field under-reports and the deficit (h - d) accumulates as
// occlusion, with an outer-rung falloff and a gain/clamp. Five mapMasked() calls, the same currency as the 4-tap
// normal, paid ONLY on lit hits and tile-masked exactly like softShadow (a masked-out instance is as absent from a
// nearby tap as it is from the hit itself). Purely local — no cones, no hemisphere, no history — but reads convincingly
// as contact shadowing in creases and under overhangs.
//
// Applied to the AMBIENT/sky fill ONLY, never the sun: soft shadows govern direct light, and multiplying occlusion into
// direct light ghosts (iq's Multiresolution-AO warning). The (h - d) subtract mixes a WORLD-space rung h with a
// mapMasked distance pre-scaled by the Lipschitz clamp, so d is divided back to world units FIRST (d / stepScale) — the
// same divide-back softShadow applies; without it occlusion strength tracks each program's stepScale bake, not geometry.
// `stepScale` is renderView's hoisted Lipschitz clamp (see softShadow): the (h - d) rung subtract mixes a world-space
// rung with a mapMasked distance, so d is divided back to world units by it first.
float calcAO(float3 surfacePoint, float3 surfaceNormal, uint instanceMaskBase, float stepScale) {
    float occlusion = 0.0;
    float scale = 1.0;

    [unroll]
    for (int i = 0; (i < 5); i++) {
        float h = (0.01 + ((0.12 * float(i)) / 4.0));
        float d = (mapMasked(surfacePoint + (surfaceNormal * h), instanceMaskBase).distance / stepScale);
        occlusion += ((h - d) * scale);
        scale *= 0.95;
    }

    return clamp((1.0 - (3.0 * occlusion)), 0.0, 1.0);
}
// STYLIZED curvature/NPR shading — artistic, not physically-based, so OFF by default. The master switch is a compile-
// time static-const bool: unlike the CRT gains above (which fold through `fmul fast`), the curvature chain carries a
// divide and a 5th `map()` center tap that DXC's DXIL backend does NOT dead-code-eliminate from an arithmetic *0 gain
// (SPIR-V does; DXIL kept ~13 KB), so a `if (CurvatureShadingEnabled)` dead-branch guard is used instead — DXC strips
// the whole branch (extra tap included) on BOTH backends, keeping the shipped look byte-identical and zero-cost. This
// is the least-plumbing seam that guarantees that: no new PUCK_* var, no push-constant/env-buffer contract change. Flip
// the bool (and rebuild) to author with it; the gains below then tune cavity depth, rim strength, and ink width.
static const bool CurvatureShadingEnabled = false;
static const float CurvatureCavityGain = 0.0; // darken concave creases (curvature < 0) — cavity/crease shading
static const float CurvatureRimGain = 0.0;    // brighten convex ridges/silhouettes (curvature > 0) — rim light
static const float CurvatureInkGain = 0.0;    // ink-line outline strength where |curvature| spikes (creases + edges)
static const float CurvatureInkLo = 6.0;      // |curvature| where the ink line starts
static const float CurvatureInkHi = 16.0;     // |curvature| where the ink line saturates
static const float3 CurvatureInkColor = float3(0.02, 0.02, 0.03); // near-black ink

// Fold the stylized curvature terms into an already-lit surface color. Cavity darkening scales the color down in
// concavities, rim light adds on ridges, and the ink outline lerps toward the ink color where |curvature| spikes. With
// the gains at 0 this returns `shaded` unchanged (and the curvature that feeds it dead-code-eliminates upstream).
float3 applyCurvatureShading(float3 shaded, float curvature) {
    shaded *= (1.0 - (CurvatureCavityGain * max(-curvature, 0.0)));
    shaded += (CurvatureRimGain * max(curvature, 0.0));

    float ink = (CurvatureInkGain * smoothstep(CurvatureInkLo, CurvatureInkHi, abs(curvature)));

    return lerp(shaded, CurvatureInkColor, saturate(ink));
}
#ifdef SDF_SCREEN_SOURCES
// The world FLOOR grid (grid-locking §4b): two-scale frac bands on the floor's XZ, tinted (not replaced) toward a cool
// line color, with distance + grazing fades so the far field and skimming rays never moire. A line is drawn where
// EITHER axis sits near a cell boundary; the major band (4x pitch) reads heavier so distance counts at a glance.
// Guarded on SDF_SCREEN_SOURCES: it reads the grid rows from sdfScreenLights, bound only in that configuration.
float3 applyWorldFloorGrid(float3 color, float2 xz, float2 pitch, float3 rayDirection, float traveled) {
    if ((pitch.x <= 0.0) || (pitch.y <= 0.0)) {
        return color;
    }

    float2 minorEdge = min(frac(xz / pitch), (1.0 - frac(xz / pitch)));
    float2 majorEdge = min(frac(xz / (pitch * 4.0)), (1.0 - frac(xz / (pitch * 4.0))));
    float minorLine = (1.0 - smoothstep(0.0, 0.04, min(minorEdge.x, minorEdge.y)));
    float majorLine = (1.0 - smoothstep(0.0, 0.04, min(majorEdge.x, majorEdge.y)));
    float strength = max((minorLine * 0.45), (majorLine * 0.9));

    // Anti-moire (§4d): fade with distance (far pitch < 1px) and with grazing angle (floor normal = +Y).
    strength *= saturate(1.0 - (traveled / GridFadeDistance));
    strength *= saturate(abs(rayDirection.y) / GridGrazeCos);

    return lerp(color, GridWorldLineColor, (strength * 0.55));
}

// The OBJECT grid (grid-locking §4c): a FINITE lattice patch — the reference's OWN lattice, floor-projected around the
// guide within a bounded radius. The floor point is transformed into the reference's LOCAL frame and the frac bands
// are evaluated on its local XZ, so a rotated reference renders a correctly-rotated grid for free (the world->local
// transform bakes the rotation — no lines are rotated in screen space). Warm, so it reads distinct from the cool
// world floor grid it overlays; a radial fade keeps the patch finite and legible around the reference.
float3 applyObjectGrid(float3 color, float3 surfacePoint, float3 rayDirection, float floorY) {
    if (abs(surfacePoint.y - floorY) >= 0.02) {
        return color; // floor-projected: only paints the floor plane (it overlays the cool world grid)
    }

    float4 originRow = sdfScreenLights[SdfGridObjOrigin];
    float4 frame = sdfScreenLights[SdfGridObjFrame];
    float4 paramsRow = sdfScreenLights[SdfGridObjParams];
    float2 pitch = float2(originRow.w, paramsRow.x);
    float patchRadius = paramsRow.y;

    if ((pitch.x <= 0.0) || (pitch.y <= 0.0) || (patchRadius <= 0.0)) {
        return color;
    }

    float3 local = rotatePointByInverseQuaternion((surfacePoint - originRow.xyz), frame); // world -> reference-local
    float planar = length(local.xz);

    if (planar > patchRadius) {
        return color; // finite patch, not an infinite plane
    }

    float2 minorEdge = min(frac(local.xz / pitch), (1.0 - frac(local.xz / pitch)));
    float minorLine = (1.0 - smoothstep(0.0, 0.05, min(minorEdge.x, minorEdge.y)));
    float radialFade = saturate(1.0 - (planar / patchRadius));
    float graze = saturate(abs(rayDirection.y) / GridGrazeCos); // floor normal = +Y
    float strength = ((minorLine * radialFade) * graze);

    return lerp(color, GridObjectLineColor, (strength * 0.7));
}
#endif

float3 renderView(ViewportData view, float2 localUv, float marchStart, float firstExit, float secondEntry, uint instanceMaskBase, float pixelFootprint) {
    float3 rayOrigin = view.position.xyz;
    float3 rayDirection = cameraRayDirection(view, localUv);
    int viewMode = (int)round(view.forward.w);
    float time = view.position.w;

    float traveled = max(marchStart, 0.0);
    bool hitSurface = false;
    int material = 0;
    int marchStep = 0;
    // Tier-0 coverage AA: the CLAMPED field at the accepted hit (the terminal-step residual), captured by both march
    // paths at their hit-accept. The coverage metric derived from it in the epilogue must live in the SAME units as
    // the footprint-adaptive termination test (clamped radius vs hitThreshold) — do NOT divide by stepScale here.
    // The divide-back that is correct for softShadow/calcAO (world-space geometric comparisons) is WRONG for this
    // metric: de-scaling inflated the ratio by 1/stepScale (3.16x on a twisted program), saturating it to ~1 on
    // EVERY solid hit — the coverage signal vanished and the whole blend rode on a noisy one-tap gate, which read as
    // scaly/blocky moire across solid surfaces (the wave-1 Row 3 regression). See the epilogue comment.
    float terminalRadius = 0.0;
    // The footprint-adaptive hit threshold captured at the SAME accept step as terminalRadius (both march paths). The
    // epilogue's coverage = terminalRadius / hitThreshold, and `traveled` is frozen at the hit after the loop breaks, so
    // this equals a recompute of max(SurfaceEpsilon, pixelFootprint * traveled) there — capture once instead.
    float terminalHitThreshold = SurfaceEpsilon;
    // The per-program Lipschitz clamp, read ONCE and shared by softShadow, calcAO, and the coverage-AA epilogue (each
    // divides it back out of a WORLD-space comparison — a penumbra ratio, an AO rung, the open-space rise; NOT the
    // coverage ratio itself, which lives in the same clamped units as the termination test). Hoisting the single
    // sdfStepScale() read here drops three redundant reads of the same segment-directory header lane.
    float stepScale = sdfStepScale();

    // The SLICE view never marches: it evaluates the field on a plane instead (its case below), and the beam prepass
    // force-survives every tile for it — marching those would be pure waste (sky pixels would run the full MaxSteps).
    if ((marchStart >= 0.0) && (viewMode != DebugViewModeSlice)) {
        // Sphere-trace to the surface with a footprint-ADAPTIVE hit threshold. The field mapMasked returns is already
        // Lipschitz-clamped (SdfProgram stepScale, D1 keystone; <= 1-Lipschitz along the ray), so over-relaxing stays
        // safe. DEFAULT: Bán & Valasek 2023 AUTO-RELAXED tracing — a per-ray slope EMA `slopeM` drives an adaptive
        // over-relaxation omega = max(1, 2/(1 - m)) (planar approach steps big, concave degenerates to a plain step),
        // with a disjoint-sphere step-back on overshoot. This subsumes the fixed clear-space multiplier the teleport
        // increment carried. STRICT (SDF_STRICT_MARCH): the plain omega=1.2 Keinert marcher — the conservative
        // cross-backend parity reference; the auto-relaxed step's division never rides the strict gate. The four-bound
        // teleport runs in BOTH paths.
        //
        // Footprint-hit biases (both conservative toward the camera — fatten a silhouette, never drop geometry):
        // (1) pixelFootprint * traveled is the pixel's full world DIAMETER (2x Keinert's radius); (2) `radius` is
        // Lipschitz-clamped, so the test fires at true distance threshold/stepScale.
#ifdef SDF_STRICT_MARCH
        float omega = SphereTraceOmega;
#else
        float slopeM = -1.0; // slope EMA, init -1 => the first step is plain (omega = 1)
#endif
        float previousRadius = 0.0;
        float stepLength = 0.0;

        [loop]
        for (marchStep = 0; (marchStep < MaxSteps); marchStep++) {
            SdfHit hit = mapMasked(rayOrigin + (rayDirection * traveled), instanceMaskBase);
            float radius = hit.distance;
            float hitThreshold = max(SurfaceEpsilon, (pixelFootprint * traveled));
            bool overshoot;

#ifdef SDF_STRICT_MARCH
            // Keinert over-relaxation: omega=1.2 with a disjoint-sphere step-back, latching to plain tracing (omega=1)
            // for the rest of the ray once it overshoots. Never terminate on an overshoot-retreat step.
            overshoot = ((omega > 1.0) && ((radius + previousRadius) < stepLength));

            if (overshoot) {
                stepLength -= (omega * stepLength);
                omega = 1.0;
            }
            else {
                stepLength = (radius * omega);
            }

            previousRadius = radius;
#else
            // Auto-relaxed step (Bán 2023). `stepLength` is the step that reached this sample. A disjoint-sphere
            // overshoot (`stepLength > |R| + r` — the over-relaxed step tunneled past / off the previous unbounding
            // sphere) is rejected: retreat to the previous sample and plain-step, resetting the slope. The divided
            // step and the fallback compare are `precise` so DXC's SPIR-V/DXIL FMA contraction can't flip the branch
            // near tangency; SlopeCap keeps (1 - m) away from 0 there.
            precise float sphereReach = (abs(radius) + previousRadius);
            overshoot = ((stepLength > 0.0) && (stepLength > sphereReach));

            if (overshoot) {
                traveled -= stepLength; // undo the unsafe step — back to the previous accepted sample
                radius = previousRadius;
                slopeM = -1.0;          // next step is plain (omega = 1)
            }
#endif

            // SHARED hit-accept: both march paths have now decided this sample's overshoot/step outcome and land
            // here — an overshoot-retreat sample is never tested (there is nothing new to accept this iteration),
            // and the coverage-AA epilogue's terminal-state capture (terminalRadius/terminalHitThreshold) lives in
            // ONE place instead of duplicated per path.
            if (!overshoot && (radius < hitThreshold)) {
                hitSurface = true;
                material = hit.material;
                terminalRadius = radius;
                terminalHitThreshold = hitThreshold;
                break;
            }

#ifdef SDF_STRICT_MARCH
            traveled += stepLength;
#else
            // Update the slope EMA from the step that reached this sample (skip the very first sample; an
            // overshoot-retreat step already reset slopeM above). Only reached when the shared accept check did
            // not break.
            if (!overshoot && (stepLength > 0.0)) {
                precise float slope = ((radius - previousRadius) / stepLength);
                slopeM = lerp(slopeM, slope, SlopeBeta);
            }

            precise float denominator = (1.0 - min(slopeM, SlopeCap));
            precise float omega = max(1.0, (2.0 / denominator));
            precise float advance = (radius * omega);
            previousRadius = radius;
            stepLength = advance;
            traveled += stepLength;
#endif
            // Four-bound teleport (Larsson "The Gunk"): once the ray marches past the tile's first occupied band without
            // converging, it is inside the beam-proven-empty gap — jump straight to the second band's start. secondEntry
            // >= firstExit, so this fires at most once (past secondEntry it is a no-op); a tile with no proven gap packs
            // firstExit = MaxDistance, making the branch dead. The teleport lands at secondEntry <= the ray's true
            // re-entry, so `traveled` — and the footprint threshold — is never inflated beyond a normal march (it cannot
            // worsen the ground-notch). Reset the relaxation state so a stale step/slope does not carry across the jump.
            if ((traveled >= firstExit) && (traveled < secondEntry)) {
                traveled = secondEntry;
                previousRadius = 0.0;
                stepLength = 0.0;
#ifndef SDF_STRICT_MARCH
                slopeM = -1.0;
#else
                // Strict keeps the pre-wave semantics: omega is NEVER re-armed — a latched omega=1 (post-overshoot)
                // stays latched across the teleport. Only previousRadius/stepLength reset, so the disjoint-sphere
                // step-back restarts cleanly at the landing sample without resurrecting over-relaxation the overshoot
                // already retired.
#endif
            }

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

        float curvature = 0.0; // level-set mean curvature at the hit (drives the stylized cavity/rim/ink terms below)

        if (needsNormal) {
            // The curvature variant reuses the normal's four taps plus one center tap; the plain probe (the default)
            // strips to exactly the four-tap normal on both backends via this compile-time branch.
            if (CurvatureShadingEnabled) {
                normal = calculateNormalCurvature(surfacePoint, instanceMaskBase, curvature);
            } else {
                normal = calculateNormal(surfacePoint, instanceMaskBase);
            }
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
                sunDiffuse *= softShadow(surfacePoint, normal, SdfSunDirection, instanceMaskBase, stepScale);
            }

            if (material >= SDF_SCREEN_MATERIAL) {
                // The procedural test-card face: a declared screen with no source bound this frame (or the plain
                // sentinel). Unlit apart from a faint sun tint — it is its own emitter, so the radiance accumulation
                // below would be discarded. Test the whole sentinel RANGE, never `==`: a screen-instance id is
                // SDF_SCREEN_MATERIAL + 1 + screenIndex and must never index the material table.
                color = (screenContent(surfacePoint, time) * (ScreenCardBase + (ScreenCardSunTint * sunDiffuse)));
            } else {
                // 5-tap normal-ladder AO, into the AMBIENT fill ONLY (the sun stays governed by softShadow above).
                // Computed in the material branch so the emissive screen-card path never pays its five taps.
                float ambientOcclusion = calcAO(surfacePoint, normal, instanceMaskBase, stepScale);
                float3 radiance = (float3(1.0, 1.0, 1.0) * (((ambient * ambientScale) * ambientOcclusion) + ((SunWeight * sunDiffuse) * sunScale)));

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
                // Stylized curvature enrichment (cavity darken / rim light / ink outline). The compile-time guard strips
                // it (and the extra center tap upstream) from the shipped build on both backends.
                if (CurvatureShadingEnabled) {
                    color = applyCurvatureShading(color, curvature);
                }
            }
        }

        if (useFinalShading) {
#ifdef SDF_SCREEN_SOURCES
            // Grid-lock overlays (grid-locking §4): tint the lit color BEFORE the distance fog so a far grid still
            // recedes. The world grid gates on the surface being the floor plane by HEIGHT (its material id is
            // runtime-assigned, so height is the stable test); the object grid is a finite patch in the reference frame.
            float4 gridControl = sdfScreenLights[SdfGridWorld];
            uint gridFlags = (uint)(gridControl.x + 0.5);

            if (((gridFlags & 1u) != 0u) && (abs(surfacePoint.y - gridControl.y) < 0.02)) {
                color = applyWorldFloorGrid(color, surfacePoint.xz, gridControl.zw, rayDirection, traveled);
            }

            if ((gridFlags & 2u) != 0u) {
                color = applyObjectGrid(color, surfacePoint, rayDirection, gridControl.y);
            }
#endif

            float fog = (1.0 - exp(-FogDensity * traveled));
            color = lerp(color, skyColor(rayDirection), fog);

            // Tier-0 coverage antialiasing: blend a HIT pixel toward the sky only where three independent signals agree
            // it is a genuine silhouette edge, so a grazing edge ramps toward the background (reconstructing the
            // sub-pixel silhouette ordered dither provably cannot) while solid surfaces stay bit-solid. This CORRECTS
            // the wave-1 Row 3 increment, which had two coupled faults:
            //   (1) its coverage metric (running min of the DE-SCALED field over footprint) divided the Lipschitz
            //       clamp back out, inflating the ratio by 1/stepScale and saturating it to ~1 on every solid hit —
            //       the metric carried no interior-vs-silhouette signal at all (forcing its gate to 1 washed the
            //       ENTIRE scene to sky). Do not "re-fix" that divide back in: the metric must live in the SAME
            //       CLAMPED units as the footprint-adaptive termination test it mirrors (clamped radius vs
            //       hitThreshold); the divide-back that softShadow/calcAO need for world-space geometry is wrong here.
            //   (2) the whole blend therefore rode on a single ABSOLUTE forward field tap, which on grazing-but-solid
            //       surfaces (the far floor, oblique twisted-torus flanks) leaked the per-pixel terminal gap as a
            //       fractional sky blend — the scaly/blocky moire.
            // The corrected signals:
            //   coverage — the terminal-step residual over the SAME hitThreshold the march terminated against (both
            //       clamped units). A dead-on/overstepped hit lands deep below threshold (~0, saturate handles a
            //       negative overstep); only a tangent-creep hit — the outermost ray of a silhouette — reads ~1.
            //   grazing — the normal-facing clamp: a camera-facing surface can never blend, whatever the probes say.
            //       Costs nothing (the normal is already computed on lit hits; an emissive screen face skips the
            //       normal, reads grazing=1, and relies on the other two gates — its slab interior still gates to 0).
            //   opened — the open-space confirmation, now RELATIVE: the field's rise from the terminal residual to a
            //       probe a few footprints along the ray. A solid surface the ray is entering has a falling field
            //       (opened <= 0 — the floor gates to 0 regardless of its terminal gap, the fault-2 leak); only a true
            //       silhouette, where the ray exits past the edge into open space, rises. The rise is a world-space
            //       geometric comparison, so de-scaling the DIFFERENCE by stepScale here is correct (same rule as
            //       softShadow/calcAO) — fault 1 was de-scaling the absolute metric, not a difference.
            // Sky-blend ONLY (Tier 0); blending against farther GEOMETRY is the gated Tier-1 continuation, out of
            // scope here. Ordered dither runs AFTER this (the 8-bit store in sdf-world-views.comp), so the coverage
            // ramp quantizes last and is never dithered-then-smeared along the edge.
            // coverage rides terminalHitThreshold — the SAME threshold the march accepted the hit against, captured at
            // accept (traveled is frozen at the hit after the loop, so this equals recomputing it here).
            float coverage = saturate(terminalRadius / terminalHitThreshold);
            float grazing = (1.0 - saturate(-dot(normal, rayDirection)));
            // The open-space probe (aheadField) is a WHOLE extra VM interpretation, so gate it: only a genuine
            // silhouette candidate — coverage AND grazing both non-trivial — can produce a visible blend. A camera-
            // facing solid hit reads grazing ~0 (and an overstepped one coverage ~0), so edgeWeight falls below the
            // 8-bit dither quantum, the blend would quantize away, and the probe is pure waste there. Below the gate
            // `opened` stays 0 and the lerp is a no-op — visually identical, one fewer map() on the common path.
            float edgeWeight = (coverage * grazing);
            float opened = 0.0;

            if (edgeWeight > DitherQuantum) {
                float probeSpan = max((pixelFootprint * traveled) * 3.0, SurfaceEpsilon);
                float aheadField = mapMasked(surfacePoint + (rayDirection * probeSpan), instanceMaskBase).distance;

                // The open-space rise is a world-space geometric difference, so divide the Lipschitz clamp back out
                // (same rule as softShadow/calcAO — fault 1 was de-scaling the ABSOLUTE coverage metric, not a difference).
                opened = smoothstep(0.0, (0.5 * probeSpan), ((aheadField - terminalRadius) / stepScale));
            }

            color = lerp(color, skyColor(rayDirection), (edgeWeight * opened));
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
        case 6: { // termination cause — WHY the march loop exited, per pixel (reconstructed from post-loop state so
                  // the hot non-debug path's codegen is untouched: no per-step tracking, just a read of the exit facts).
                  // green = epsilon-dominated hit, cyan = footprint-dominated hit, red = MaxSteps exhausted (the ground-
                  // notch hypothesis), dark blue = escaped past MaxDistance (or a tile the beam culled empty).
                  //
                  // THE TERMINATION/SLICE SPLIT (deliberate, keep it): this view shows what the REAL pipeline does —
                  // tile cull included (a beam-culled tile reads as escaped/background here, because that is exactly
                  // what the production march would do). The SLICE view below shows the IDEAL field instead: the beam
                  // force-survives every tile for it and its evaluation is the UNMASKED map(), so no cull or mask can
                  // truncate the picture. One view diagnoses the pipeline, the other the mathematics.
            if (hitSurface) {
                // Which term won the footprint-adaptive threshold at the hit: SurfaceEpsilon (near-camera precision
                // floor) or the pixel's world footprint (pixelFootprint * traveled). Same comparison the loop's
                // max(SurfaceEpsilon, pixelFootprint*traveled) made, read back at the hit distance.
                bool epsilonDominated = (SurfaceEpsilon >= (pixelFootprint * traveled));
                viewColor = (epsilonDominated ? float3(0.15, 0.90, 0.25) : float3(0.15, 0.80, 0.95));
            }
            else if ((marchStart < 0.0) || (traveled > MaxDistance)) {
                viewColor = float3(0.02, 0.05, 0.28); // escaped to the sky (or a beam-culled empty tile) — background
            }
            else {
                viewColor = float3(0.92, 0.16, 0.10); // the loop ran out of steps without hitting or escaping
            }

            break;
        }
        case 7: { // distance-field cross-section — the IDEAL field, wall to wall (see the termination/slice split
                  // note on case 6). The march was skipped (the gate above); the beam force-survived every in-viewport
                  // tile for this mode, so every pixel of the viewport reaches here — no tile truncation, no staircase.
                  // Default plane: through the WORLD ORIGIN with normal = camera forward (the debug subject sits at
                  // the origin — a camera-locked slice). The env entry's z/w lanes optionally select a world-axis
                  // plane instead (the `sdf.slice` verb; camera-locked when the lanes are 0/absent).
            float3 sliceNormal = view.forward.xyz; // already unit (the camera basis)
            float planeOffset = 0.0;               // the plane is dot(p, n) = planeOffset

#ifdef SDF_SCREEN_SOURCES
            float4 sliceEnv = sdfScreenLights[SdfScreenLightEnv];
            int sliceAxis = (int)round(sliceEnv.z);

            if (sliceAxis == 1) { sliceNormal = float3(1.0, 0.0, 0.0); planeOffset = sliceEnv.w; }
            else if (sliceAxis == 2) { sliceNormal = float3(0.0, 1.0, 0.0); planeOffset = sliceEnv.w; }
            else if (sliceAxis == 3) { sliceNormal = float3(0.0, 0.0, 1.0); planeOffset = sliceEnv.w; }
#endif

            float denominator = dot(rayDirection, sliceNormal);

            if (abs(denominator) < 1.0e-4) {
                viewColor = float3(0.0, 0.0, 0.0); // ray parallel to the slice — nothing to sample
                break;
            }

            float planeT = ((planeOffset - dot(rayOrigin, sliceNormal)) / denominator);

            if (planeT < 0.0) {
                viewColor = float3(0.0, 0.0, 0.0); // the plane is behind the camera along this ray
                break;
            }

            // The UNMASKED field (map, the rt-debug kernel's precedent — never mapMasked): the slice is the ideal
            // mathematics, so no per-tile instance mask may hide far-field contributions. Still the post-stepScale-
            // clamp distance — the quantity the marcher steps on — so an isoline IS a level set of the marched field.
            float sliceDistance = map(rayOrigin + (rayDirection * planeT)).distance;

            // Two-scale isolines over the sign-split hue ramp (inside warm/red, outside cool/blue): brightness ramps
            // within each MINOR band (0.25 wu) so the gradient direction stays readable; a thin dark line marks every
            // minor boundary and a heavier, darker line every MAJOR band (1.0 wu), so distance reads at a glance
            // (count the heavy rings, then the light ones). The zero contour stays the one bright white line.
            const float MinorBand = 0.25;
            const float MajorBand = 1.0;
            float fieldMagnitude = abs(sliceDistance);
            float minorPhase = frac(fieldMagnitude / MinorBand);
            float majorPhase = frac(fieldMagnitude / MajorBand);
            float3 field = ((sliceDistance < 0.0) ? float3(0.90, 0.35, 0.22) : float3(0.22, 0.45, 0.90));
            float3 sliceColor = (field * (0.35 + (0.50 * minorPhase)));
            // Distance to the nearest band boundary, in band units (0 at a boundary, 0.5 mid-band).
            float minorEdge = min(minorPhase, (1.0 - minorPhase));
            float majorEdge = min(majorPhase, (1.0 - majorPhase));

            if (minorEdge < 0.05) {  // ~0.0125 wu half-width: thin dark minor line
                sliceColor *= 0.45;
            }

            if (majorEdge < 0.02) {  // ~0.02 wu half-width: heavier, near-black major line
                sliceColor *= 0.15;
            }

            if (fieldMagnitude < 0.02) {
                sliceColor = float3(1.0, 1.0, 1.0); // the bright zero contour — the cross-section outline wins over all
            }

            viewColor = sliceColor;
            break;
        }
    }

    return viewColor;
}

#endif
