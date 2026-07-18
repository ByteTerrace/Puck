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
    // x = the RENDER-SCALE numerator q (1..255; 255 = native): the view renders at worldRenderDims(rectDims, q) and
    // Stage 2 upsamples back into the full region (bilinear; q == 255 takes the exact-copy path). yzw spare.
    // KEEP IN SYNC with SdfWorldEngine.PackViewports (the 96-byte row) and BuildCompositePush's scaleQPacked.
    float4 renderScale;
};
[[vk::binding(2, 0)]] StructuredBuffer<ViewportData> viewports : register(t1);

// The per-view REDUCED render extent, derived from the view's OUTPUT extent and the quantized scale numerator q by
// INTEGER arithmetic — max(1, (outDim * q + 127) / 255) — so Stage 1 (render), the beam/instance-cull tile coverage,
// and Stage 2 (upsample) can never disagree by a float rounding: every consumer derives the identical extent from the
// identical integers on both backends. q = 255 reduces to outDim exactly ((d*255 + 127)/255 == d), the native path.
uint2 worldRenderDims(uint2 rectDims, float renderScaleQ) {
    uint q = clamp((uint)renderScaleQ, 1u, 255u);

    return max((((rectDims * q) + 127u) / 255u), uint2(1u, 1u));
}

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
    uint summaryWords = ((params.instanceMaskWordCount + 31u) >> 5u);

    return ((params.instanceMaskWordCount + summaryWords) * tileIndex);
}
// The tile cull buffer's plane layout (four-bound teleport, Larsson "The Gunk", + the F1 far bound). Plane 0 = the
// march-start lower bound (the classic beam output; sdf-cull-args + the compositor read ONLY this plane, so their
// worldTileIndex stride is unchanged). Planes 1/2 = the proven-empty gap [firstExit, secondEntry] a tile's cone
// cleared between two occupied bands: sdf-beam writes them, sdf-world-views teleports across them. Plane 3 = the F1
// FAR BOUND: the depth beyond which the tile's cone provably cannot produce ANY footprint-accepted hit through
// MaxDistance (sdf-beam writes it, sdf-world-views exits the fine march at traveled >= farBound). Each plane is one
// entry per (viewport, tile) THIS frame — the same span worldTileIndex covers — so plane k of tile T sits at
// (k * stride + tileIndex). KEEP IN SYNC with SdfWorldEngine.TilePlaneCount.
static const uint WorldTilePlaneCount = 4u;
uint worldTilePlaneStride() {
    return (params.tileGrid.x * params.tileGrid.y * params.viewportCount);
}
// Plane 0 (march-start) needs no stride multiply — this accessor exists only for symmetry with the three below (see
// the layout comment above: sdf-cull-args and the compositor deliberately read plane 0 directly, unaffected by any
// plane-count change, so they do not call it).
uint worldTileMarchStartIndex(uint tileIndex) {
    return tileIndex;
}
uint worldTileFirstExitIndex(uint tileIndex) {
    return (worldTilePlaneStride() + tileIndex);
}
uint worldTileSecondEntryIndex(uint tileIndex) {
    return ((2u * worldTilePlaneStride()) + tileIndex);
}
uint worldTileFarBoundIndex(uint tileIndex) {
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
// The screen source images (nearest-filtered, so emulator/child pixels stay crisp) — one per screen index (0..31),
// THIRTY-TWO separate combined-image-sampler bindings (12..43; DXC's vk::combinedImageSampler does not support an ARRAY
// texture, only a scalar one, so a true single Vulkan combined-image-sampler array isn't expressible in this HLSL — see
// the C# side for the derived binding indices). Each Texture2D+SamplerState pair shares one binding (fusing into ONE
// Vulkan combined-image-sampler descriptor) and needs its OWN sampler register (s0..s31) — DXC rejects two distinct
// sampler declarations aliased onto one register — so Direct3D 12 bakes in THIRTY-TWO static samplers, one per
// SampledImage binding, ALL with the identical requested filter (NEAREST): logically one shared sampler, materialized
// as thirty-two registers because the shading language has no array-of-combined-image-sampler here. Direct3D 12 assigns
// t#/s# registers in the C# binding-array order (DirectXGpuComputePipelineFactory), so these register(tN)/register(sN)
// annotations must mirror SdfWorldEngine's viewsBindings order exactly — currently t5..t36 / s0..s31. Slots with no
// source bound this frame (params.screenMask bit clear) duplicate a valid filler view; the shader never samples an
// unbound slot (screenSourceBound gates it), so the filler's content never reaches the image. (params.screenMask is a
// single uint, so exactly 32 screen bits fit — raising past 32 needs a second mask word.)
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
[[vk::combinedImageSampler]] [[vk::binding(20, 0)]] Texture2D<float4> screenSource8 : register(t13);
[[vk::combinedImageSampler]] [[vk::binding(20, 0)]] SamplerState screenSampler8 : register(s8);
[[vk::combinedImageSampler]] [[vk::binding(21, 0)]] Texture2D<float4> screenSource9 : register(t14);
[[vk::combinedImageSampler]] [[vk::binding(21, 0)]] SamplerState screenSampler9 : register(s9);
[[vk::combinedImageSampler]] [[vk::binding(22, 0)]] Texture2D<float4> screenSource10 : register(t15);
[[vk::combinedImageSampler]] [[vk::binding(22, 0)]] SamplerState screenSampler10 : register(s10);
[[vk::combinedImageSampler]] [[vk::binding(23, 0)]] Texture2D<float4> screenSource11 : register(t16);
[[vk::combinedImageSampler]] [[vk::binding(23, 0)]] SamplerState screenSampler11 : register(s11);
[[vk::combinedImageSampler]] [[vk::binding(24, 0)]] Texture2D<float4> screenSource12 : register(t17);
[[vk::combinedImageSampler]] [[vk::binding(24, 0)]] SamplerState screenSampler12 : register(s12);
[[vk::combinedImageSampler]] [[vk::binding(25, 0)]] Texture2D<float4> screenSource13 : register(t18);
[[vk::combinedImageSampler]] [[vk::binding(25, 0)]] SamplerState screenSampler13 : register(s13);
[[vk::combinedImageSampler]] [[vk::binding(26, 0)]] Texture2D<float4> screenSource14 : register(t19);
[[vk::combinedImageSampler]] [[vk::binding(26, 0)]] SamplerState screenSampler14 : register(s14);
[[vk::combinedImageSampler]] [[vk::binding(27, 0)]] Texture2D<float4> screenSource15 : register(t20);
[[vk::combinedImageSampler]] [[vk::binding(27, 0)]] SamplerState screenSampler15 : register(s15);
[[vk::combinedImageSampler]] [[vk::binding(28, 0)]] Texture2D<float4> screenSource16 : register(t21);
[[vk::combinedImageSampler]] [[vk::binding(28, 0)]] SamplerState screenSampler16 : register(s16);
[[vk::combinedImageSampler]] [[vk::binding(29, 0)]] Texture2D<float4> screenSource17 : register(t22);
[[vk::combinedImageSampler]] [[vk::binding(29, 0)]] SamplerState screenSampler17 : register(s17);
[[vk::combinedImageSampler]] [[vk::binding(30, 0)]] Texture2D<float4> screenSource18 : register(t23);
[[vk::combinedImageSampler]] [[vk::binding(30, 0)]] SamplerState screenSampler18 : register(s18);
[[vk::combinedImageSampler]] [[vk::binding(31, 0)]] Texture2D<float4> screenSource19 : register(t24);
[[vk::combinedImageSampler]] [[vk::binding(31, 0)]] SamplerState screenSampler19 : register(s19);
[[vk::combinedImageSampler]] [[vk::binding(32, 0)]] Texture2D<float4> screenSource20 : register(t25);
[[vk::combinedImageSampler]] [[vk::binding(32, 0)]] SamplerState screenSampler20 : register(s20);
[[vk::combinedImageSampler]] [[vk::binding(33, 0)]] Texture2D<float4> screenSource21 : register(t26);
[[vk::combinedImageSampler]] [[vk::binding(33, 0)]] SamplerState screenSampler21 : register(s21);
[[vk::combinedImageSampler]] [[vk::binding(34, 0)]] Texture2D<float4> screenSource22 : register(t27);
[[vk::combinedImageSampler]] [[vk::binding(34, 0)]] SamplerState screenSampler22 : register(s22);
[[vk::combinedImageSampler]] [[vk::binding(35, 0)]] Texture2D<float4> screenSource23 : register(t28);
[[vk::combinedImageSampler]] [[vk::binding(35, 0)]] SamplerState screenSampler23 : register(s23);
[[vk::combinedImageSampler]] [[vk::binding(36, 0)]] Texture2D<float4> screenSource24 : register(t29);
[[vk::combinedImageSampler]] [[vk::binding(36, 0)]] SamplerState screenSampler24 : register(s24);
[[vk::combinedImageSampler]] [[vk::binding(37, 0)]] Texture2D<float4> screenSource25 : register(t30);
[[vk::combinedImageSampler]] [[vk::binding(37, 0)]] SamplerState screenSampler25 : register(s25);
[[vk::combinedImageSampler]] [[vk::binding(38, 0)]] Texture2D<float4> screenSource26 : register(t31);
[[vk::combinedImageSampler]] [[vk::binding(38, 0)]] SamplerState screenSampler26 : register(s26);
[[vk::combinedImageSampler]] [[vk::binding(39, 0)]] Texture2D<float4> screenSource27 : register(t32);
[[vk::combinedImageSampler]] [[vk::binding(39, 0)]] SamplerState screenSampler27 : register(s27);
[[vk::combinedImageSampler]] [[vk::binding(40, 0)]] Texture2D<float4> screenSource28 : register(t33);
[[vk::combinedImageSampler]] [[vk::binding(40, 0)]] SamplerState screenSampler28 : register(s28);
[[vk::combinedImageSampler]] [[vk::binding(41, 0)]] Texture2D<float4> screenSource29 : register(t34);
[[vk::combinedImageSampler]] [[vk::binding(41, 0)]] SamplerState screenSampler29 : register(s29);
[[vk::combinedImageSampler]] [[vk::binding(42, 0)]] Texture2D<float4> screenSource30 : register(t35);
[[vk::combinedImageSampler]] [[vk::binding(42, 0)]] SamplerState screenSampler30 : register(s30);
[[vk::combinedImageSampler]] [[vk::binding(43, 0)]] Texture2D<float4> screenSource31 : register(t36);
[[vk::combinedImageSampler]] [[vk::binding(43, 0)]] SamplerState screenSampler31 : register(s31);
// Per-frame screen LIGHT records (binding 11, register t38 — the LAST SRV in the views set): entries 0..31 carry each
// screen's emitted light (rgb = the framebuffer's average color this frame, a = intensity gain), entry 32 is the
// ENVIRONMENT (x = ambient scale, y = sun scale — dim the room so the glow dominates; z/w = the SLICE debug view's
// plane selector: z = axis (0 camera-locked, 1/2/3 world X/Y/Z), w = the axis plane's signed offset — see
// SdfFrame.DebugSliceAxis; read only by debug view mode 7). A light's geometry
// (position/orientation/extent) is the SAME screenSurfaces[i] entry above — a screen is an area emitter, so it needs
// only its color here. KEEP IN SYNC with SdfWorldEngine's screen-light buffer packing.
[[vk::binding(11, 0)]] StructuredBuffer<float4> sdfScreenLights : register(t38);
static const uint SdfScreenLightEnv = 32u;

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
// The F1 FAR-FIELD lever row (perf plan Phase 5.1): x = disable the beam-published per-tile far bound (1 = the A/B
// "off" side — the fine march ignores plane 3 and runs to MaxDistance exactly as pre-F1; 0 = the DEFAULT shipped
// behavior with the far bound ACTIVE, so an unset frame uploads 0 and the feature is ON); y = disable the F2 shadow
// light-side exit (RESERVED for F2, not yet consumed); zw reserved. A SEPARATE row from SdfShadowProxyParams (whose
// lanes carry the shadow proxy). KEEP IN SYNC with SdfWorldEngine.PackScreenLights + SdfFrame's DisableFarBound field.
static const uint SdfFarFieldParams = 39u;
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
// and SdfProgram. LAYOUT (one uint4 StructuredBuffer, APPENDED LAST in the views set — Vulkan binding 45, Direct3D 12
// register t40, after the glyph atlas t39): the first SdfDecalDescriptorCount (== SdfWorldEngine.MaxScreenSurfaces)
// entries are the PER-SCREEN descriptors, then the shared CELL region.
//   descriptor[screenIndex] = (gridCols, gridRows, cellBase, asuint(distanceRange)); gridCols == 0 => that screen has
//                             NO decal this frame (the image/procedural path applies) — an all-zero buffer is inert, so
//                             a program that declares no decal renders byte-identically.
//   cell[i]                 = (packedUvTopLeft, packedUvBottomRight [unorm2x16, sdfGlyphUnpackUv], fgRgba8, bgRgba8);
//                             a BLANK cell packs uvTopLeft == uvBottomRight (a real glyph never has zero UV extent).
#if defined(SDF_GLYPH_ATLAS)
[[vk::binding(45, 0)]] StructuredBuffer<uint4> sdfDecalCells : register(t40);
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

bool screenSourceBound(uint screenIndex) {
    return (0u != (params.screenMask & (1u << screenIndex)));
}
// One past the highest bound screen slot (0 when screenMask is 0) — firstbithigh(0) is undefined, so that case is
// guarded explicitly rather than relied on to return -1.
uint screenLightLoopBound() {
    return ((0u == params.screenMask) ? 0u : (firstbithigh(params.screenMask) + 1u));
}
float4 sampleScreenSource(uint screenIndex, float2 uv) {
    // Every screenSamplerN carries the SAME filter (NEAREST) — the thirty-two-way split is purely to give DXC one
    // sampler symbol per register; there is exactly one LOGICAL sampler behavior on either backend.
    switch (screenIndex) {
        case 0:  return screenSource0.SampleLevel(screenSampler0, uv, 0);
        case 1:  return screenSource1.SampleLevel(screenSampler1, uv, 0);
        case 2:  return screenSource2.SampleLevel(screenSampler2, uv, 0);
        case 3:  return screenSource3.SampleLevel(screenSampler3, uv, 0);
        case 4:  return screenSource4.SampleLevel(screenSampler4, uv, 0);
        case 5:  return screenSource5.SampleLevel(screenSampler5, uv, 0);
        case 6:  return screenSource6.SampleLevel(screenSampler6, uv, 0);
        case 7:  return screenSource7.SampleLevel(screenSampler7, uv, 0);
        case 8:  return screenSource8.SampleLevel(screenSampler8, uv, 0);
        case 9:  return screenSource9.SampleLevel(screenSampler9, uv, 0);
        case 10: return screenSource10.SampleLevel(screenSampler10, uv, 0);
        case 11: return screenSource11.SampleLevel(screenSampler11, uv, 0);
        case 12: return screenSource12.SampleLevel(screenSampler12, uv, 0);
        case 13: return screenSource13.SampleLevel(screenSampler13, uv, 0);
        case 14: return screenSource14.SampleLevel(screenSampler14, uv, 0);
        case 15: return screenSource15.SampleLevel(screenSampler15, uv, 0);
        case 16: return screenSource16.SampleLevel(screenSampler16, uv, 0);
        case 17: return screenSource17.SampleLevel(screenSampler17, uv, 0);
        case 18: return screenSource18.SampleLevel(screenSampler18, uv, 0);
        case 19: return screenSource19.SampleLevel(screenSampler19, uv, 0);
        case 20: return screenSource20.SampleLevel(screenSampler20, uv, 0);
        case 21: return screenSource21.SampleLevel(screenSampler21, uv, 0);
        case 22: return screenSource22.SampleLevel(screenSampler22, uv, 0);
        case 23: return screenSource23.SampleLevel(screenSampler23, uv, 0);
        case 24: return screenSource24.SampleLevel(screenSampler24, uv, 0);
        case 25: return screenSource25.SampleLevel(screenSampler25, uv, 0);
        case 26: return screenSource26.SampleLevel(screenSampler26, uv, 0);
        case 27: return screenSource27.SampleLevel(screenSampler27, uv, 0);
        case 28: return screenSource28.SampleLevel(screenSampler28, uv, 0);
        case 29: return screenSource29.SampleLevel(screenSampler29, uv, 0);
        case 30: return screenSource30.SampleLevel(screenSampler30, uv, 0);
        default: return screenSource31.SampleLevel(screenSampler31, uv, 0);
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
    ScreenSurfaceData surface = screenSurfaces[screenIndex];
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
// lands at secondEntry <= the true re-entry). Four stalled steps keep gap-less tile cost bounded.
static const int TileGapStallLimit = 4;
// F1 FAR BOUND (perf plan Phase 5.1): after the gap search resolves, a bounded TAIL phase cone-marches from the
// resolved t to prove the far bound — the depth past which the tile's cone cannot produce any footprint-accepted hit
// through MaxDistance. TileFarSteps caps that extra beam cost (the tail is a latency-rich single-thread march, per the
// beam kernel's design). Sixteen steps is enough to walk a live tile's near band + one gap + a second band into a
// clear-to-far span; if the span is not proven within the budget the tile publishes farBound = MaxDistance (no early
// exit — a total function).
static const int TileFarSteps = 16;
// Bán & Valasek 2023 auto-relaxed sphere tracing (EG short paper). The fine march tracks the field's along-ray slope
// with an EMA `m` and over-relaxes adaptively — `omega = max(1, 2/(1 - m))`, so a planar (m -> 1) approach takes a big
// step and a concave (m -> -1) one degenerates to a plain step. SlopeBeta is the paper's default;
// SlopeCap clamps `1 - m` away from 0 at tangency so `omega` stays finite (the field is stepScale-clamped to
// <= 1-Lipschitz, so the measured slope M is in [-1, 1] and m never legitimately exceeds SlopeCap).
static const float SlopeBeta = 0.3;
static const float SlopeCap = 0.8;   // omega <= 2 / (1 - 0.8) = 10
// STRICT-MARCH fallback (SDF_STRICT_MARCH). Defining it (a build-time flip, rebuild the kernels) replaces the default
// Bán 2023 auto-relaxed marcher with a conservative Keinert marcher: fixed omega = 1.2 with a
// disjoint-sphere step-back that LATCHES omega to 1 for the rest of the ray after an overshoot — and omega is NEVER
// re-armed thereafter, not even across a four-bound teleport (the teleport jump itself still runs — it is bound-proven
// on both paths — but it does not reset the latch). It is the conservative, division-free reference marcher. Chosen
// as a compile-time #define because the world kernels are AOT-compiled by DXC in-place at build. (The engine ships one enumerable
// pair of compiled Stage 1 variants — full-ISA vs core-ops, selected per program at UploadProgram; see the
// SDF_CORE_OPS banner in sdf-vm.hlsli — but a hand-flip parity anchor like this one still doesn't earn a shipped
// pipeline: the variant list stays exactly two.)
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
// debug.view.evals calibration: the ramp saturates at this many tallied field evaluations. Worst case for a single
// lit pixel is bounded by MaxSteps (160, primary march) + ShadowSteps (48, soft shadow) + 3 (calcAO) + 4 (the 4-tap
// normal fallback, worse than the 1-eval analytic default) + 1 (the coverage-AA probe) ~= 216, so 256 leaves margin
// before saturating solid red — chosen so a typical unshadowed ambient-only hit (~30-40 evals: a short march plus
// the analytic normal and AO) reads green/yellow rather than washing out at the floor.
static const float EvalHeatmapCeiling = 256.0;
// Soft-shadow march toward the sun: the step budget, the penumbra sharpness, the reach, the surface-offset bias that
// keeps the march from immediately self-hitting the origin surface, and the per-step advance clamp.
static const int ShadowSteps = 48;
static const int FastShadowSteps = 16;
static const float ShadowSharpness = 9.0;
// Half the RT path's 24-unit reach: this compute march has no TLAS to fast-forward to the occluder, so every unit of
// reach is marched per lit pixel. Contact/self shadows (the visual win) are near; 12 covers every realistic case while
// halving the worst-case empty-space step count on dense scenes.
static const float ShadowMaxDistance = 12.0;
static const float ShadowBias = 0.02;
static const float ShadowStepMin = 0.02;  // an occluder thinner than this can be stepped through
static const float ShadowStepMax = 0.6;   // the NEAR-field ceiling; far-field it grows with distance (ShadowStepFarSlope)
// Distance-proportional far-field step ceiling: past ~4 units of reach the ceiling relaxes to traveled*this, so an
// unoccluded ray marching open space toward the sun clears the far field in fewer, bigger steps (~20 -> ~12 tape evals
// on a spread scene) while the near field — where contact/self shadows live and every step matters — is untouched
// (traveled*0.15 < ShadowStepMax until traveled > 4). Sound because the advance still takes min(clearance, boundBound)
// FIRST: the ceiling only bites in genuinely open space where the true clearance is already large, and a far occluder's
// tiny angular penumbra tolerates the coarser sampling.
static const float ShadowStepFarSlope = 0.15;
// Fleet-scale presentation path: sphere tracing still never advances past the conservative field/boundary clearance,
// but samples the soft penumbra less densely. A closest-approach refinement (fits a parabola between consecutive
// samples) recovers the closest approach across the wider interval; a result below half an 8-bit output quantum is
// already invisible after lighting and can terminate.
static const float FastShadowStepMax = 1.8;
static const float FastShadowStepFarSlope = 0.45;
static const float FastShadowDarknessCutoff = (0.5 / 255.0);
static const float FastShadowMaxDistance = 6.0;
// The soft-shadow PENUMBRA half-aperture, the shadow-cull gather's cone chord (see sdfShadowGather). The DIRECT penumbra
// reach is 1/ShadowSharpness: an occluder softens the shadow while its along-ray clearance d keeps sample =
// ShadowSharpness*d/traveled below 1 (d < traveled/ShadowSharpness), i.e. out to perpendicular distance boundRadius +
// traveled/ShadowSharpness — a cone of half-slope 1/ShadowSharpness (chord 0, a bare ray, MISSES this ring, the
// world-grid-cull self-shadow diff a chord-0 gather dropped). But the march's closest-approach REFINEMENT
// couples each sample to the PREVIOUS sample's clearance (previousTrue = the true nearest-surface distance), so a
// second occluder that is merely the nearest surface one step BEFORE a shadowing sample perturbs that sample's parabola
// — the influence-corridor class that killed tape pruning. That coupling occluder sits within a few × the shadowing
// occluder's distance, so the sound gather cone is WIDER than the direct penumbra: 3/ShadowSharpness is calibrated to
// reproduce the flat soft shadow BIT-IDENTICALLY (measured — 1/k left 840 penumbra-edge px, 2/k 125, 3/k a clean 0 with
// margin, on the overlapping-penumbra world-shadow-cull scene). A wider cone is always safe (a superset), only less
// selective; this is the least-wide value that clears the gate with margin.
static const float ShadowPenumbraChord = (3.0 / ShadowSharpness);
// The gradient probe's finite-difference offset. Small enough that the tetrahedron's O(eps) curvature error is
// sub-LSB, large enough to stay clear of the field's own float noise.
static const float NormalProbeEpsilon = 0.0006;

// Per-pixel field-evaluation TALLY for debug.view.evals (perf-plan Phase 0 instrumentation). A plain per-thread
// scalar counter — mirrors sdfMaterialBlendWeight's per-thread-static pattern (sdf-vm.hlsli) — because softShadow/
// calcAO/the normal probes below cannot otherwise report their internal map()-family call counts back to
// renderView's epilogue without threading a return channel through every call site. Kept HERE (never in
// mapCore/sdf-vm.hlsli): every producing call site already lives in sdf-world.hlsli, so counting stays entirely at
// the call site, never inside the interpreter itself. renderView resets it to 0 at entry; one scalar add per call
// site is negligible next to the field eval it accompanies, so the tally is left unconditional (not gated behind
// the eval view being selected) — every other view simply ignores it.
static float sdfEvalCount = 0.0;

// The 4-tap TETRAHEDRON normal probe, MASKED (world path): estimates the field gradient from 4 samples at the corners
// of a tetrahedron (offset directions k.xyy/k.yyx/k.yxy/k.xxx = the alternating cube corners) instead of 6 axis-aligned
// samples. The taps are isotropic — Σ dᵢdᵢᵀ = 4·I and Σ dᵢ = 0 — so weighting each sample by its own direction
// reconstructs the SAME first-order gradient as the 6-tap central difference, from 4 evaluations instead of 6.
// Visually identical for lit shading (the O(ε) vs O(ε²) curvature error is sub-LSB at this ε), at 2/3 the cost of the
// kernel's hottest call. Every tap shares the pixel's tile instance mask — sound because a masked-out instance is
// exactly as absent from a nearby tap as it is from the hit itself (the beam prepass's tile cone covers the whole
// tile, taps included at this epsilon). The per-program stepScale is a common factor that cancels under
// normalize, so the Lipschitz clamp leaves normals untouched.
float3 calculateNormal(float3 p, uint instanceMaskBase) {
    const float2 k = float2(1.0, -1.0);
    const float e = NormalProbeEpsilon;

    sdfEvalCount += 4.0; // four mapDistanceMasked taps below

    return normalize(
        (k.xyy * mapDistanceMasked(p + (k.xyy * e), instanceMaskBase)) +
        (k.yyx * mapDistanceMasked(p + (k.yyx * e), instanceMaskBase)) +
        (k.yxy * mapDistanceMasked(p + (k.yxy * e), instanceMaskBase)) +
        (k.xxx * mapDistanceMasked(p + (k.xxx * e), instanceMaskBase))
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

    sdfEvalCount += 5.0; // four tetrahedron taps plus the extra center tap below

    float d0 = mapDistanceMasked(p + (k.xyy * e), instanceMaskBase);
    float d1 = mapDistanceMasked(p + (k.yyx * e), instanceMaskBase);
    float d2 = mapDistanceMasked(p + (k.yxy * e), instanceMaskBase);
    float d3 = mapDistanceMasked(p + (k.xxx * e), instanceMaskBase);
    float center = mapDistanceMasked(p, instanceMaskBase);
    float stepScale = sdfStepScale();

    curvature = (((d0 + d1 + d2 + d3) - (4.0 * center)) / ((2.0 * e * e) * stepScale));

    return normalize(((k.xyy * d0) + (k.yyx * d1) + (k.yxy * d2) + (k.xxx * d3)));
}
// The ANALYTIC surface normal (forward-mode gradient dual): ONE dual field eval at the hit — replacing the four taps —
// carries the exact world-space field gradient through the transform chain (sdf-vm.hlsli's mapGradMasked). Immune to
// the finite-difference catastrophic cancellation the taps suffer near a warp/fold/displace, and more cross-backend-
// stable near these discontinuities. mapGradMasked returns the UN-normalized gradient; the stepScale the scalar
// distance still carries is a uniform positive factor that cancels under this normalize, so the dual never applies it.
// Same tile instance mask as the primary march, so the analytic normal sees the identical masked field the hit did.
float3 calculateNormalAnalytic(float3 p, uint instanceMaskBase) {
    float3 gradient;

    sdfEvalCount += 1.0; // one dual field eval replaces the four taps

    mapGradMasked(p, instanceMaskBase, gradient);

    return sdfSafeNormalize(gradient);
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
// determinism pin). secondEntry >= firstExit always. `farBound` (F1, plane 3) is the depth past which the tile's cone
// cannot produce ANY footprint-accepted hit through MaxDistance (proven against the FOOTPRINT-INFLATED threshold — see
// coneMarchFarBound); MaxDistance when the tail phase proved no such bound (the consumer's far-exit is then a no-op).
struct TileBounds {
    float entry;
    float firstExit;
    float secondEntry;
    float farBound;
};

// F1 TAIL PHASE (perf plan Phase 5.1). Proves the FAR BOUND: the depth past which the tile's cone cannot produce any
// hit the fine march would ACCEPT, all the way to MaxDistance. Marches forward from `startT` with a FOOTPRINT-INFLATED
// clearance threshold — the load-bearing correctness fact. The fine march accepts a hit at fieldDistance <
// max(SurfaceEpsilon, footprint*t) (sdf-world-views computes footprint = 2*right.w/rectDims.y; the beam computes the
// identical value from regionSizePx), and footprint*t ~ 0.001*t exceeds ConeEpsilon past t~2 — so a bare ConeEpsilon
// proof is ANTI-conservative and could bound above a real footprint hit. Inflating the cone's transverse radius by the
// pixel footprint (spread = chord + footprint) and requiring clearance = min(map(center), sdfMapStepBound) -
// spread*t > SurfaceEpsilon (stepping by clearance/(1 + spread), the 1-Lipschitz cone guarantee for the inflated cone)
// guarantees that for every ray and every t' in [farBound, MaxDistance] the hit-accept fieldDistance <
// max(SurfaceEpsilon, footprint*t') can NEVER fire — so the ray renders skyColor whether it exits at farBound or marches
// on, i.e. the far exit is OUTPUT-IDENTICAL on the shipped shading path (only step counts and the termination debug view
// change). FOLD-SAFE like the gap phases (the bounded clearance rides sdfMapStepBound). Total function: no proven clear-
// to-far span within the budget => MaxDistance.
float coneMarchFarBound(ViewportData view, TileCone cone, uint instanceMaskBase, float footprint, float startT) {
    float3 origin = view.position.xyz;
    float spread = (cone.chord + footprint); // the cone half-spread inflated by the pixel footprint
    float t = startT;
    float clearStart = MaxDistance; // start of the CURRENT footprint-clear span (MaxDistance = not in one)
    bool clear = false;

    [loop]
    for (int i = 0; (i < TileFarSteps); i++) {
        if (t > MaxDistance) {
            // Reached the far plane. If we are inside a footprint-clear span, it extends to MaxDistance => the far
            // bound is that span's start; otherwise no bound was proven.
            return (clear ? clearStart : MaxDistance);
        }

        float clearance = (min(mapDistanceMasked(origin + (cone.centerDirection * t), instanceMaskBase), sdfMapStepBound) - (spread * t));

        if (clearance > SurfaceEpsilon) {
            if (!clear) {
                clearStart = t; // start of a proven footprint-clear span
                clear = true;
            }

            t += (clearance / (1.0 + spread)); // conservative step across the inflated-clear span
        }
        else {
            // Footprint geometry (or its cone margin) is present here — any earlier clear span does NOT reach the far
            // plane. Advance through the band (magnitude-stepped, floored); overshoot only shrinks/misses a far bound,
            // never invents one (the same one-sided safety the gap search's through-band phase relies on).
            clear = false;
            clearStart = MaxDistance;
            t += (max(-clearance, TileGapMinStep) / (1.0 + spread));
        }
    }

    return MaxDistance; // budget exhausted without proving a clear-to-far span => no far bound
}

// Cone march that additionally records the first proven-empty gap past the entry band. The GAP is
// conservative for the WHOLE tile cone: `firstExit` is a t at which the cone clearance is strictly positive (every
// ray in the tile is clear there) and the search then steps by <= clearance/(1+chord) — the sphere-trace guarantee —
// so it cannot skip the cone re-entering geometry; the first re-entry is `secondEntry`. Overstepping the interior of
// the first band (the through-band phase) can only MISS a gap (reporting firstExit = MaxDistance), never invent one,
// so a teleport is never unsafe. Reaching MaxDistance while clear yields secondEntry = MaxDistance (an empty tail —
// the ray teleports to the far plane and ends), the one far-bound benefit taken here.
//
// The march evaluates the TILE-MASKED field (mapMasked at `instanceMaskBase` — the mask the instance-cull pass wrote
// for THIS tile, dispatched immediately before the beam): each sample walks only the instances overlapping the tile's
// cone, so the march's per-step cost is O(instances near this tile), not O(all instances) — the measured O(n) beam
// wall (docs/sdf-bench-notes.md) was exactly this per-sample enumeration (~1.6B segment-bound checks at 4096
// instances), never the per-tile binning. BIT-EXACT by the same contract Stage 1's masked march rides: a masked-out
// instance's bound excludes every point of the tile's cone (the sphere-vs-cone test is a necessary condition for the
// bound to touch it), and the bound-sizing contract (SdfProgram.PackInstances — union influence margins, smooth
// halos, scoped-field reach, the unmaskable sentinel) guarantees such an instance's compose returns the accumulator
// bit-exactly at any point outside its influence — so every mapMasked sample here equals the unmasked map() to the
// bit, and marchStart/the gap planes are unchanged. World segments have no mask bits and always evaluate (mapCore's
// world/instance merge). A consumer with no mask passes SDF_INSTANCE_MASK_ALL and gets the unmasked march verbatim.
TileBounds coneMarchTileBounds(ViewportData view, TileCone cone, uint instanceMaskBase, float footprint) {
    float3 origin = view.position.xyz;

    TileBounds b;
    b.entry = TileEmpty;
    b.firstExit = MaxDistance;   // no proven gap => teleport disabled (total function)
    b.secondEntry = MaxDistance;
    b.farBound = MaxDistance;    // F1: no proven far bound yet => the consumer's far-exit is a no-op (total function)

    // Phase 1 — ENTRY (the classic conservative cone/beam march). map() is a true distance field, so the cone of
    // half-spread `chord` clears ALL of its rays while map(center) - chord*t > 0, and a 1-Lipschitz-safe step is
    // clearance / (1 + chord). Returns the earliest t at which the cone could hit (the shared per-tile marchStart), or
    // TileEmpty when the cone clears the field out to MaxDistance.
    float t = ConeNear;
    bool foundEntry = false;

    [loop]
    for (int i = 0; (i < ConeMarchSteps); i++) {
        // FOLD-SAFE: the clearance proof rides min(value, sdfMapStepBound). A folded field's raw value can
        // overestimate near a fold boundary, and a cone proof built on it classifies tiles straight through shell
        // geometry (the Droste tile-shatter — docs/sdf-backlog.md Live defects). min with the published boundary gap
        // is an honest unbounding sphere of the TRUE field, so entry, TileEmpty, and the gap proofs stay sound; a
        // fold-free program publishes SDF_STEP_BOUND_NONE and this min is the identity.
        float clearance = (min(mapDistanceMasked(origin + (cone.centerDirection * t), instanceMaskBase), sdfMapStepBound) - (cone.chord * t));

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
        // F1 leak #2: a budget-exhausted grazing tile is marked LIVE (all its pixels fine-march from t). Prove the far
        // bound from here so sky pixels that clear the near band exit early instead of running to MaxDistance.
        b.farBound = coneMarchFarBound(view, cone, instanceMaskBase, footprint, t);

        return b;
    }

    // Phases 2/3 — walk PAST the entry band to prove one empty gap. `clear` flips true once the cone is provably
    // clear again (firstExit); the first time it dips back under ConeEpsilon after that is secondEntry.
    bool clear = false;
    int stall = 0;                    // consecutive in-band, non-increasing-clearance steps (the early-abandon streak)
    float previousClearance = 1.0e20; // seeded large so the first in-band step counts as non-increasing

    [loop]
    for (int j = 0; (j < TileGapSteps); j++) {
        // FOLD-SAFE, same as phase 1: a raw-value overestimate here would prove a FALSE clear span across a fold
        // boundary — an unsafe teleport. The bounded clearance keeps firstExit/secondEntry honest.
        float clearance = (min(mapDistanceMasked(origin + (cone.centerDirection * t), instanceMaskBase), sdfMapStepBound) - (cone.chord * t));

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
                    // F1: a descending, gap-less tile (ground/wall). The tail proves the far bound if the cone ever
                    // clears to the far plane past here (a ground tile never does => farBound stays MaxDistance).
                    b.farBound = coneMarchFarBound(view, cone, instanceMaskBase, footprint, t);

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
                // F1 leak #4: past the ONE proven gap there was no far information. Prove the far bound from the second
                // band so a ray that clears it exits early instead of marching the second band's sky to MaxDistance.
                b.farBound = coneMarchFarBound(view, cone, instanceMaskBase, footprint, t);

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
    // F1: the gap budget ran out mid-band; the tail phase gets its own budget to prove a far bound from here.
    b.farBound = coneMarchFarBound(view, cone, instanceMaskBase, footprint, t);

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
// inverseAperture >= 1, so the exact test is conservative.
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

// The uniform-grid CELL WALK lives in sdf-instance-cull.comp.hlsl (collectInstanceGridMask): it writes mask bits
// straight into the per-tile mask buffer (each tile's words are exclusively owned by ONE invocation, so a same-thread
// read-modify-write is race-free), which only that kernel binds writable. Two rejected shapes, both MEASURED worse on
// the 4096-instance sweep: (a) a per-thread accumulation array — a dynamically indexed uint[SDF_MAX_INSTANCES/32]
// local allocates 512 B of thread scratch per invocation; (b) fusing the cull into sdf-beam — the walk's register
// high-water mark cost the co-resident cone march ~12% occupancy on BOTH paths (grid enabled or not). Hence the
// dedicated pass.

// March + shade one viewport's ray for a pixel at the viewport-local UV, starting the march at `marchStart` (the
// tile-cull lower bound; TileEmpty skips the march entirely → background) and resolving the debug view mode.
// `instanceMaskBase` is the pixel's tile mask base in the mask buffer (SDF_INSTANCE_MASK_ALL when the beam prepass
// never resolved one, e.g. a consumer that skips it) — the WHOLE march (and its normal probe) uses the SAME mask
// throughout, so the masked field a ray marches through is self-consistent start to finish.
// The debug-view-mode wire contract: viewport forward.w carries the mode index into DebugViewModes.Names
// (src/Puck.SdfVm/DebugViewModes.cs — the list's ORDER is the wire value; KEEP IN SYNC, including the switch below).
// Mode 0 / >= DebugViewModeCount render final shading.
static const int DebugViewModeCount = 11;
static const int DebugViewModeNormals = 2;
// Mode 7 (slice) is special-cased in TWO other places: renderView SKIPS the march for it (the slice never needs a
// hit), and the beam prepass FORCE-SURVIVES every in-viewport tile for it (sdf-beam.comp) so the indirect dispatch
// and Stage 2's empty-tile flatten cannot truncate the field picture — the slice must show the IDEAL field wall to
// wall. KEEP IN SYNC with DebugViewModes.Names in src/Puck.SdfVm/DebugViewModes.cs.
static const int DebugViewModeSlice = 7;
// Mode 8 (mask density) tints each pixel by its tile's kept-instance count — cull correctness by eye, and the way the
// lead WATCHES the storm cliff. Mode 9 (overshoot detector) marches the pixel TWICE — the production Lipschitz-clamped
// field and the same field with the clamp forced to 1.0 — and colors their depth disagreement (the liar's-spiral class
// as a live view). BOTH skip the primary march (mask reads the mask buffer directly; overshoot runs its own two
// marches), so neither needs the slice's beam force-survive: like the termination view they show what the PIPELINE
// dispatched (a beam-culled tile reads as background). KEEP IN SYNC with DebugViewModes.Names in
// src/Puck.SdfVm/DebugViewModes.cs.
static const int DebugViewModeMask = 8;
static const int DebugViewModeOvershoot = 9;
// Mode 10 (eval-count heatmap, perf-plan Phase 0 instrumentation: docs/reviews/2026-07-16-sdf-renderer-sota-perf-
// plan.md). UNLIKE every other debug mode, this one needs the REAL final-shading epilogue to run (normal, soft
// shadow, AO, coverage-AA) so the tallied count reflects actual per-frame cost — see useFinalShading below, which
// folds this mode in alongside the final-image modes instead of skipping straight to a cheap switch-only case.
static const int DebugViewModeEvals = 10;

// The analytic-normal A/B toggle (the forward-mode dual's debug lever). Rides a reserved lane of the grid-object-params
// screen-light row (SdfGridObjParams.z): 0 (the DEFAULT) selects the analytic dual normal (calculateNormalAnalytic),
// 1 selects the 4-tap finite-difference probe (calculateNormal) for comparison under debug.view.normals.
// Decoded only under SDF_SCREEN_SOURCES — the world-views kernel is the sole SDF-hit shader; every other config keeps
// analytic. KEEP IN SYNC with SdfFrame.UseFiniteDifferenceNormals and SdfWorldEngine.PackScreenLights.
bool worldUseTapNormals() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfGridObjParams].z > 0.5);
#else
    return false;
#endif
}

// The four engine-bench shader-feature levers (sdf.soft-shadows / sdf.ao / sdf.shadow-distance /
// sdf.screen-lights). Ride the reserved bench-params screen-light row (SdfBenchParams): x = disable soft shadows,
// y = disable AO, z = shadow-distance scale (0 => the full 1.0 reach, so an unset frame uploads 0 and is unchanged),
// w = disable screen lights. Decoded only under SDF_SCREEN_SOURCES (the world-views kernel is the sole lit SDF shader);
// every other config keeps the shipped defaults. KEEP IN SYNC with SdfFrame's DisableSoftShadows/DisableAmbientOcclusion/
// ShadowDistanceScale/DisableScreenLights fields and SdfWorldEngine.PackScreenLights.
bool worldSoftShadowsDisabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfBenchParams].x > 0.5);
#else
    return false;
#endif
}
bool worldAoDisabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfBenchParams].y > 0.5);
#else
    return false;
#endif
}
float worldShadowDistanceScale() {
#ifdef SDF_SCREEN_SOURCES
    float s = sdfScreenLights[SdfBenchParams].z;
    return ((s > 0.0) ? s : 1.0);
#else
    return 1.0;
#endif
}
bool worldScreenLightsDisabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfBenchParams].w > 0.5);
#else
    return false;
#endif
}
// The F1 FAR-BOUND A/B lever (perf plan Phase 5.1). Rides SdfFarFieldParams.x: 0 (the DEFAULT, an unset frame) keeps
// the beam-published far bound ACTIVE (the shipped behavior — the fine march exits at traveled >= farBound); 1 pushes
// the far bound out of reach so the march runs to MaxDistance exactly as pre-F1 (the paired-run "off" side). Decoded
// only under SDF_SCREEN_SOURCES (the world-views kernel is the sole SDF-hit shader). KEEP IN SYNC with
// SdfFrame.DisableFarBound and SdfWorldEngine.PackScreenLights.
bool worldFarBoundDisabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfFarFieldParams].x > 0.5);
#else
    return false;
#endif
}
// The F2 SHADOW LIGHT-SIDE EXIT A/B lever (perf plan Phase 5.1). Rides SdfFarFieldParams.y: 0 (the DEFAULT, an unset
// frame) keeps softShadow's no-further-darkening early exit ACTIVE (the shipped behavior); 1 disables it so the shadow
// march runs its full step budget/reach exactly as pre-F2 (the paired-run "off" side). Same SDF_SCREEN_SOURCES decode
// discipline as worldFarBoundDisabled. KEEP IN SYNC with SdfFrame.DisableShadowFarExit and
// SdfWorldEngine.PackScreenLights.
bool worldShadowFarExitDisabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfFarFieldParams].y > 0.5);
#else
    return false;
#endif
}
// PATH B — the SHADOW-PROXY lever (sdf.shadow-proxy): when enabled, sdfShadowGather OMITS Subtraction-family carve
// instances (host-flagged SHADOW-TRANSPARENT) from the soft-shadow occluder set, so the shadow march evaluates the
// pre-carve union hull — O(few) on a dense carve cluster by construction, collapsing the shadow re-march the frame is
// bound on. Conservative: skipping a pure carve can only make the field MORE solid, so shadows go darker/never leak.
// Default 0 = OFF (an unset frame uploads 0 and is byte-identical). Rides SdfShadowProxyParams.x. KEEP IN SYNC with
// SdfFrame.EnableShadowProxy and SdfWorldEngine.PackScreenLights.
bool worldShadowProxyEnabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfShadowProxyParams].x > 0.5);
#else
    return false;
#endif
}
// The dense-crowd approximation: reuse Stage 0's camera-tile mask for the shadow march and skip the per-lit-pixel
// shadow-grid gather. It can omit an off-camera occluder whose shadow reaches into the tile, so it is opt-in and the
// default remains the correctness-complete gathered mask. SdfShadowProxyParams.y; see SdfFrame.
bool worldUseCameraTileShadowMask() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfShadowProxyParams].y > 0.5);
#else
    return false;
#endif
}
// Dense-scene presentation path: bound the number, reach, and spacing of shadow samples. Default false preserves the
// full quality path for every engine consumer; Puck.World opts in only at its declared fleet tiers.
bool worldUseFastSoftShadowMarch() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfShadowProxyParams].z > 0.5);
#else
    return false;
#endif
}
// Fleet-scale contact AO: one calibrated middle-rung field sample instead of the quality path's three samples.
// Default false preserves the full ladder for every engine consumer. SdfShadowProxyParams.w; see SdfFrame.
bool worldUseFastAmbientOcclusion() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfShadowProxyParams].w > 0.5);
#else
    return false;
#endif
}

#ifdef SDF_SCREEN_SOURCES
// The soft-shadow GRID-CULL A/B lever (the sdf.shadowcull verb). Rides SdfGridObjParams.w: 0 (the DEFAULT, an unset
// frame uploads 0) = ON — the grid-gathered shadow-ray march; 1 = OFF — the flat all-instances march (the ground-truth
// reference the Post gate matches, and the A/B lever's slow reference). KEEP IN SYNC with SdfFrame.DisableShadowCull
// and SdfWorldEngine.PackScreenLights.
bool worldShadowCullEnabled() {
    return (sdfScreenLights[SdfGridObjParams].w < 0.5);
}

// Build the shadow-ray candidate mask into sdfShadowMaskWords for a soft-shadow march from `origin` toward `direction`
// out to `reach` (ShadowMaxDistance). Returns true when the mask is COMPLETE (the program fits SDF_SHADOW_MASK_WORDS and
// packs an enabled grid) — softShadow then marches it under sdfShadowMaskActive; false => the caller marches the flat
// all-instances field. SUPERSET-PRESERVING: it sets a bit for every instance whose bound falls within the shadow ray's
// PENUMBRA CONE (chord ShadowPenumbraChord = 1/ShadowSharpness — see that constant: an occluder softens the shadow out
// to perpendicular distance boundRadius + traveled/ShadowSharpness, so the exact ray is NOT enough), so mapMasked over
// this mask equals the flat map() soft shadow TO THE BIT — a wider-than-direct-penumbra cone so the parabola coupling
// occluder is caught too (see ShadowPenumbraChord); an omitted instance can neither lower a sample's clearance below
// full light nor perturb a shadowing sample's parabola, so it cannot change the result min. The walk is collectInstanceGridMask's
// SAME robust-slabs cone rasterization, the cone here being the penumbra cone about the shadow ray, capped at the march
// reach + footprintPad (samples past `reach` are never taken). KEEP THE WALK IN SYNC with sdf-instance-cull.comp.hlsl's
// collectInstanceGridMask (the device-buffer twin — a hand-maintained near-clone, like the EmitShape pair): same cell
// rasterization, same footprintPad contract; only the bit TARGET (this static array), the chord (the penumbra
// aperture), and the tExit cap (the shadow reach) differ. World segments need no bit — mapCore always evaluates them.
// Returns the fallback DECISION so the caller marches correctly whether or not the mask was built:
//   2 = mask BUILT into sdfShadowMaskWords — march it (the cull);
//   1 = a grid is packed but the program has MORE instances than the local mask can address — march the CAMERA-TILE
//       mask (the pre-cull shipped fallback, cheap; NOT the ~20x-slower flat all-instances march);
//   0 = NO grid packed (a grid-suppressed or few-instance program) — march the FLAT all-instances field, which for a
//       few-instance program is cheap AND, for a deliberately grid-suppressed program, MATCHES the grid-present gather
//       so the grid toggle stays render-invariant (the world-grid-cull grid==flat contract).
uint sdfShadowGather(float3 origin, float3 direction, float reach) {
    uint packedInstanceCount = sdfInstanceCount();
    uint instanceCount = min(packedInstanceCount, SDF_MAX_INSTANCES);

    uint instanceOffset = sdfInstanceDirectoryOffset();
    SdfInstanceGridHeader grid = sdfLoadInstanceGridHeader(instanceOffset, packedInstanceCount);

    if (!grid.enabled) {
        return 0u; // no grid — flat fallback (cheap for few instances; matches a would-be gather so the grid toggle is invariant)
    }

    uint wordCount = sdfInstanceMaskWordCount(instanceCount);

    if (wordCount > SDF_SHADOW_MASK_WORDS) {
        return 1u; // grid packed but too many instances to address the local mask — the camera-tile fallback (no flat catastrophe)
    }

    [loop]
    for (uint z = 0u; (z < wordCount); z++) {
        sdfShadowMaskWords[z] = 0u;
    }

    float chord = ShadowPenumbraChord; // the soft penumbra cone's half-slope, NOT a bare ray (see ShadowPenumbraChord)
    float inverseAperture = rsqrt(max((1.0 - (chord * chord)), 1.0e-6));

    // PATH B — the shadow proxy (sdf.shadow-proxy): when enabled, a SHADOW-TRANSPARENT instance (a host-flagged pure
    // Subtraction-family carve) is NOT added to the mask, so the soft-shadow march evaluates the pre-carve union hull.
    // SOUNDNESS: the mask IS the candidate set the march reads, so the gather's skip set and the march's field are the
    // SAME field by construction (the cull-set/march-agreement rule, the same shape sdf.shadow-distance's shared reach
    // follows) — a skipped carve is simply never composed, and a Subtraction only ever removes material, so the shadow
    // is conservatively darker, never light-leaked. Default OFF, so an unset frame gathers the full set as before.
    bool shadowProxy = worldShadowProxyEnabled();

    // (1) The ALWAYS-tested list — dynamic + unmaskable instances the frozen grid cannot bin — against the penumbra cone.
    [loop]
    for (uint a = 0u; (a < grid.alwaysCount); a++) {
        uint index = sdfGridWordAt(grid, grid.alwaysWord + a);
        float4 bound = sdfInstanceBoundAt(instanceOffset, index);

        // Per-instance shadow-participation skip (the cheaper-mask twin of sdfNextVisibleInstanceRange's enumeration
        // skip): a shadow-suppressed dynamic instance never enters the mask, so it costs no mask bit and mode-2's march
        // stays consistent with the enumerate-skip. Gated on the raw condition — the gather is inherently shadow-scoped.
        if (sdfInstancePassesTileCone(bound, origin, direction, chord, inverseAperture) && !(shadowProxy && sdfInstanceShadowTransparent(instanceOffset, index)) && !sdfInstanceShadowSuppressed(instanceOffset, index)) {
            sdfShadowMaskWords[index >> 5u] |= (1u << (index & 31u));
        }
    }

    // (2) The grid cells the penumbra cone sweeps, far bound capped at the shadow reach + the query pad — the same
    // robust-slabs clip + cell rasterization the beam cull uses (see collectInstanceGridMask), the cone here being the
    // penumbra cone about the shadow ray.
    float3 gridMin = grid.origin;
    float3 gridMax = (grid.origin + (float3(grid.dims) * grid.cellSize));
    float3 farCorner = float3(
        ((direction.x > 0.0) ? gridMax.x : gridMin.x),
        ((direction.y > 0.0) ? gridMax.y : gridMin.y),
        ((direction.z > 0.0) ? gridMax.z : gridMin.z)
    );
    float projection = max(dot((farCorner - origin), direction), 0.0);
    float tFar = min(((projection + grid.footprintPad) / max((1.0 - chord), 0.01)), (reach + grid.footprintPad));

    float inflate = ((chord * tFar) + grid.footprintPad); // the widest query radius any slab uses (chord grows it with t)
    float3 clipMin = (gridMin - inflate);
    float3 clipMax = (gridMax + inflate);
    float tEnter = 0.0;
    float tExit = tFar;

    [unroll]
    for (int axis = 0; (axis < 3); axis++) {
        float dir = direction[axis];
        float ori = origin[axis];

        if (abs(dir) > 1.0e-8) {
            float tA = ((clipMin[axis] - ori) / dir);
            float tB = ((clipMax[axis] - ori) / dir);

            tEnter = max(tEnter, min(tA, tB));
            tExit = min(tExit, max(tA, tB));
        }
        else if ((ori < clipMin[axis]) || (ori > clipMax[axis])) {
            return 2u; // the cone provably misses the grid on this axis — only the always-list bits (set above) matter
        }
    }

    if (tEnter > tExit) {
        return 2u; // the cone never overlaps the grid — always-list only
    }

    int3 dimensionsMinusOne = (int3(grid.dims) - int3(1, 1, 1));
    float slabStep = (grid.cellSize * SDF_GRID_SLAB_CELLS);
    float t0 = tEnter;

    [loop]
    for (uint slab = 0u; (slab < SDF_GRID_MAX_SLABS); slab++) {
        float t1 = (((slab + 1u) < SDF_GRID_MAX_SLABS) ? min((t0 + slabStep), tExit) : tExit);
        float3 c0 = (origin + (direction * t0));
        float3 c1 = (origin + (direction * t1));
        float radius = ((chord * t1) + grid.footprintPad);
        float3 low = (min(c0, c1) - radius);
        float3 high = (max(c0, c1) + radius);

        if (all(high >= gridMin) && all(low <= gridMax)) {
            int3 cellLow = clamp(int3(floor((low - grid.origin) * grid.invCellSize)), int3(0, 0, 0), dimensionsMinusOne);
            int3 cellHigh = clamp(int3(floor((high - grid.origin) * grid.invCellSize)), int3(0, 0, 0), dimensionsMinusOne);

            [loop]
            for (int cz = cellLow.z; (cz <= cellHigh.z); cz++) {
                [loop]
                for (int cy = cellLow.y; (cy <= cellHigh.y); cy++) {
                    [loop]
                    for (int cx = cellLow.x; (cx <= cellHigh.x); cx++) {
                        uint cell = ((((uint)cz * grid.dims.y) + (uint)cy) * grid.dims.x) + (uint)cx;
                        uint entryStart = sdfGridWordAt(grid, grid.cellStartWord + cell);
                        uint entryEnd = sdfGridWordAt(grid, grid.cellStartWord + cell + 1u);

                        [loop]
                        for (uint k = entryStart; (k < entryEnd); k++) {
                            uint index = sdfGridWordAt(grid, grid.entryWord + k);
                            float4 bound = sdfInstanceBoundAt(instanceOffset, index);

                            // Per-instance shadow-participation skip — same raw-condition test as the always-list loop
                            // above: a shadow-suppressed dynamic instance is kept out of the shadow mask.
                            if (sdfInstancePassesTileCone(bound, origin, direction, chord, inverseAperture) && !(shadowProxy && sdfInstanceShadowTransparent(instanceOffset, index)) && !sdfInstanceShadowSuppressed(instanceOffset, index)) {
                                sdfShadowMaskWords[index >> 5u] |= (1u << (index & 31u));
                            }
                        }
                    }
                }
            }
        }

        if (t1 >= tExit) {
            break;
        }

        t0 = t1;
    }

    return 2u;
}
#endif

// De-scale a Lipschitz-CLAMPED field sample back to WORLD units — the ONE primitive genuinely shared by the three
// shading-epilogue field walks (softShadow, calcAO, coverage-AA). mapMasked/map return the field pre-multiplied by the
// per-program stepScale (the <= 1-Lipschitz clamp); a consumer that COMPARES a sample against a world-space
// quantity must divide that clamp back out FIRST, or its result tracks the program's stepScale bake instead of geometry
// — the ~30%-darkening chamfer bug (stepScale = 1/sqrt(2)) a prior fix already closed. The three consumers each divide
// it back for a DELIBERATELY DIFFERENT world-space comparison — this is the divide-back FOOT-GUN the docs warn re-fixers
// about, so factor only the divide, never the surrounding intent:
//   - softShadow  — the penumbra parabola's closest-approach miss distance (clearanceTrue); the RAW clamped sample is
//                   kept for the step advance AND the `traveled` denominator (an under-step is conservative), so ONLY
//                   the parabola's numerator/miss is de-scaled. The closest-approach parabola itself is softShadow-
//                   EXCLUSIVE (calcAO is a fixed rung ladder, coverage a terminal ratio).
//   - calcAO      — the rung distance d in the (h - d) open-space deficit (a world-space rung minus a field sample).
//   - coverage-AA — the open-space RISE (aheadField - terminalRadius), a world-space DIFFERENCE. It deliberately does
//                   NOT de-scale the coverage RATIO, which stays in the SAME clamped units as the footprint termination
//                   test it mirrors; the ratio must remain in clamped units.
// stepScale == 1.0 EXACTLY for an isometric, warp-free program and x / 1.0f == x to the bit, so those scenes stay
// byte-identical whether the divide inlines here or is spelled at the call site.
float sdfDeScaleField(float clampedSample, float stepScale) {
    return (clampedSample / stepScale);
}

// A soft shadow toward the (directional) sun: a penumbra march of the field from the surface point up toward
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
float softShadow(float3 surfacePoint, float3 surfaceNormal, float3 lightDirection, uint instanceMaskBase, float stepScale, float reach) {
    float3 origin = (surfacePoint + (surfaceNormal * ShadowBias));
    float traveled = ShadowBias;
    float result = 1.0;
    float previousTrue = 1.0e20; // the previous sample's clearance, seeded large so the first step's closest-approach y is ~0.
    bool fastMarch = worldUseFastSoftShadowMarch();
    bool farExit = !worldShadowFarExitDisabled(); // F2 no-further-darkening early exit (default ON; A/B lever = off)
    reach = (fastMarch ? min(reach, FastShadowMaxDistance) : reach);
    int stepBudget = (fastMarch ? FastShadowSteps : ShadowSteps);
    float stepCeiling = (fastMarch ? FastShadowStepMax : ShadowStepMax);
    float farSlope = (fastMarch ? FastShadowStepFarSlope : ShadowStepFarSlope);

    [loop]
    for (int step = 0; (step < stepBudget); step++) {
        float clearance = mapDistanceMasked(origin + (lightDirection * traveled), instanceMaskBase);

        sdfEvalCount += 1.0; // one march sample, regular or fast — both variants share this call site

        if (clearance < (SurfaceEpsilon * stepScale)) {
            return 0.0; // fully occluded
        }

        // Closest-approach refinement over the classic k*h/t penumbra term: treat the previous and current SDF samples
        // as a local parabola and recover the PERPENDICULAR miss distance `d` at the estimated closest point BETWEEN
        // samples (y = h^2/(2*ph), d = sqrt(h^2 - y^2)), which avoids step-frequency banding —
        // same per-step cost bar two ops and a sqrt, no extra map() evals. LOAD-BEARING pin: y and d are WORLD-space,
        // so the Lipschitz clamp is divided back out of the sample FIRST (clearanceTrue = clearance / stepScale) — the
        // denominator keeps the RAW `traveled` exactly as the classic form did, so in the far limit (y->0,
        // d->clearanceTrue) this reduces to k*clearance/(traveled*stepScale), and an isometric program
        // (stepScale == 1) stays byte-identical. Mixing a scaled clearance into y/d darkens chamfered surfaces.
        float clearanceTrue = sdfDeScaleField(clearance, stepScale);
        float y = ((clearanceTrue * clearanceTrue) / (2.0 * previousTrue));
        // Closest-point-behind guard: when the parabola places the estimated closest point AT OR BEYOND the
        // current sample (y >= clearanceTrue — a tight graze followed by an opening field, or the degenerate first
        // sample where previousTrue is seeded huge), d = sqrt(clearanceTrue^2 - y^2) collapses to 0 and result LATCHES
        // to full occlusion (the black-speck band). Fall back to the classic k*clearanceTrue/traveled term for that
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

        if (fastMarch && (result <= FastShadowDarknessCutoff)) {
            return 0.0;
        }

        // F2 NO-FURTHER-DARKENING EARLY EXIT (perf plan Phase 5.1). `result` is a running MIN, so continuing can only
        // DARKEN; exit as soon as the remaining march provably cannot lower it. The field is 1-Lipschitz along the ray,
        // so for every future t' in (traveled, reach] the TRUE (de-scaled) clearance c' >= clearanceTrue - (t' - traveled)
        // >= clearanceTrue - (reach - traveled) =: cMin. The classic penumbra term k*c'/t' (k = ShadowSharpness) is
        // decreasing in t' along that floor, so its infimum over the remaining march is k*cMin/reach at t' = reach.
        //   EXIT INEQUALITY:  ShadowSharpness*(clearanceTrue - (reach - traveled)) >= result*reach
        //   <=>  clearanceTrue >= (reach - traveled) + result*reach/ShadowSharpness   (design §1b form)
        // When it holds, cMin >= result*reach/k > 0, so (a) full occlusion (clearance -> 0) is impossible for the rest of
        // the march and (b) the classic term never drops below `result`. This is SOUND vs the classic penumbra term AND
        // vs the true continuous penumbra (physical remaining shadow >= k*cMin/reach >= result), so the exit never
        // brightens a pixel above its correct soft-shadow value.
        //   PARABOLA CAVEAT (why this is MARCH-PATH, not bit-identical): the Aaltonen closest-approach refinement above
        // reports k*d/(t'-y), d = sqrt(c'^2 - y^2), y = c'^2/(2*prev). Holding c' fixed, that sample's infimum over the
        // parabola regime (y < c') is 0 — reached as y -> c', i.e. prev -> c'/2, the near-radial-escape knife-edge just
        // inside the y>=c fallback guard (per-step growth ratio c'/prev -> 2, the 1-Lipschitz cap). So NO finite clearance
        // margin makes the parabola's worst case >= result: the strong (bit-identical) form does NOT close. Where the
        // shipped march would take that undershoot past this exit, skipping it makes the pixel brighter than the full
        // march but NOT brighter than truth (the undershoot is itself a shipped estimator artifact below the true
        // penumbra k*cMin/reach). Classified MARCH-PATH (solidity + parity families gate it).
        if (farExit && ((ShadowSharpness * (clearanceTrue - (reach - traveled))) >= (result * reach))) {
            return result;
        }

        previousTrue = clearanceTrue;
        // The step clamp: the floor keeps a near-tangent march from stalling (at the cost of stepping through
        // occluders thinner than it), the ceiling keeps the penumbra from over-marching past a grazing silhouette. The
        // ceiling is now DISTANCE-PROPORTIONAL (max(ShadowStepMax, traveled*ShadowStepFarSlope)): unchanged in the near
        // field where the fixed ShadowStepMax dominates, relaxing only once the ray is far enough that its penumbra
        // angular size shrinks — so a far unoccluded ray clears open space in fewer steps.
        // FOLD-SAFE: the advance also honors the published boundary gap (min) so a shadow ray cannot stride across a
        // fold boundary the raw value lies about; the penumbra SAMPLE above stays on the raw value (an occasional
        // boundary overestimate lightens a penumbra ratio slightly — occlusion soundness comes from the stepping).
        traveled += clamp(min(clearance, sdfMapStepBound), ShadowStepMin, max(stepCeiling, (traveled * farSlope)));

        if (traveled > reach) {
            break;
        }
    }

    return result;
}
// Normal-ladder ambient occlusion (calcAO): from the hit, step a short ladder of fixed rungs OUTWARD along
// the surface normal; at each rung compare the distance expected to travel (h) against what the field actually reports
// (d) — where nearby geometry crowds the normal the field under-reports and the deficit (h - d) accumulates as
// occlusion, with an outer-rung falloff and a gain/clamp. THREE mapMasked() calls (was five): the rungs are
// re-spaced to span the SAME 0.01..0.13 reach at double pitch, the per-rung falloff squared (0.95^2 = 0.9025) to hold
// the same spatial decay, and the gain re-tuned 3.0 -> 5.07 so the fully-occluded floor matches the 5-tap value to
// ~0.01 (verified analytically over constant-factor and constant-gap occluder models) — a same-look AO at 60% of the
// taps, and because calcAO is [unroll]ed the two dropped taps also relieve the views kernel's register pressure. Paid
// ONLY on lit hits and tile-masked exactly like softShadow (a masked-out instance is as absent from a nearby tap as it
// is from the hit itself). Purely local — no cones, no hemisphere, no history — but reads convincingly as contact
// shadowing in creases and under overhangs.
//
// Applied to the AMBIENT/sky fill ONLY, never the sun: soft shadows govern direct light, and multiplying occlusion into
// direct light double-darkens it (occlusion and shadow would both attenuate the same light twice). The (h - d)
// subtract mixes a WORLD-space rung h with a mapMasked distance pre-scaled by the Lipschitz clamp, so d is divided
// back to world units FIRST (d / stepScale) — the same divide-back softShadow applies; without it occlusion strength
// tracks each program's stepScale bake, not geometry.
// `stepScale` is renderView's hoisted Lipschitz clamp (see softShadow): the (h - d) rung subtract mixes a world-space
// rung with a mapMasked distance, so d is divided back to world units by it first.
float calcAO(float3 surfacePoint, float3 surfaceNormal, uint instanceMaskBase, float stepScale) {
    float occlusion = 0.0;
    float scale = 1.0;

    [unroll]
    for (int i = 0; (i < 3); i++) {
        float h = (0.01 + ((0.12 * float(i)) / 2.0));
        float d = sdfDeScaleField(mapDistanceMasked(surfacePoint + (surfaceNormal * h), instanceMaskBase), stepScale);

        sdfEvalCount += 1.0; // one of the three AO rungs

        occlusion += ((h - d) * scale);
        scale *= 0.9025;
    }

    return clamp((1.0 - (5.07 * occlusion)), 0.0, 1.0);
}
// One-sample fleet approximation of calcAO. The middle quality rung (h=.07) captures the contact/crease signal. Its
// gain matches the three-rung ladder's constant-factor response: 5.07*(.01 + .9025*.07 + .9025^2*.13)/.07 = 12.97.
// It also matches the small-gap response within ~6%, retaining the grounding cue while removing two field walks.
float calcFastAO(float3 surfacePoint, float3 surfaceNormal, uint instanceMaskBase, float stepScale) {
    const float h = 0.07;
    float d = sdfDeScaleField(mapDistanceMasked(surfacePoint + (surfaceNormal * h), instanceMaskBase), stepScale);

    sdfEvalCount += 1.0; // the single fleet-tier AO tap

    return clamp((1.0 - (12.97 * (h - d))), 0.0, 1.0);
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

// The overshoot detector's plain sphere march (DEBUG VIEW ONLY — never on the shipped shading path). Returns the
// terminal depth for a footprint-adaptive sphere trace of the tile-masked field, stepping by (radius * stepMultiplier):
// stepMultiplier = 1 marches the PRODUCTION Lipschitz-clamped field (mapMasked already bakes stepScale, so radius is the
// safe clamped distance), while 1/stepScale FORCES the clamp back to 1.0 — the step then rides the raw, possibly-non-1-
// Lipschitz field, so a twisted/warped program TUNNELS the thin geometry the clamp exists to hold. debug.view.overshoot
// colors the two terminals' disagreement. Plain omega=1 (no auto-relaxation) so the ONLY variable between the two
// marches is the clamp; the four-bound teleport rides both (bound-proven on either). The hit ACCEPT compares the clamped
// radius against the same footprint threshold the production march uses — only the STEP is enlarged, so the enlarged
// step can jump PAST a surface before the sample reads a hit (the overshoot). Two full marches per pixel is the
// documented debug cost — the overshoot case gates the primary march OFF, so a pixel runs this twice and the production
// marcher zero times.
float marchOvershootDepth(float3 rayOrigin, float3 rayDirection, float marchStart, float firstExit, float secondEntry, uint instanceMaskBase, float pixelFootprint, float stepMultiplier) {
    if (marchStart < 0.0) {
        return MaxDistance; // a beam-culled tile — nothing to march; both marches agree at the far plane
    }

    float traveled = max(marchStart, 0.0);

    [loop]
    for (int step = 0; (step < MaxSteps); step++) {
        float radius = mapDistanceMasked(rayOrigin + (rayDirection * traveled), instanceMaskBase);
        float hitThreshold = max(SurfaceEpsilon, (pixelFootprint * traveled));

        // Accept on the CLAMPED field (production-consistent), so a landed-inside sample (radius < threshold, incl.
        // negative) ends the march before any backward step. Tunneling happens when the enlarged step below clears the
        // thin band so no sample ever lands within the threshold inside it.
        if (radius < hitThreshold) {
            break;
        }

        // FOLD-SAFE: both detector marches honor the published boundary gap, so the ONLY remaining variable between
        // them stays the Lipschitz clamp (the detector's purpose) — boundary striding is fixed on the shipped path.
        traveled += (min(radius, sdfMapStepBound) * stepMultiplier);

        // The four-bound teleport (bound-proven for either march): jump the proven-empty gap once.
        if ((traveled >= firstExit) && (traveled < secondEntry)) {
            traveled = secondEntry;
        }

        if (traveled > MaxDistance) {
            traveled = MaxDistance;
            break;
        }
    }

    return traveled;
}

float3 renderView(ViewportData view, float2 localUv, float marchStart, float firstExit, float secondEntry, float farBound, uint instanceMaskBase, float pixelFootprint) {
    float3 rayOrigin = view.position.xyz;
    float3 rayDirection = cameraRayDirection(view, localUv);
    int viewMode = (int)round(view.forward.w);
    float time = view.position.w;

    sdfEvalCount = 0.0; // fresh tally for this pixel — see debug.view.evals (case 10 below)

    float traveled = max(marchStart, 0.0);
    bool hitSurface = false;
    int material = 0;
    // Material blend at smooth seams (sdf-vm.hlsli's sdfMaterialBlendWeight): captured from the ACCEPT-sample march call
    // alongside `material`, because the normal/AO/shadow map calls after the loop clobber the per-thread channel. Weight 0
    // (no smooth seam within a blend radius of the hit) => the shade below is the exact table lookup, unchanged.
    float materialBlendWeight = 0.0;
    int materialBlendOther = 0;
    int marchStep = 0;
    // Tier-0 coverage AA: the CLAMPED field at the accepted hit (the terminal-step residual), captured by both march
    // paths at their hit-accept. The coverage metric derived from it in the epilogue must live in the SAME units as
    // the footprint-adaptive termination test (clamped radius vs hitThreshold) — do NOT divide by stepScale here.
    // The divide-back that is correct for softShadow/calcAO (world-space geometric comparisons) is WRONG for this
    // metric: de-scaling inflates the ratio by 1/stepScale and saturates solid hits, erasing the coverage signal.
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
    // MASK (reads the tile mask buffer directly) and OVERSHOOT (runs its OWN two marches in its case) skip the primary
    // march too — for MASK it is unused work, for OVERSHOOT running it AS WELL would be a third march. Every non-debug
    // and every OTHER debug mode still marches exactly as before (the added compares are false for them).
    if ((marchStart >= 0.0) && (viewMode != DebugViewModeSlice) && (viewMode != DebugViewModeMask) && (viewMode != DebugViewModeOvershoot)) {
        // Sphere-trace to the surface with a footprint-ADAPTIVE hit threshold. The field mapMasked returns is already
        // Lipschitz-clamped (SdfProgram stepScale; <= 1-Lipschitz along the ray), so over-relaxing stays
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

            sdfEvalCount += 1.0; // one primary-march sample

            // FOLD-SAFE split: STEP (sizing, unbounding spheres, the slope EMA) on min(value, sdfMapStepBound) —
            // the sound marchable field near a fold boundary — but TERMINATE on the raw value (exact in the owning
            // cell; the bound never invents a phantom boundary hit). Fold-free programs: the min is the identity.
            float fieldDistance = hit.distance;
            float radius = min(fieldDistance, sdfMapStepBound);
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
            if (!overshoot && (fieldDistance < hitThreshold)) {
                hitSurface = true;
                material = hit.material;
                // This mapMasked() call evaluated at exactly surfacePoint (traveled is frozen at the break), so its
                // per-thread material blend channel describes THIS hit's winning smooth seam — capture it now, before the
                // epilogue's normal/AO/shadow marches overwrite the static.
                materialBlendWeight = sdfMaterialBlendWeight;
                materialBlendOther = sdfMaterialBlendOther;
                terminalRadius = fieldDistance;
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
                // Strict keeps omega latched at 1 after an overshoot and across teleports.
                // Only previousRadius/stepLength reset, so the disjoint-sphere
                // step-back restarts cleanly at the landing sample without resurrecting over-relaxation the overshoot
                // already retired.
#endif
            }

            // F1 FAR-FIELD EXIT: past the tile's beam-proven far bound no ray in the tile can produce a hit the fine
            // march would ACCEPT (coneMarchFarBound proved it against the footprint-inflated threshold), so the ray
            // renders skyColor whether it exits here or marches on — OUTPUT-IDENTICAL, only fewer steps. farBound =
            // MaxDistance (no bound proven, or the A/B lever pushed it out of reach) makes this a no-op past the far
            // plane the MaxDistance break already handles.
            if (traveled >= farBound) {
                break;
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
        // EVALS rides the SAME epilogue as final shading (normal, soft shadow, AO, screen sampling, coverage-AA):
        // the whole point of the heatmap is to tally what a REAL lit pixel costs, so it cannot take the cheap
        // switch-only shortcut every other debug mode does.
        bool useFinalShading = ((viewMode <= 0) || (viewMode >= DebugViewModeCount) || (viewMode == DebugViewModeEvals));
        bool sampledScreen = false;
#ifdef SDF_SCREEN_SOURCES
        if (useFinalShading) {
            // A bound screen source wins over BOTH the flat sentinel and the procedural test-card: emissive/unlit
            // (the diegetic screen is its own light source, like a real display — no scene lighting dims or tints it),
            // but shaped by the CRT glass face (curvature/bezel/scanlines/vignette/glint/bloom in sampleScreenSurface)
            // before the shared distance fog. The screen ALSO lights the room — see the screen-light loop below.
            sampledScreen = sampleScreenSurface(material, surfacePoint, rayDirection, (pixelFootprint * traveled), color);
        }
#endif

        bool needsLitColor = (useFinalShading && !sampledScreen);
        bool needsNormal = ((viewMode == DebugViewModeNormals) || needsLitColor);

        float curvature = 0.0; // level-set mean curvature at the hit (drives the stylized cavity/rim/ink terms below)

        if (needsNormal) {
            // The curvature variant reuses the normal's four taps plus one center tap (compile-time, off by default).
            // Otherwise the runtime toggle selects between the ANALYTIC forward-mode dual normal (the default — one dual
            // eval, exact through the op chain) and the 4-tap finite-difference probe (worldUseTapNormals, for the
            // A/B lever). The 4-tap path stays compiled; the toggle picks at runtime.
            if (CurvatureShadingEnabled) {
                normal = calculateNormalCurvature(surfacePoint, instanceMaskBase, curvature);
            } else if (worldUseTapNormals()) {
                normal = calculateNormal(surfacePoint, instanceMaskBase);
            } else {
                normal = calculateNormalAnalytic(surfacePoint, instanceMaskBase);
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
            // not black). Skip the march where the surface already faces away from the sun (sunDiffuse == 0) OR when the
            // engine-bench sdf.soft-shadows lever disables it (the sun then goes unshadowed — visually loud, intended).
            // The procedural screen branch below consumes sunDiffuse too, so this march is NOT dead there.
            if ((sunDiffuse > 0.0) && !worldSoftShadowsDisabled()) {
                // ONE shared scaled reach for BOTH the gather cull cone and the march ceiling (the sdf.shadow-distance
                // lever) — they MUST use the same length or the gathered occluder set is unsound for the shadow ray.
                float shadowReach = (ShadowMaxDistance * worldShadowDistanceScale());
#ifdef SDF_SCREEN_SOURCES
                // The shadow GRID CULL (default ON). Gather this lit pixel's shadow-ray grid neighbourhood into the local
                // mask (sdfShadowMaskWords) and march THAT — bit-identical to the flat all-instances march (proven by the
                // world-shadow-cull gate) but restricted to the instances the shadow ray can actually reach, so the
                // shadow walks neither the camera-tile mask (the wrong occluder set for a ray that leaves the camera
                // cone) NOR every instance. sdfShadowGather returns 2 (mask BUILT — the cull), 1 (a grid is packed but
                // the program overflows the local mask → the camera-tile fallback, the cheap pre-cull behaviour, NOT the
                // ~20x-slower all-instances flat), or 0 (NO grid → the flat all-instances fallback, which is cheap for a
                // few-instance program and keeps the grid toggle render-invariant). The cull OFF marches flat
                // all-instances — the ground-truth reference the A/B lever and the world-shadow-cull gate use.
                bool cullOn = worldShadowCullEnabled();
                uint gather = (cullOn ? (worldUseCameraTileShadowMask() ? 1u : sdfShadowGather((surfacePoint + (normal * ShadowBias)), SdfSunDirection, shadowReach)) : 0u);
                bool culled = (gather == 2u);
                uint shadowFallbackMask = ((cullOn && (gather == 1u)) ? instanceMaskBase : SDF_INSTANCE_MASK_ALL);

                sdfShadowMaskActive = culled;
                // Per-instance soft-shadow participation is live for THIS march ONLY (set UNCONDITIONALLY, not gated on
                // `culled`): all three fallback modes — the gather cull, the camera-tile mask, and the flat all-instances
                // walk — resolve through sdfNextVisibleInstanceRange, so a shadow-suppressed dynamic instance (packed
                // position.w > 0.5) must drop out of every one of them identically. camera/AO/coverage marches keep the
                // flag false, so they are untouched.
                sdfShadowParticipationActive = true;
                sunDiffuse *= softShadow(surfacePoint, normal, SdfSunDirection, shadowFallbackMask, stepScale, shadowReach);
                sdfShadowParticipationActive = false;
                sdfShadowMaskActive = false;
#else
                sunDiffuse *= softShadow(surfacePoint, normal, SdfSunDirection, instanceMaskBase, stepScale, shadowReach);
#endif
            }

            if (material >= SDF_SCREEN_MATERIAL) {
                // The procedural test-card face: a declared screen with no source bound this frame (or the plain
                // sentinel). Unlit apart from a faint sun tint — it is its own emitter, so the radiance accumulation
                // below would be discarded. Test the whole sentinel RANGE, never `==`: a screen-instance id is
                // SDF_SCREEN_MATERIAL + 1 + screenIndex and must never index the material table.
                color = (screenContent(surfacePoint, time) * (ScreenCardBase + (ScreenCardSunTint * sunDiffuse)));
            } else {
                // 3-tap normal-ladder AO, into the AMBIENT fill ONLY (the sun stays governed by softShadow above).
                // Computed in the material branch so the emissive screen-card path never pays its five taps. The
                // engine-bench sdf.ao lever forces occlusion to 1 (skipping the ladder's map() evals — creases brighten).
                float ambientOcclusion = (worldAoDisabled()
                    ? 1.0
                    : (worldUseFastAmbientOcclusion()
                        ? calcFastAO(surfacePoint, normal, instanceMaskBase, stepScale)
                        : calcAO(surfacePoint, normal, instanceMaskBase, stepScale)));
                float3 radiance = (float3(1.0, 1.0, 1.0) * (((ambient * ambientScale) * ambientOcclusion) + ((SunWeight * sunDiffuse) * sunScale)));

#ifdef SDF_SCREEN_SOURCES
                // Every BOUND diegetic screen is a colored area light: its position/orientation come from the
                // screen-surface table, its color from the per-frame framebuffer average. The dot(screenNormal, -L) gate
                // is the "light through the glass" cue — a screen only lights what sits in front of its face.
                // SdfScreenLightEnv doubles as the screen-slot COUNT (the environment entry sits right after 0..count-1).
                // The normalize is load-bearing: right/up are unit but not guaranteed orthogonal (see SdfScreenSurface).
                // The engine-bench sdf.screen-lights lever skips the whole additive loop (the CRTs stop spilling glow).
                if (!worldScreenLightsDisabled()) {
                    for (uint lightIndex = 0u; (lightIndex < screenLightLoopBound()); lightIndex++) {
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
                }
#endif

                // MATERIAL BLEND AT SEAMS. The smooth blend eases the DISTANCE across
                // the seam, but `material` is the single integer winner — a hard colour cut at the geometric midpoint.
                // Cross-fade the winner's albedo toward the losing operand captured at the winning smooth blend, by the
                // clamped seam weight (0 at/beyond the blend band, up to 0.5 at the seam centre; symmetric min(h,1-h),
                // so the mix is CONTINUOUS through the winner-flip). HIT-ONLY: one lerp per lit pixel, the channel was
                // already computed by the accept-sample march. Both ids are table materials (the capture zeroes the
                // weight for a screen sentinel) carrying their parityMaterialDelta recolour, so the mixed colour rides
                // the same relaxed material-flip parity family the hard cut already did.
                SdfMaterialData shadeMaterial = sdfMaterialLoad(material);

                if (materialBlendWeight > 0.0) {
                    shadeMaterial.albedo = lerp(shadeMaterial.albedo, sdfMaterialAlbedo(materialBlendOther), materialBlendWeight);
                }

                color = sdfMaterialShade(shadeMaterial, radiance, normal, rayDirection, SdfSunDirection, sunScale);
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
            // sub-pixel silhouette ordered dither cannot) while solid surfaces stay bit-solid. The three signals are:
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
                float aheadField = mapDistanceMasked(surfacePoint + (rayDirection * probeSpan), instanceMaskBase);

                sdfEvalCount += 1.0; // the open-space probe, only when the silhouette gate above admits it

                // The open-space rise is a world-space geometric difference, so divide the Lipschitz clamp back out
                // (same rule as softShadow/calcAO — fault 1 was de-scaling the ABSOLUTE coverage metric, not a difference).
                opened = smoothstep(0.0, (0.5 * probeSpan), sdfDeScaleField((aheadField - terminalRadius), stepScale));
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
            float sliceDistance = mapDistance(rayOrigin + (rayDirection * planeT));

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
        case 8: { // MASK DENSITY — tint by the kept-instance count in this pixel's tile (popcount over the tile's mask
                  // words), normalized by the live instance count. The counts are ALREADY in the mask buffer the views
                  // kernel binds (the beam prepass wrote them), so this is one popcount loop — no march, no field eval.
                  // Cull behaviour and tile-boundary artifacts become visible BY CONSTRUCTION: each tile's density is a
                  // single value, so adjacent tiles that kept different counts show a hard colour step. A world-only
                  // program (0 instances) reads 0 → the floor colour. This is how the lead WATCHES the storm cliff — a
                  // dense red field over the swarm means many instances survive the cull into each tile.
            uint liveInstances = sdfInstanceCount();
            uint keptInstances = 0u;

            [loop]
            for (uint maskWord = 0u; (maskWord < params.instanceMaskWordCount); maskWord++) {
                keptInstances += countbits(sdfInstanceMaskWord(instanceMaskBase, maskWord, liveInstances));
            }

            // Fraction of the live instances this tile keeps. The sqrt lifts the low end so a handful of survivors out of
            // thousands still registers as green rather than washing to the floor blue — the ramp stays perceptible
            // across the whole range while the NORMALIZATION base stays the live count (as specified).
            float density = ((liveInstances > 0u) ? (float(keptInstances) / float(liveInstances)) : 0.0);
            float ramp = sqrt(saturate(density));
            // dark blue (0) -> green (low) -> red (high).
            float3 lowBand = lerp(float3(0.04, 0.07, 0.32), float3(0.14, 0.85, 0.30), saturate(ramp * 2.0));
            viewColor = lerp(lowBand, float3(0.95, 0.16, 0.10), saturate((ramp - 0.5) * 2.0));
            break;
        }
        case 9: { // OVERSHOOT DETECTOR — march the pixel TWICE and colour the depth disagreement. The first march is the
                  // production Lipschitz-CLAMPED field (stepMultiplier 1); the second forces the clamp to 1.0
                  // (stepMultiplier 1/stepScale) so the step rides the raw, possibly-non-1-Lipschitz field and TUNNELS
                  // thin geometry the clamp holds. Where they agree the clamp was not load-bearing (green); where the
                  // unclamped march tunneled past a surface the terminals diverge (hot) — the liar's-spiral class made
                  // live. This is a DEBUG-ONLY two-marches-per-pixel cost; the primary march was gated OFF above for it.
            float clampedDepth = marchOvershootDepth(rayOrigin, rayDirection, marchStart, firstExit, secondEntry, instanceMaskBase, pixelFootprint, 1.0);
            float unclampedDepth = marchOvershootDepth(rayOrigin, rayDirection, marchStart, firstExit, secondEntry, instanceMaskBase, pixelFootprint, (1.0 / stepScale));
            float disagreement = abs(clampedDepth - unclampedDepth);
            // Log-scaled against the march reach so a sub-unit tunnel still reads while a full escape saturates.
            float hot = saturate(log2(1.0 + disagreement) / log2(1.0 + MaxDistance));
            // green (agree) -> yellow -> red (the unclamped march tunneled far).
            float3 warmBand = lerp(float3(0.10, 0.70, 0.22), float3(0.98, 0.85, 0.12), saturate(hot * 2.0));
            viewColor = lerp(warmBand, float3(0.96, 0.12, 0.05), saturate((hot - 0.5) * 2.0));
            break;
        }
        case 10: { // EVALS — per-pixel HEATMAP of every map()-family field evaluation tallied this frame (primary
                   // march steps, soft-shadow march steps — regular or fast — the 3/1-tap AO ladder, the analytic-
                   // normal dual or its 4/5-tap fallbacks, and the coverage-AA open-space probe when taken). Unlike
                   // every other numbered mode this one runs the REAL final-shading epilogue (see useFinalShading
                   // above), so sdfEvalCount reflects actual per-frame cost, not a debug shortcut's own cost.
                   // Calibrated ramp (EvalHeatmapCeiling = 256, see its declaration for the worst-case budget this
                   // is sized against): dark blue (idle/background, 0 evals) -> green (a cheap ambient-only hit) ->
                   // yellow (a hit paying the soft-shadow march) -> red (256+, saturating so a runaway pixel reads
                   // solid red instead of wrapping).
            float evalRamp = saturate(sdfEvalCount / EvalHeatmapCeiling);
            float3 coldBand = lerp(float3(0.02, 0.04, 0.20), float3(0.14, 0.85, 0.30), saturate(evalRamp * 2.0));
            viewColor = lerp(coldBand, float3(0.95, 0.16, 0.10), saturate((evalRamp - 0.5) * 2.0));
            break;
        }
    }

    return viewColor;
}

#endif
