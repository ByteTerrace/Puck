// Shared contract and rendering functions for the world kernels. Beam evaluates tile clearance; primary records
// camera hits; views reconstructs hit shading and diagnostics; composite assembles the source images. The scene
// program and cameras remain data. KEEP IN SYNC with SdfWorldEngine's packing and pass order.
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
    // Stage 2 upsamples back into the full region (bilinear; q == 255 takes the exact-copy path). yz = the off-axis
    // (asymmetric) frustum's tangent-space center offset (SdfAsymmetricFrustum) — (0,0) for an ordinary symmetric
    // camera, consumed by cameraRayDirection below. w = the frame's FAR DISTANCE (SdfFrame.FarDistance, read through
    // worldFarDistance below).
    // KEEP IN SYNC with SdfWorldEngine.PackViewports (the 96-byte row) and BuildCompositePush's scaleQPacked.
    float4 renderScale;
};
[[vk::binding(2, 0)]] StructuredBuffer<ViewportData> viewports : register(t1);

// The frame's FAR DISTANCE — the depth at which every camera march ends: the fine march's far exit (renderView), the
// beam's cone proofs (entry, the gap search, the F1 far bound) and the "nothing proven" sentinel every tile plane
// carries, and the depth/overshoot debug ramps. It is WORLD DATA (render.farDistance → SdfFrame.FarDistance, packed
// per view row by SdfWorldEngine.PackViewports — the one buffer every kernel that marches already binds), never a
// shader constant: the host refuses a non-finite or non-positive value before packing, so no kernel guards it.
float worldFarDistance(ViewportData view) {
    return view.renderScale.w;
}

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
    // The deterministic tick clock the sky's twinkle and cloud motion read. Stage 1 only. KEEP IN SYNC with
    // SdfFrame.SampleIndex.
    uint sampleIndex;
};
[[vk::push_constant]] ConstantBuffer<CompositeParams> params;

#if defined(SDF_PRIMARY_PASS) || defined(SDF_PRIMARY_READ)
// Three float4 rows per full-extent pixel per viewport. KEEP IN SYNC with SdfWorldEngine.PrimaryHitByteLength,
// PrimaryHitBindingIndex and its views binding order (tiles u0, five source images u1..u5, hit records u6).
// Row 0: depth, terminal field radius, acceptance threshold, material bits. Row 1: anonymous hit lanes.
// Row 2: frame-slot bits, seam weight, other-material bits, packed step/eval/hit bits. No quantized depth/attributes.
// Scalar uint storage matches the engine's four-byte UAV descriptor stride on both backends.
[[vk::binding(49, 0)]] RWStructuredBuffer<uint> sdfPrimaryHits : register(u6);
uint sdfPrimaryHitOffset(uint2 pixel, uint viewIndex) {
    return (12u * (((viewIndex * params.imageExtent.y) + pixel.y) * params.imageExtent.x + pixel.x));
}
float4 sdfLoadPrimaryRow(uint index) {
    return asfloat(uint4(sdfPrimaryHits[index], sdfPrimaryHits[index + 1u], sdfPrimaryHits[index + 2u], sdfPrimaryHits[index + 3u]));
}
void sdfStorePrimaryRow(uint index, float4 value) {
    uint4 bits = asuint(value);
    sdfPrimaryHits[index] = bits.x;
    sdfPrimaryHits[index + 1u] = bits.y;
    sdfPrimaryHits[index + 2u] = bits.z;
    sdfPrimaryHits[index + 3u] = bits.w;
}
#endif

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
// FAR BOUND: the depth beyond which the tile's cone provably cannot produce ANY footprint-accepted hit through the far
// distance (sdf-beam writes it, sdf-world-views exits the fine march at traveled >= farBound). Each plane is one
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
// (binding 10/11 are not part of the beam prepass or Stage 2's descriptor sets). Indexed DIRECTLY by screen index
// (0..31, the same slot SetScreenSource/screenSources binds) — not by declaration order — so a hit resolves its
// surface with no search; an unfilled slot's entry is never read (no material id can address it: the host packs an
// entry only when SdfProgramBuilder registers that screen index).
struct ScreenSurfaceData {
    float4 right;   // xyz = unit world-space U axis, w = half-width
    float4 up;      // xyz = unit world-space V axis (V=0 at top), w = half-height
    float4 origin;  // xyz = world-space front-face center, w = unused (pad)
};
[[vk::binding(10, 0)]] StructuredBuffer<ScreenSurfaceData> screenSurfaces : register(t4);
// The screenSurfaces[] / sdfDecalCells[] / screenSourceN entry count — the width every screen index is bounded
// against before it indexes one. KEEP IN SYNC with SdfProgramBuilder.MaxScreenSurfaces.
static const uint SdfScreenSurfaceCount = 32u;
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

// Bounded emissive volumes (Puck.SignedDistance.SdfVolume — a participating medium, never a distance-field shape):
// one uint4-free, 11-float4-per-volume table, APPENDED LAST in the views set — binding 48, Direct3D 12 register t43
// (after the frame instance grid t42). Stage 1 is the only kernel that shades, so it is the only one that binds it.
// Decoded and integrated by shade-volumes.hlsli in renderView and the sky prepass. KEEP IN SYNC with
// SdfWorldEngine.PackVolumes / SdfProgramBuilder.MaxVolumes.
[[vk::binding(48, 0)]] StructuredBuffer<float4> sdfVolumes : register(t43);
static const uint SdfVolumeCount = 64u;
#include "shade-volumes.hlsli"

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

    // The sibling of sdf-world-rt-debug's hitMaterial guard: an out-of-bounds structured-buffer read is zeroed on
    // Direct3D 12 by spec but only defined under robustBufferAccess on Vulkan, so the bound makes both backends agree
    // by construction rather than by driver luck. Falling back to the material-shaded path is the same answer a zeroed
    // entry would produce here (no decal, no bound source), and the host refuses such an id, so no valid program
    // reaches this branch and no composed pixel moves.
    if (screenIndex >= SdfScreenSurfaceCount) {
        return false;
    }

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

// Generic surface coverage, available wherever materials shade.
#include "shade-weathering.hlsli"

// The environment's typed reads. worldSunDirection/worldSunColor name the SHADOW light (the one directional whose
// Lambert term is soft-shadowed); with no shadow light they read the pinned sun so the sky disc, the clouds' lighting
// and the material specular still have a key.
struct SdfEnvLight {
    float3 direction; // unit, surface -> light (directional); a world-space POSITION (point)
    float weight;
    float3 color;
    uint kind;        // SdfEnvLight{Directional,Hemisphere,Rim,Point}
    float param;      // penumbra half-slope / hemisphere gradient / rim exponent / point falloff radius
    bool shadows;
    int dynamicSlot;  // point only: the dynamic-transform slot its position rides, or -1 for the static position
};
uint worldLightCount() { return min((uint)max(worldEnvRow(SdfEnvControl).x + 0.5, 0.0), SdfEnvMaxLights); }
int worldShadowLightIndex() { return (int)round(worldEnvRow(SdfEnvControl).y); }
bool worldSkyEnabled() { return (worldEnvRow(SdfEnvControl).z > 0.5); }
float worldSkyFogDensity() { return worldEnvRow(SdfEnvControl).w; }
SdfEnvLight worldLight(uint index) {
    uint row = (SdfEnvLights + (index * SdfEnvRowsPerLight));
    float4 a = worldEnvRow(row);
    float4 b = worldEnvRow(row + 1u);
    float4 c = worldEnvRow(row + 2u);
    SdfEnvLight light;

    light.direction = a.xyz;
    light.weight = a.w;
    light.color = b.rgb;
    light.kind = (uint)(b.w + 0.5);
    light.param = c.x;
    light.shadows = (c.y > 0.5);
    light.dynamicSlot = (int)round(c.z);

    return light;
}
// A point light's current world-space position: the live dynamic transform its slot names (an anchored light
// tracking a moving shape), or the authored static position when unanchored. Falls back to the static position on
// a kernel that binds no per-frame dynamic-transform table.
float3 worldPointLightPosition(SdfEnvLight light) {
#ifdef SDF_DYNAMIC_TRANSFORMS
    if (light.dynamicSlot < 0) return light.direction;
    uint slot = (uint)light.dynamicSlot;
    return sdfDynamicTransforms[3u * slot].xyz + rotatePointByQuaternion(light.direction, sdfDynamicTransforms[3u * slot + 1u]);
#else
    return light.direction;
#endif
}
float3 worldSunDirection() {
    int index = worldShadowLightIndex();

    return ((index >= 0) ? worldLight((uint)index).direction : SdfSunDirection);
}
float3 worldSunColor() {
    int index = worldShadowLightIndex();

    return ((index >= 0) ? worldLight((uint)index).color : float3(1.0, 1.0, 1.0));
}
float worldShadowPenumbraSlope() {
    int index = worldShadowLightIndex();

    return ((index >= 0) ? max(worldLight((uint)index).param, 1.0e-3) : (1.0 / 9.0));
}
float worldCurvatureCavity() { return worldEnvRow(SdfEnvCurvatureA).x; }
float worldCurvatureRim() { return worldEnvRow(SdfEnvCurvatureA).y; }
float worldCurvatureInk() { return worldEnvRow(SdfEnvCurvatureA).z; }
float worldCurvatureInkLow() { return worldEnvRow(SdfEnvCurvatureA).w; }
float3 worldCurvatureInkColor() { return worldEnvRow(SdfEnvCurvatureB).rgb; }
float worldCurvatureInkHigh() { return worldEnvRow(SdfEnvCurvatureB).w; }
uint worldSkyStopCount() { return min((uint)max(worldEnvRow(SdfEnvSkyControl).x + 0.5, 0.0), 4u); }
float4 worldSkyStop(uint index) { return worldEnvRow(SdfEnvSkyStops + index); } // rgb colour, w elevation
int worldSkySunDiscLightIndex() { return (int)round(worldEnvRow(SdfEnvSkyControl).y); }
float worldSkySunDiscExponent() { return worldEnvRow(SdfEnvSkyControl).z; }
float worldSkySunDiscIntensity() { return worldEnvRow(SdfEnvSkyControl).w; }
float worldSkyStarDensity() { return worldEnvRow(SdfEnvStars).x; }
float worldSkyStarBrightness() { return worldEnvRow(SdfEnvStars).y; }
uint worldSkyStarSeed() { return (uint)(worldEnvRow(SdfEnvStars).z + 0.5); }
float worldSkyStarTwinkleShare() { return worldEnvRow(SdfEnvTwinkle).x; }
float worldSkyStarTwinkleDepth() { return worldEnvRow(SdfEnvTwinkle).y; }
uint worldSkyStarTwinklePeriodTicks() { return max((uint)(worldEnvRow(SdfEnvTwinkle).z + 0.5), 1u); }
float3 worldSkyCloudColor() { return worldEnvRow(SdfEnvCloudsA).rgb; }
float worldSkyCloudCoverage() { return worldEnvRow(SdfEnvCloudsA).w; }
float worldSkyCloudSoftness() { return worldEnvRow(SdfEnvCloudsB).x; }
float worldSkyCloudScale() { return worldEnvRow(SdfEnvCloudsB).y; }
uint worldSkyCloudSeed() { return (uint)(worldEnvRow(SdfEnvCloudsB).z + 0.5); }
float2 worldSkyCloudOffset() { return worldEnvRow(SdfEnvCloudsC).xy; }
float2 worldSkyCloudShearOffset() { return worldEnvRow(SdfEnvCloudsC).zw; }
float worldSkyCloudSpinAngle() { return worldEnvRow(SdfEnvCloudsD).x; }
float worldSkyCloudCurl() { return worldEnvRow(SdfEnvCloudsD).y; }

// render.environment: analytic studio-softbox reflections plus a two-color reflection horizon, sampled at the
// shading site as studioReflection(reflect(rayDirection, normal), roughness) — an absent section (zero softboxes,
// zero-black horizon) contributes exactly 0, so the reflection term is a no-op addition then.
struct SdfEnvSoftbox {
    float3 direction; // unit, surface -> the softbox (host-normalized on upload)
    float weight;
    float3 color;
    float2 size;       // angular half-extent proxy (width, height), world-authored radians-scale units
    float blur;
};
uint worldEnvironmentSoftboxCount() { return min((uint)max(worldEnvRow(SdfEnvSoftboxControl).x + 0.5, 0.0), SdfEnvMaxSoftboxes); }
uint worldTonemapMode() { return (uint)max(worldEnvRow(SdfEnvSoftboxControl).y + 0.5, 0.0); }
SdfEnvSoftbox worldEnvironmentSoftbox(uint index) {
    uint row = (SdfEnvSoftboxes + (index * SdfEnvRowsPerSoftbox));
    float4 a = worldEnvRow(row);
    float4 b = worldEnvRow(row + 1u);
    float4 c = worldEnvRow(row + 2u);
    SdfEnvSoftbox box;

    box.direction = a.xyz;
    box.weight = a.w;
    box.color = b.rgb;
    box.size = float2(b.w, c.x);
    box.blur = c.y;

    return box;
}
float3 worldEnvironmentHorizon(float3 direction) {
    float3 low = worldEnvRow(SdfEnvHorizonLow).rgb;
    float3 high = worldEnvRow(SdfEnvHorizonHigh).rgb;

    return lerp(low, high, saturate((direction.y * 0.5) + 0.5));
}
// The analytic studio reflection: the horizon gradient plus each authored softbox's angular falloff from `direction`,
// widened by the surface roughness (a rougher surface blurs the softbox into a broader, dimmer catch). Every softbox
// falloff is a smooth (never hard-edged) disc, so the sum stays finite and free of the reflect() singularity a mirror
// direction could otherwise expose.
float3 worldStudioReflection(float3 direction, float roughness) {
    float3 result = worldEnvironmentHorizon(direction);
    uint count = worldEnvironmentSoftboxCount();

    [loop]
    for (uint index = 0u; (index < count); index++) {
        SdfEnvSoftbox box = worldEnvironmentSoftbox(index);
        float cosAngle = saturate(dot(direction, box.direction));
        float angle = acos(cosAngle);
        float radius = max((length(box.size) + max(box.blur, roughness)), 1.0e-3);
        float falloff = saturate(1.0 - (angle / radius));

        falloff = ((falloff * falloff) * (3.0 - (2.0 * falloff))); // smoothstep shaping
        result += (box.color * (box.weight * falloff));
    }

    return result;
}
#include "shade-layers.hlsli"
// render.tonemap: the Narkowicz ACES-fit filmic curve, and ONLY the curve. The study follows it with a gamma-2.2
// encode because its shading is linear light; this pipeline's stylized shading is already display-referred (no sRGB
// encode exists anywhere between the shade and the rgba8 store), so a second encode here washes the whole frame out.
// None (the default) is a no-op — the pipeline stores its stylized color directly, as it always has.
float3 sdfFilmicTonemap(float3 color) {
    return saturate((color * ((2.51 * color) + 0.03)) / (((color * ((2.43 * color) + 0.59)) + 0.14)));
}

// The primary march's step budget. KEEP IN SYNC with SdfWorldEngine.PrimaryMarchSteps (the world.budget cost sheet
// quotes it against the authored far distance). There is deliberately NO far-distance constant beside it any more:
// the far plane is world data (render.farDistance), read per view through worldFarDistance.
static const int MaxSteps = 128;
static const float SurfaceEpsilon = 0.001;
static const float SphereTraceOmega = 1.2; // Keinert over-relaxation factor (1 = plain sphere tracing; [1, 2))
static const int ConeMarchSteps = 56;
static const int IndependentConeMarchSteps = 8;
static const float ConeNear = 0.02;
static const float ConeEpsilon = 0.002;
// Four-bound teleport (Larsson "The Gunk"): after the beam cone finds the tile's ENTRY (the classic marchStart), it
// keeps marching a bounded budget to detect ONE proven-empty gap between two occupied bands — [firstExit,
// secondEntry] — that sdf-world-views teleports the fine ray across. TileGapSteps caps the extra beam cost;
// TileGapMinStep floors the through-band advance so a near-zero cone clearance can't stall the search.
static const int TileGapSteps = 16;
static const float TileGapMinStep = 0.15;
// Abandon the gap search after this many consecutive in-band, non-increasing-clearance samples. Ground/wall cones
// often descend further into an occupied half-space; continuing both gap and tail searches there adds field walks
// without finding a useful bound. A later clear span may be missed, so this is a cost heuristic, not an emptiness
// proof. Keep the entry already established and leave gap/far bounds at the far plane; the fine ray does the work.
static const int TileGapStallLimit = 3;
// F1 FAR BOUND: after the gap search resolves, a bounded TAIL phase cone-marches from the
// resolved t to prove the far bound — the depth past which the tile's cone cannot produce any footprint-accepted hit
// through the far distance. TileFarSteps caps that extra beam cost (the tail is a latency-rich single-thread march,
// per the beam kernel's design). A descending-band stall skips this phase altogether. Otherwise ten samples may
// establish a clear-to-far span; if none is proven, the tile publishes farBound = the far distance (no early exit).
static const int TileFarSteps = 10;
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

// Shading weights of the world's one directional-sun-plus-hemisphere model. The ambient base, its hemisphere
// gradient, the sun weight and the fog density are environment lanes (SdfEnvironment) so a world can author them;
// their pinned values live on as SdfEnvironment.Default. The fog density's pinned value lives on as
// SdfEnvironment.DefaultFogDensity.
// The procedural test-card face (an unbound screen): its own emitter, tinted faintly by the sun.
static const float ScreenCardBase = 0.85;
static const float ScreenCardSunTint = 0.15;
// Keeps a screen light's inverse-square attenuation finite for a surface point on the emitter's own face.
static const float ScreenLightMinDistanceSquared = 1.0e-4;
// The 8-bit dither quantum: +-0.5 LSB of R2 noise before the store (see sdfR2Dither).
static const float DitherQuantum = (1.0 / 255.0);
// debug.view.evals calibration: the ramp saturates at this many tallied field evaluations. Worst case for a single
// lit pixel is bounded by MaxSteps (128, primary march) + ShadowSteps (40, the soft-shadow march) + 3 (calcAO) + 4
// (the 4-tap normal fallback, worse than the 1-eval analytic default) + 1 (the coverage-AA probe) ~= 176, so 256
// leaves margin
// before saturating solid red — chosen so a typical unshadowed ambient-only hit (~30-40 evals: a short march plus
// the analytic normal and AO) reads green/yellow rather than washing out at the floor.
static const float EvalHeatmapCeiling = 256.0;
// The soft-shadow march toward the shadow light: a closest-approach penumbra estimate — the running minimum of
// k · d / t, where d is the nearest approach of the field's clearance spheres to the ray between consecutive samples
// and t the distance travelled — marched by the field's own clearance under a distance-proportional step ceiling that
// keeps the samples dense enough for the estimate to converge. Deterministic: one ray per lit pixel, no per-frame
// sample, no history. k is the reciprocal of the shadow light's authored penumbra half-slope
// (worldShadowPenumbraSlope), so the visibility ramps across an angular band of that slope about an occluder's edge.
static const int ShadowSteps = 64;
static const int FastShadowSteps = 12;
// Half the RT path's 24-unit reach: this compute march has no TLAS to fast-forward to the occluder, so every unit of
// reach is marched per lit pixel. Contact/self shadows (the visual win) are near; 12 covers every realistic case while
// halving the worst-case empty-space step count on dense scenes.
static const float ShadowMaxDistance = 9.0;
static const float ShadowBias = 0.02;
// A sample within this travel of the origin reads the origin surface itself — a ray skimming its own curved surface
// at grazing incidence — and is skipped. Contact occluders closer than this are not resolved.
static const float ShadowEstimateStart = 0.12;
static const float ShadowStepMin = 0.02;      // an occluder thinner than this can be stepped through
static const float ShadowStepNear = 0.08;     // the step ceiling's floor, world units
static const float ShadowStepFarSlope = 0.05; // the ceiling grows with distance: max(ShadowStepNear, slope * t)
// Fleet-scale presentation path: shorter reach and budget, a wider stride through open space. It can step through
// occluders thinner than its stride, the same trade the near-field ShadowStepMin floor already makes.
static const float FastShadowStepMax = 1.8;
static const float FastShadowStepFarSlope = 0.45;
static const float FastShadowMaxDistance = 5.0;
// The shadow-cull gather's cone (see sdfShadowGather): the half-slope of the cone whose occluders the gather must
// contain for the shadow march to be sound. The march's samples read the field within the penumbra band about the
// ray, so every occluder that can lower the estimate lies inside three penumbra half-slopes with margin; a wider cone
// is always a superset, only less selective. SdfEnvironment.MaxPenumbraSlope keeps the chord below one.
float worldShadowPenumbraChord() { return (3.0 * worldShadowPenumbraSlope()); }
// The gradient probe's finite-difference offset. Small enough that the tetrahedron's O(eps) curvature error is
// sub-LSB, large enough to stay clear of the field's own float noise.
static const float NormalProbeEpsilon = 0.0006;
// GRADIENT-SCALED PENUMBRA/AO (secondary-ray posture, src/Puck.World/Assets/studies/moth.glsl's surfaceGradient/shadow/ambientOcclusion).
// mapCore/mapGradCore's per-program stepScale (sdfStepScale) is a single GLOBAL, WORST-CASE Lipschitz bound for the
// whole program/scope — it keeps the march SOUND but says nothing about how far a given shape's own formula departs
// from a unit SDF AT THE HIT (an approximate Ellipsoid's directional gradient, AxialProfile's y-varying shear, the study's
// own hand-authored `d*.7`-style scalar distance multiplies). The RAW gradient mapGradMasked returns (before its
// consumer normalizes) already carries that local departure — it is the gradient of the same shape-formula distance
// mapCore returns before ITS OWN final stepScale multiply (sdf-vm.hlsli: "result.distance *= stepScale;" is NOT
// mirrored onto `gradient`). Its magnitude is therefore a SEPARATE, per-hit correction from stepScale, and the two
// compose multiplicatively into one effective de-scale factor (see shadingStepScale at the softShadowVisibility/
// calcAO call sites below) — never folded into stepScale itself, which must stay the program's own march-soundness
// bound. GradientMagnitudeFloor keeps a near-degenerate local gradient (a cusp, a blend seam) from blowing the
// estimate up; it mirrors the reference study's own clamp lower bound (src/Puck.World/Assets/studies/moth.glsl, clamp(magnitude,.12,1.5)).
static const float GradientMagnitudeFloor = 0.12;

// Per-pixel query tally for debug.view.evals, including primary local-part marches and shading probes.
// Call sites here and in sdf-primary.hlsli count their queries; the interpreter does not. This per-thread
// scalar follows the material-seam channel's pattern and resets at renderView entry. Counting stays active
// for every view so selecting the evaluation heatmap does not change the work being measured.
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
// gradientMagnitude (out): the secondary-ray gradient-scaling posture (src/Puck.World/Assets/studies/moth.glsl's surfaceGradient) — the
// tetrahedron sum's own magnitude divided by 4e recovers the RAW field's local gradient magnitude at the hit
// (BEFORE this normalize), still carrying the taps' own mapDistanceMasked stepScale bake, so it is divided back out
// by sdfStepScale() to land in the SAME program-stepScale-independent units calculateNormalAnalytic reports (see
// GradientMagnitudeFloor above). The normal direction itself is unaffected — this is a second, additive return.
float3 calculateNormal(float3 p, uint instanceMaskBase, out float gradientMagnitude) {
    const float2 k = float2(1.0, -1.0);
    const float e = NormalProbeEpsilon;

    sdfEvalCount += 4.0; // four mapDistanceMasked taps below

    float3 sum =
        (k.xyy * mapDistanceMasked(p + (k.xyy * e), instanceMaskBase)) +
        (k.yyx * mapDistanceMasked(p + (k.yyx * e), instanceMaskBase)) +
        (k.yxy * mapDistanceMasked(p + (k.yxy * e), instanceMaskBase)) +
        (k.xxx * mapDistanceMasked(p + (k.xxx * e), instanceMaskBase));

    gradientMagnitude = ((length(sum) / (4.0 * e)) / sdfStepScale());

    return normalize(sum);
}
// The tetrahedron's four distances minus four times the center recover 2*e^2 times the field Laplacian.
// De-scale it to world units: concave creases read negative, convex ridges positive. The primary hit supplies
// the center unless Detail shapes can change the shading field; those programs query the current field again.
float3 calculateNormalCurvature(float3 p, uint instanceMaskBase, float primaryCenter, out float curvature, out float gradientMagnitude) {
    const float e = NormalProbeEpsilon;
    sdfEvalCount += 4.0;
    float3 sum = 0.0;
    float total = 0.0;
    // Keep one interpreter call site: unrolling duplicates the large VM body and slows the views kernel.
    [loop]
    for (uint probe = 0u; probe < 4u; probe++) {
        float3 direction = float3((probe == 0u || probe == 3u) ? 1.0 : -1.0,
            probe >= 2u ? 1.0 : -1.0, (probe & 1u) != 0u ? 1.0 : -1.0);
        float distance = mapDistanceMasked(p + (direction * e), instanceMaskBase);
        sum += direction * distance;
        total += distance;
    }
    float center = primaryCenter;
    if (!sdfProgramLayout.noDetailShapes) {
        center = mapDistanceMasked(p, instanceMaskBase);
        sdfEvalCount += 1.0;
    }
    float stepScale = sdfStepScale();
    curvature = ((total - (4.0 * center)) / ((2.0 * e * e) * stepScale));
    gradientMagnitude = ((length(sum) / (4.0 * e)) / stepScale);

    return normalize(sum);
}
// The ANALYTIC surface normal (forward-mode gradient dual): ONE dual field eval at the hit — replacing the four taps —
// carries the exact world-space field gradient through the transform chain (sdf-vm.hlsli's mapGradMasked). Immune to
// the finite-difference catastrophic cancellation the taps suffer near a warp/fold/displace, and more cross-backend-
// stable near these discontinuities. mapGradMasked returns the UN-normalized gradient; the stepScale the scalar
// distance still carries is a uniform positive factor that cancels under this normalize, so the dual never applies it.
// Same tile instance mask as the primary march, so the analytic normal sees the identical masked field the hit did.
// gradientMagnitude (out): mapGradMasked's `gradient` is ALREADY the RAW, program-stepScale-EXCLUDED field gradient
// (sdf-vm.hlsli's mapGradCore multiplies only `result.distance` by stepScale, never `gradient` — see the
// GradientMagnitudeFloor remarks above) — its length is this function's local gradient magnitude for free, no extra
// field evaluation.
float3 calculateNormalAnalytic(float3 p, uint instanceMaskBase, out float gradientMagnitude) {
    float3 gradient;

    sdfEvalCount += 1.0; // one dual field eval replaces the four taps

    mapGradMasked(p, instanceMaskBase, gradient);

    gradientMagnitude = length(gradient);

    return sdfSafeNormalize(gradient);
}
// Procedural placeholder for a SCREEN_SLAB face: an animated test-card.
float3 screenContent(float3 p, float time) {
    float bars = (0.5 + (0.5 * sin((p.y * 26.0) - (time * 5.0))));
    float3 baseColor = lerp(float3(0.02, 0.04, 0.09), float3(0.10, 0.80, 1.00), bars);
    float sweep = smoothstep(0.49, 0.5, frac((p.x * 1.3) + (time * 0.4)));

    return (baseColor + (0.35 * float3(0.95, 0.45, 0.12) * sweep));
}
// Octahedral encoding of a unit direction into [-1, 1]^2 (Meyer et al., "On Floating-Point Normal Vectors") — the
// star field's cell-grid domain. No texture, no per-pixel trig; area-preserving enough for a uniform-reading star
// density across the sky.
float2 sdfOctEncode(float3 n) {
    float2 p = (n.xy * (1.0 / ((abs(n.x) + abs(n.y)) + abs(n.z))));

    if (n.z < 0.0) {
        p = ((1.0 - abs(p.yx)) * float2(((p.x >= 0.0) ? 1.0 : -1.0), ((p.y >= 0.0) ? 1.0 : -1.0)));
    }

    return p;
}
// The inverse of sdfOctEncode: a [-1, 1]^2 octahedral point back to a unit direction.
float3 sdfOctDecode(float2 p) {
    float3 n = float3(p.x, p.y, (1.0 - (abs(p.x) + abs(p.y))));

    if (n.z < 0.0) {
        n.xy = ((1.0 - abs(n.yx)) * float2(((n.x >= 0.0) ? 1.0 : -1.0), ((n.y >= 0.0) ? 1.0 : -1.0)));
    }

    return normalize(n);
}
// The procedural star field: a per-cell PCG3D hash (seed folded in) over the octahedral sky projection picks
// StarSparsity of the cells to carry a star; two hash channels place the star inside its cell (kept StarInset from
// the walls so a disc never straddles a cell it is not tested in). A second hash of the first, paid only by the
// cells that carry a star, gives each its own apparent luminosity and colour: luminosity follows the count law of
// sources spread uniformly through space (N(>F) ∝ F^-3/2, so F = floor·u^-2/3, capped at the authored peak — most
// stars faint, a few bright, as the real sky reads), the disc growing mildly with it; colour is a blackbody tint
// picked log-uniformly in temperature from ~3000 K (orange) through ~6500 K (white) to ~15000 K (blue-white), each
// tint normalized to a unit peak channel so it colours the star without changing the luminosity law. Twinkling is
// optional: a hash-chosen share of the stars dip by the authored depth and recover, each riding its own small
// harmonic and phase of the authored period on the deterministic tick counter (reduced by an integer modulo first,
// so the phase is exact however long the session runs, and a replay at tick N twinkles identically). The disc is
// measured ANGULARLY — the star's cell point is decoded back to a direction and the pixel's angle to it compared
// against StarRadiusFraction of one cell's angular pitch (≈ π/density) — so a star is round everywhere on the sky
// rather than stretched by the projection's anisotropy. No texture, no session state — the identical (direction,
// seed) always draws the identical field.
static const float StarSparsity = 0.08;         // fraction of cells that carry a star
static const float StarInset = 0.3;             // star center's minimum distance from its cell walls, in cells
static const float StarRadiusFraction = 0.12;   // star angular radius as a fraction of one cell's angular pitch, at peak luminosity
static const float StarLuminosityFloor = 0.125; // the faintest star's luminosity as a fraction of the peak (~2.3 magnitudes)
static const float3 StarSpectrum[7] = {         // blackbody tints, unit peak channel: 3000, 4000, 5000, 6500, 8000, 10000, 15000 K
    float3(1.00, 0.71, 0.42),
    float3(1.00, 0.82, 0.64),
    float3(1.00, 0.89, 0.81),
    float3(1.00, 0.98, 0.99),
    float3(0.89, 0.91, 1.00),
    float3(0.79, 0.85, 1.00),
    float3(0.71, 0.80, 1.00)
};
float3 sdfStarField(float3 direction, float density, float brightness, uint seed, float twinkleShare, float twinkleDepth, uint twinklePeriodTicks, uint tick) {
    density = max(density, 1.0);

    float2 cellF = (((sdfOctEncode(direction) * 0.5) + 0.5) * density);
    float2 cellId = floor(cellF);
    uint3 h = sdfPcg3d(uint3(asuint(cellId.x), asuint(cellId.y), seed));
    float existence = ((float)h.x * SDF_INV_2POW32);

    if (existence > StarSparsity) {
        return float3(0.0, 0.0, 0.0);
    }

    uint3 h2 = sdfPcg3d(h);
    float luminosity = min(1.0, (StarLuminosityFloor * pow(max(((float)h2.x * SDF_INV_2POW32), 1e-6), -0.6666667)));
    float spectrum = (((float)h2.y * SDF_INV_2POW32) * 6.0);
    uint spectrumIndex = min((uint)spectrum, 5u);
    float3 tint = lerp(StarSpectrum[spectrumIndex], StarSpectrum[(spectrumIndex + 1u)], (spectrum - (float)spectrumIndex));

    if (((float)h2.z * SDF_INV_2POW32) < twinkleShare) {
        // Two sines at distinct small harmonics of the period, phase-offset per star, multiplied: an irregular dip
        // pattern that still closes exactly at the period boundary, so the integer modulo never shows a seam.
        uint3 h3 = sdfPcg3d(h2);
        float phase = ((float)(tick % twinklePeriodTicks) / (float)twinklePeriodTicks);
        float harmonicA = (float)(1u + (h3.x % 3u));
        float harmonicB = (float)(2u + (h3.y % 3u));
        float offset = ((float)h3.z * SDF_INV_2POW32);
        float flicker = (0.5 + (0.5 * (sin(6.28318531 * ((harmonicA * phase) + offset)) * sin(6.28318531 * ((harmonicB * phase) + (offset * 1.7))))));

        luminosity *= (1.0 - (twinkleDepth * flicker));
    }
    float2 starUv = lerp(StarInset.xx, (1.0 - StarInset).xx, float2(((float)h.y * SDF_INV_2POW32), ((float)h.z * SDF_INV_2POW32)));
    float3 starDirection = sdfOctDecode((((cellId + starUv) / density) * 2.0) - 1.0);
    // 1 - cos(angle) ≈ angle²/2 for the small angles a star subtends: compare against the radius squared over two, no acos.
    float radius = (((StarRadiusFraction * 3.14159265) / density) * lerp(0.6, 1.0, sqrt(luminosity)));
    float separation = (1.0 - dot(direction, starDirection));
    float coverage = smoothstep((0.5 * (radius * radius)), 0.0, separation);

    return (((coverage * brightness) * luminosity) * tint);
}
// Value noise on the integer lattice: one sdfPcg3d per corner (seed folded in), quintic-smoothed bilinear blend.
// The cell coordinates are hashed by their float bit patterns, so negative cells are as distinct as positive ones.
float sdfLatticeNoise(float2 p, uint seed) {
    float2 cell = floor(p);
    float2 f = (p - cell);
    float2 u = ((f * f * f) * ((f * ((f * 6.0) - 15.0)) + 10.0));
    float a = ((float)sdfPcg3d(uint3(asuint(cell.x), asuint(cell.y), seed)).x * SDF_INV_2POW32);
    float b = ((float)sdfPcg3d(uint3(asuint(cell.x + 1.0), asuint(cell.y), seed)).x * SDF_INV_2POW32);
    float c = ((float)sdfPcg3d(uint3(asuint(cell.x), asuint(cell.y + 1.0), seed)).x * SDF_INV_2POW32);
    float d = ((float)sdfPcg3d(uint3(asuint(cell.x + 1.0), asuint(cell.y + 1.0), seed)).x * SDF_INV_2POW32);

    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}
// Four octaves of lattice noise (lacunarity 2, gain ½), each octave on its own seed, normalized to [0, 1].
float sdfCloudFbm(float2 p, uint seed) {
    float value = 0.0;
    float amplitude = 0.5;

    [unroll]
    for (uint octave = 0u; (octave < 4u); octave++) {
        value += (amplitude * sdfLatticeNoise(p, (seed + octave)));
        p = ((p * 2.0) + 17.0);
        amplitude *= 0.5;
    }

    return (value / 0.9375);
}
// The procedural cloud layer: a heightfield of cloud on a DOME — a spherical shell of radius CloudDomeRadius + 1
// about a centre CloudDomeRadius below the camera, unit height overhead. A direction's layer point is where its ray
// meets that shell (t = -R·d.y + sqrt(R²·d.y² + 2R + 1): 1 overhead, sqrt(2R + 1) at the horizon), so the layer
// compresses smoothly toward the horizon as a real one does and no direction ever needs a clamp — the vertical
// "curtain" smear a clamped plane projection painted under every horizon cloud is gone with it. The wind acts on
// that point before the noise reads it: the layer turns about the zenith by the host-integrated spin angle, is wound
// by the Coriolis curl (an extra rotation of curl · 2r/(1+r²) — zero at the zenith, peaking at 45° elevation, fading
// to the horizon — so bands spiral inward the way a rotating frame bends a broad flow, without the unbounded shear a
// rigid differential rotation would tear the field into), is scaled by the authored cell size and slid by the
// host-integrated drift. The THICKNESS at a point is a domain-warped fbm (a first fbm bends the second's domain by
// CloudWarp — the puffed, lobed silhouettes flat noise never gives), the shaping fbm read at its own host-integrated
// shear offset so the two fields slide past each other and the clouds boil and re-form as they travel, thresholded
// at (1 - coverage) over the authored softness. Volume is READ FROM THAT HEIGHTFIELD, four thickness taps per pixel:
// the centre tap and two offset taps give the field's gradient, hence a surface normal (CloudHeight tall per unit
// thickness) that lights against the lighting sun — sunward flanks bright, lee flanks dark; a fourth tap toward the
// sun finds a taller neighbour shadowing this point (CloudSelfShadow); the sun seen THROUGH a thin edge lines it in
// the sun's colour (CloudSilverLining); and the thickness sets the opacity through Beer's law (1 - exp(-t·CloudOpacity))
// so a core is solid and a fringe is a wisp. The layer fades to nothing in the last CloudHorizonFade of elevation
// (where its cells shrink past a pixel) and is never drawn below the horizon. Deterministic: (direction, settings,
// seed, host-integrated wind, sun) alone.
static const float CloudDomeRadius = 6.0;      // dome centre depth below the camera, in layer units (unit height overhead)
static const float CloudWarp = 0.6;            // how far the first fbm bends the second's domain, in cells
static const float CloudHeight = 0.7;          // the heightfield's rise per unit thickness, in cells — the normal's steepness
static const float CloudNormalTap = 0.18;      // the gradient taps' offset from the centre, in cells
static const float CloudSelfShadow = 0.6;      // how dark a point goes under a taller sunward neighbour
static const float CloudSilverLining = 0.5;    // the sun-through-a-thin-edge highlight's strength
static const float CloudOpacity = 3.5;         // Beer's-law extinction per unit thickness
static const float CloudHorizonFade = 0.05;    // direction.y below which the layer fades to nothing
float sdfCloudThickness(float2 p, float2 shearOffset, uint seed, float threshold, float softness) {
    float warp = sdfCloudFbm((p + shearOffset), (seed ^ 0x9E3779B9u));
    float density = sdfCloudFbm((p + (CloudWarp * (warp - 0.5))), seed);

    return smoothstep(threshold, (threshold + softness), density);
}
float4 sdfCloudLayer(float3 direction, float3 color, float coverage, float softness, float scale, uint seed, float2 offset, float2 shearOffset, float spinAngle, float curl) {
    if ((coverage <= 0.0) || (direction.y <= 0.0)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float b = (CloudDomeRadius * direction.y);
    float t = (sqrt((b * b) + ((2.0 * CloudDomeRadius) + 1.0)) - b);
    float2 layer = (direction.xz * t);
    float radius = length(layer);
    float angle = (spinAngle + (curl * ((2.0 * radius) / (1.0 + (radius * radius)))));
    float sinAngle;
    float cosAngle;

    sincos(angle, sinAngle, cosAngle);

    float2 turned = float2(((layer.x * cosAngle) - (layer.y * sinAngle)), ((layer.x * sinAngle) + (layer.y * cosAngle)));
    float2 p = ((turned / max(scale, 1e-3)) + offset);
    float threshold = (1.0 - coverage);
    float thickness = sdfCloudThickness(p, shearOffset, seed, threshold, softness);

    if (thickness <= 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    // The sun in the layer's turned frame (the same rotation the layer point took), so the lighting follows the wind.
    float3 sun = worldSunDirection();
    float2 sunTurned = float2(((sun.x * cosAngle) - (sun.z * sinAngle)), ((sun.x * sinAngle) + (sun.z * cosAngle)));
    float thicknessX = sdfCloudThickness((p + float2(CloudNormalTap, 0.0)), shearOffset, seed, threshold, softness);
    float thicknessY = sdfCloudThickness((p + float2(0.0, CloudNormalTap)), shearOffset, seed, threshold, softness);
    float thicknessSunward = sdfCloudThickness((p + (CloudNormalTap * 2.0 * normalize(sunTurned + 1e-5))), shearOffset, seed, threshold, softness);
    float3 normal = normalize(float3(-((thicknessX - thickness) / CloudNormalTap) * CloudHeight, 1.0, -((thicknessY - thickness) / CloudNormalTap) * CloudHeight));
    float diffuse = saturate(dot(normal, normalize(float3(sunTurned.x, sun.y, sunTurned.y))));
    float shadow = (1.0 - (CloudSelfShadow * saturate(thicknessSunward - thickness)));
    float lining = ((CloudSilverLining * pow(saturate(dot(direction, sun)), 8.0)) * (1.0 - thickness));
    float3 shade = ((color * (lerp(0.45, 1.0, diffuse) * shadow)) + (worldSunColor() * lining));
    float alpha = ((1.0 - exp(-(thickness * CloudOpacity))) * smoothstep(0.0, CloudHorizonFade, direction.y));

    return float4(shade, alpha);
}
// The sky's GRADIENT alone — what distance fog and the silhouette-edge blend fade toward. The sun disc, stars and
// clouds are miss-pixel content (skyColor); folding them into fog would let a low sun bleed through a fogged floor.
float3 skyGradient(float3 direction) {
    if (!worldSkyEnabled()) {
        // The pinned two-stop gradient, UNCHANGED from before render.sky existed: the identical instructions in the
        // identical order, so a world that never authors render.sky renders bit-identically.
        float t = clamp((0.5 * (direction.y + 1.0)), 0.0, 1.0);

        return lerp(float3(0.04, 0.05, 0.07), float3(0.10, 0.13, 0.20), t);
    }

    // The authored stops, ascending in elevation (the validator orders them): piecewise-linear in direction.y,
    // clamped to the end stops beyond the first and last.
    uint stops = worldSkyStopCount();
    float elevation = direction.y;
    float4 previous = worldSkyStop(0u);

    if ((stops <= 1u) || (elevation <= previous.w)) {
        return previous.rgb;
    }

    [loop]
    for (uint index = 1u; (index < stops); index++) {
        float4 next = worldSkyStop(index);

        if (elevation <= next.w) {
            float t = saturate((elevation - previous.w) / max((next.w - previous.w), 1.0e-5));

            return lerp(previous.rgb, next.rgb, t);
        }

        previous = next;
    }

    return previous.rgb;
}
float3 skyColor(float3 direction) {
    float3 color = skyGradient(direction);

    if (!worldSkyEnabled()) {
        return color;
    }

    // The sun disc: an additive pow(cosAngle, k) highlight about its light's direction. k is HOST-BAKED from the
    // authored angular radius (SdfWorldEngine.PackEnvironment) so this pays one pow() rather than deriving the
    // exponent from an angle per pixel.
    int discLight = worldSkySunDiscLightIndex();

    if (discLight >= 0) {
        float cosAngle = dot(direction, worldLight((uint)discLight).direction);

        color += (worldSkySunDiscIntensity() * pow(saturate(cosAngle), worldSkySunDiscExponent())).xxx;
    }

    // Stars read only above the local horizon — a night sky under the ground plane is never visible to the camera
    // and would otherwise tile through geometry for nothing.
    if (direction.y > 0.0) {
        color += sdfStarField(direction, worldSkyStarDensity(), worldSkyStarBrightness(), worldSkyStarSeed(), worldSkyStarTwinkleShare(), worldSkyStarTwinkleDepth(), worldSkyStarTwinklePeriodTicks(), params.sampleIndex);
    }

    // Clouds sit over everything above them — the gradient, the sun disc and the stars — by their own coverage mask.
    float4 clouds = sdfCloudLayer(direction, worldSkyCloudColor(), worldSkyCloudCoverage(), worldSkyCloudSoftness(), worldSkyCloudScale(), worldSkyCloudSeed(), worldSkyCloudOffset(), worldSkyCloudShearOffset(), worldSkyCloudSpinAngle(), worldSkyCloudCurl());

    return lerp(color, clouds.rgb, clouds.a);
}
// A distinct, stable hue per material id (an HSV hue ramp), not the table albedo — so id boundaries read clearly
// in the material-id debug view.
float3 materialPalette(int material) {
    float hue = frac(float(material) * 0.61803399);
    float3 ramp = (abs((frac(hue + float3(0.0, 0.33333333, 0.66666667)) * 6.0) - 3.0) - 1.0);

    return saturate(ramp);
}

// The perspective ray for a viewport-local UV (pixel centers in [0,1] within the viewport's region; screen-up maps
// to the camera's +up). SYMMETRIC by construction: `direction`'s defining expression below is untouched from before
// the off-axis branch existed, so a camera that never sets renderScale.yz (every camera but a border window) takes
// the IDENTICAL sum in the IDENTICAL order — bit-exact, not merely numerically equal, which is what a build with the
// branch not taken needs to prove byte-identity against a build without it at all.
float3 cameraRayDirection(ViewportData view, float2 localUv) {
    float2 ndc = ((localUv * 2.0) - 1.0);

    ndc.y = -ndc.y;

    float tanHalfFov = view.right.w;
    float aspect = view.up.w;

    float3 direction = (
        view.forward.xyz +
        (((ndc.x * aspect) * tanHalfFov) * view.right.xyz) +
        ((ndc.y * tanHalfFov) * view.up.xyz)
    );

    // Off-axis (asymmetric) frustum shear for a border window (SdfAsymmetricFrustum, Puck.SdfVm.Views): the render-
    // scale row's two always-zero spares carry the frustum's tangent-space center offset, appended as a TRAILING
    // term so the symmetric sum above is never reassociated (float addition is not associative — computing the
    // offset into a fresh accumulator first, then adding, can round differently than one flat left-to-right sum).
    if ((view.renderScale.y != 0.0) || (view.renderScale.z != 0.0)) {
        direction += ((view.renderScale.y * view.right.xyz) + (view.renderScale.z * view.up.xyz));
    }

    return normalize(direction);
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
// or TileEmpty when the cone clears the field out to the far distance), so plane 0 — and therefore the cull-args bbox,
// the compositor's empty-tile test, and the footprint-adaptive termination the ground-notch rides — is the exact
// classic beam output. `firstExit`/`secondEntry` bound ONE proven-empty gap the cone cleared between two occupied
// bands; when no gap is proven, firstExit = the far distance so the consumer's teleport is a total no-op (a total
// function, per the determinism pin). secondEntry >= firstExit always. `farBound` (F1, plane 3) is the depth past
// which the tile's cone cannot produce ANY footprint-accepted hit through the far distance (proven against the
// FOOTPRINT-INFLATED threshold — see coneMarchFarBound); the far distance when the tail phase proved no such bound (the
// consumer's far-exit is then a no-op). Every "nothing proven" sentinel is the view's worldFarDistance, never a constant.
struct TileBounds {
    float entry;
    float firstExit;
    float secondEntry;
    float farBound;
};

// F1 TAIL PHASE. Proves the FAR BOUND: the depth past which the tile's cone cannot produce any
// hit the fine march would ACCEPT, all the way to the far distance. Marches forward from `startT` with a FOOTPRINT-INFLATED
// clearance threshold — the load-bearing correctness fact. The fine march accepts a hit at fieldDistance <
// max(SurfaceEpsilon, footprint*t) (sdf-world-views computes footprint = 2*right.w/rectDims.y; the beam computes the
// identical value from regionSizePx), and footprint*t ~ 0.001*t exceeds ConeEpsilon past t~2 — so a bare ConeEpsilon
// proof is ANTI-conservative and could bound above a real footprint hit. Inflating the cone's transverse radius by the
// pixel footprint (spread = chord + footprint) and requiring clearance = min(map(center), sdfMapStepBound) -
// spread*t > SurfaceEpsilon (stepping by clearance/(1 + spread), the 1-Lipschitz cone guarantee for the inflated cone)
// guarantees that for every ray and every t' in [farBound, farDistance] the hit-accept fieldDistance <
// max(SurfaceEpsilon, footprint*t') can NEVER fire — so the ray renders skyColor whether it exits at farBound or marches
// on, i.e. the far exit is OUTPUT-IDENTICAL on the shipped shading path (only step counts and the termination debug view
// change). The same rule is what renderView's exhaustion arm accepts a closest-approach candidate against, so the proof
// covers that arm too. FOLD-SAFE like the gap phases (the bounded clearance rides sdfMapStepBound). Total function: no
// proven clear-to-far span within the budget => the far distance.
float coneMarchFarBound(ViewportData view, TileCone cone, uint instanceMaskBase, float footprint, float startT) {
    float3 origin = view.position.xyz;
    float farDistance = worldFarDistance(view);
    float spread = (cone.chord + footprint); // the cone half-spread inflated by the pixel footprint
    float t = startT;
    float clearStart = farDistance; // start of the CURRENT footprint-clear span (farDistance = not in one)
    bool clear = false;

    [loop]
    for (int i = 0; (i < TileFarSteps); i++) {
        if (t > farDistance) {
            // Reached the far plane. If we are inside a footprint-clear span, it extends to the far distance => the
            // far bound is that span's start; otherwise no bound was proven.
            return (clear ? clearStart : farDistance);
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
            clearStart = farDistance;
            t += (max(-clearance, TileGapMinStep) / (1.0 + spread));
        }
    }

    return farDistance; // budget exhausted without proving a clear-to-far span => no far bound
}

// Cone march that additionally records the first proven-empty gap past the entry band. The GAP is
// conservative for the WHOLE tile cone: `firstExit` is a t at which the cone clearance is strictly positive (every
// ray in the tile is clear there) and the search then steps by <= clearance/(1+chord) — the sphere-trace guarantee —
// so it cannot skip the cone re-entering geometry; the first re-entry is `secondEntry`. Overstepping the interior of
// the first band (the through-band phase) can only MISS a gap (reporting firstExit = the far distance), never invent
// one, so a teleport is never unsafe. Reaching the far distance while clear yields secondEntry = the far distance (an
// empty tail — the ray teleports to the far plane and ends), the one far-bound benefit taken here.
//
// The tile's instance mask excludes only bounds with no influence on its cone; world segments always evaluate.
// Independently traced parts use a short entry search: the full-scene gap/tail searches cost more than the
// cheaper local marches save. Exhaustion leaves a proven-clear start, never an empty tile or invented far bound.
// Other root compositions retain the full entry/gap/tail search below.
TileBounds coneMarchTileBounds(ViewportData view, TileCone cone, uint instanceMaskBase, float footprint) {
    float3 origin = view.position.xyz;
    float farDistance = worldFarDistance(view);
    bool entryOnly = sdfCanTracePartsIndependently();

    TileBounds b;
    b.entry = TileEmpty;
    b.firstExit = farDistance;   // no proven gap => teleport disabled (total function)
    b.secondEntry = farDistance;
    b.farBound = farDistance;    // F1: no proven far bound yet => the consumer's far-exit is a no-op (total function)

    // ENTRY: the Lipschitz-clamped field clears the cone by map(center) - chord*t.
    // Advancing by clearance/(1+chord) stays conservative, including when the entry budget ends early.
    float t = ConeNear;
    bool foundEntry = false;
    int entrySteps = entryOnly ? IndependentConeMarchSteps : ConeMarchSteps;

    [loop]
    for (int i = 0; (i < entrySteps); i++) {
        // FOLD-SAFE: the clearance proof rides min(value, sdfMapStepBound). A folded field's raw value can
        // overestimate near a fold boundary, and a cone proof built on it classifies tiles straight through shell
        // geometry (the Droste tile-shatter). min with the published boundary gap
        // is an honest unbounding sphere of the TRUE field, so entry, TileEmpty, and the gap proofs stay sound; a
        // fold-free program publishes SDF_STEP_BOUND_NONE and this min is the identity.
        float clearance = (min(mapDistanceMasked(origin + (cone.centerDirection * t), instanceMaskBase), sdfMapStepBound) - (cone.chord * t));

        if (clearance <= ConeEpsilon) {
            b.entry = t;
            foundEntry = true;
            break;
        }

        t += (clearance / (1.0 + cone.chord));

        if (t > farDistance) {
            return b; // TileEmpty entry, no gap — cull-args drops the tile
        }
    }

    if (entryOnly) {
        if (!foundEntry) b.entry = t;
        return b; // Gap and far-bound sentinels leave all remaining work to primary rays.
    }
    if (!foundEntry) {
        b.entry = t; // step budget exhausted at the entry band — matches coneMarchTile's fallthrough `return t`
        // F1 leak #2: a budget-exhausted grazing tile is marked LIVE (all its pixels fine-march from t). Prove the far
        // bound from here so sky pixels that clear the near band exit early instead of running to the far distance.
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
                    b.firstExit = farDistance;
                    b.secondEntry = farDistance;
                    // Stop the whole search after the descending-band stall. No gap or tail has been proven, so
                    // keep the initialized far bound at farDistance; never infer empty space from the stall itself.

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
                // band so a ray that clears it exits early instead of marching the second band's sky to the far distance.
                b.farBound = coneMarchFarBound(view, cone, instanceMaskBase, footprint, t);

                return b; // one gap is the 90% win (Larsson clamps to one re-entry)
            }

            t += (clearance / (1.0 + cone.chord)); // stay conservative across the clear span
        }

        if (t > farDistance) {
            if (clear) {
                b.secondEntry = farDistance;   // proven clear to the far plane — teleport ends the ray (the far bound)
            }
            else {
                b.firstExit = farDistance;     // never cleanly exited the band — disable the teleport
            }

            return b;
        }
    }

    // Budget exhausted without a clean second entry: we cannot prove the span past firstExit stays empty, so DISABLE
    // the teleport (stay conservative — never teleport past unproven space).
    b.firstExit = farDistance;
    b.secondEntry = farDistance;
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
// Mode 10 (eval-count heatmap — tallies primary-march evaluation cost per pixel by eye). UNLIKE every other debug
// mode, this one needs the REAL final-shading epilogue to run (normal, soft
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
// The F1 FAR-BOUND A/B lever. Rides SdfFarFieldParams.x: 0 (the DEFAULT, an unset frame) keeps
// the beam-published far bound ACTIVE (the shipped behavior — the fine march exits at traveled >= farBound); 1 pushes
// the far bound out of reach so the march runs to the far distance exactly as pre-F1 (the paired-run "off" side). Decoded
// only under SDF_SCREEN_SOURCES (the world-views kernel is the sole SDF-hit shader). KEEP IN SYNC with
// SdfFrame.DisableFarBound and SdfWorldEngine.PackScreenLights.
bool worldFarBoundDisabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfFarFieldParams].x > 0.5);
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
// reference the departed cull gate matched, and the A/B lever's slow reference). KEEP IN SYNC with SdfFrame.DisableShadowCull
// and SdfWorldEngine.PackScreenLights.
bool worldShadowCullEnabled() {
    return (sdfScreenLights[SdfGridObjParams].w < 0.5);
}

#ifdef SDF_GROUP_SHADOW_GATHER
// Build the shadow-ray candidate mask into sdfShadowMaskWords for the soft-shadow marches of ONE 8x8 WORKGROUP — the
// per-tile gather (2026-09-03) that replaced the per-lit-pixel gather. Every lane publishes its hit hitPoint (or none),
// lane 0 reduces the group's lit points to a apex and the radius R that encloses them, and the 64 lanes then walk
// the instance grid COOPERATIVELY along the penumbra cone apexed at the apex, testing every bound INFLATED by R
// (+ ShadowBias, the march origin's normal offset). SUPERSET-PRESERVING for every pixel in the group: a pixel's own
// gather cone is the apex cone translated by at most R, so an instance meeting the pixel cone lies within R of
// the apex cone and the inflated test admits it; and an admitted instance the pixel's ray never reaches composes
// as the accumulator to the bit (the bound-sizing contract mapMasked already rides), so the masked march of every
// lane equals the flat map() soft shadow TO THE BIT — the same argument the per-pixel gather made, widened by R. What
// changed is cost: one grid walk per 64 pixels instead of 64 divergent walks, the walk spread across the lanes, and
// the 32-word mask living in groupshared memory instead of 32 per-thread registers. The penumbra cone (chord
// worldShadowPenumbraChord) is unchanged. The walk is collectInstanceGridMask's SAME robust-slabs cone
// rasterization, capped at the march reach + R + footprintPad. KEEP THE WALK IN SYNC with
// sdf-instance-cull.comp.hlsl's collectInstanceGridMask (the device-buffer twin — a hand-maintained near-clone): same
// cell rasterization, same footprintPad contract; only the bit TARGET (the groupshared mask, InterlockedOr), the
// chord, the inflation, the lane striding, and the tExit cap differ. World segments need no bit — mapCore always
// evaluates them.
//
// UNIFORM CONTROL FLOW: every lane calls this with the same direction/reach
// (the lit lanes with their hitPoint, the rest with lit = false) — it carries group barriers, so no caller may skip it
// or call it under a per-lane branch. Returns the fallback DECISION (uniform across the group) so the caller marches
// correctly whether or not the mask was built:
//   2 = mask BUILT into sdfShadowMaskWords — march it (the cull); also the answer when no lane in the group is lit
//       (an empty mask nothing marches);
//   0 = NO grid packed (a grid-suppressed or few-instance program) — march the FLAT all-instances field, which for a
//       few-instance program is cheap AND, for a deliberately grid-suppressed program, MATCHES the grid-present gather
//       so the grid toggle stays render-invariant (the world-grid-cull grid==flat contract).
groupshared float4 sdfShadowGatherPoints[SDF_GROUP_SHADOW_LANES];
groupshared float4 sdfShadowGatherCone; // xyz = the hit points' apex, w = the enclosing radius + ShadowBias
groupshared uint sdfShadowGatherLitCount;

uint sdfShadowGatherGroup(bool lit, float3 hitPoint, float3 direction, float reach, uint lane) {
    // Phase 0 — clear the group mask and publish this lane's hitPoint.
    for (uint word = lane; word < SDF_SHADOW_MASK_WORDS; word += SDF_GROUP_SHADOW_LANES) {
        sdfShadowMaskWords[word] = 0u;
    }

    sdfShadowGatherPoints[lane] = float4(hitPoint, (lit ? 1.0 : 0.0));
    GroupMemoryBarrierWithGroupSync();

    // Phase 1 — lane 0 reduces the lit points to the cone apex and its enclosing radius.
    if (lane == 0u) {
        float3 sum = float3(0.0, 0.0, 0.0);
        float count = 0.0;

        [loop]
        for (uint i = 0u; (i < SDF_GROUP_SHADOW_LANES); i++) {
            float4 entry = sdfShadowGatherPoints[i];

            if (entry.w > 0.5) {
                sum += entry.xyz;
                count += 1.0;
            }
        }

        float3 apex = ((count > 0.0) ? (sum / count) : float3(0.0, 0.0, 0.0));
        float radius = 0.0;

        [loop]
        for (uint j = 0u; (j < SDF_GROUP_SHADOW_LANES); j++) {
            float4 entry = sdfShadowGatherPoints[j];

            if (entry.w > 0.5) {
                radius = max(radius, length(entry.xyz - apex));
            }
        }

        sdfShadowGatherCone = float4(apex, (radius + ShadowBias));
        sdfShadowGatherLitCount = ((uint)count);
    }

    GroupMemoryBarrierWithGroupSync();

    float4 cone = sdfShadowGatherCone;
    uint litCount = sdfShadowGatherLitCount;
    float3 origin = cone.xyz;
    float inflate = cone.w;

    // The decisions below are uniform (program-level facts and the group's own count), so an early return here leaves
    // no lane behind at a later barrier.
    uint packedInstanceCount = sdfInstanceCount();
    uint instanceOffset = sdfInstanceDirectoryOffset();
    SdfInstanceGridHeader grid = sdfLoadInstanceGridHeader(instanceOffset, packedInstanceCount);

    if (!grid.enabled) {
        return 0u; // no grid — flat fallback (cheap for few instances; matches a would-be gather so the grid toggle is invariant)
    }

    // Stage 1's shared mask covers SDF_MAX_INSTANCES. A large reserved pool is still an exact gather; the caller
    // selects the camera-tile approximation only when that quality mode was requested.

    if (litCount == 0u) {
        return 2u; // nothing in this group marches a shadow; the cleared mask is complete
    }

    float chord = worldShadowPenumbraChord(); // the soft penumbra cone's half-slope, not a bare ray
    float inverseAperture = rsqrt(max((1.0 - (chord * chord)), 1.0e-6));
    float groupReach = (reach + inflate);

    // PATH B — the shadow proxy (sdf.shadow-proxy): a SHADOW-TRANSPARENT instance (a host-flagged pure Subtraction-family
    // carve) is NOT added to the mask, so the soft-shadow march evaluates the pre-carve union hull. SOUNDNESS: the mask
    // IS the candidate set the march reads, so a skipped carve is simply never composed, and a Subtraction only ever
    // removes material, so the shadow is conservatively darker, never light-leaked. Default OFF.
    bool shadowProxy = worldShadowProxyEnabled();

    // Phase 2 — the cooperative walk. (1) The ALWAYS-tested list — dynamic + unmaskable instances the frozen grid cannot
    // bin — strided across the lanes, each bound inflated by R against the penumbra cone.
    [loop]
    for (uint a = lane; (a < grid.alwaysCount); a += SDF_GROUP_SHADOW_LANES) {
        uint index = sdfGridWordAt(grid, grid.alwaysWord + a);
        float4 bound = sdfInstanceBoundAt(instanceOffset, index);

        if (bound.w >= 0.0) {
            bound.w += inflate;
        }

        // Per-instance shadow-participation skip (the cheaper-mask twin of sdfNextVisibleInstanceRange's enumeration
        // skip): a shadow-suppressed dynamic instance never enters the mask, so it costs no mask bit and mode-2's march
        // stays consistent with the enumerate-skip. Gated on the raw condition — the gather is inherently shadow-scoped.
        if (sdfInstancePassesTileCone(bound, origin, direction, chord, inverseAperture) && !(shadowProxy && sdfInstanceShadowTransparent(instanceOffset, index)) && !sdfInstanceShadowSuppressed(instanceOffset, index)) {
            InterlockedOr(sdfShadowMaskWords[index >> 5u], (1u << (index & 31u)));
        }
    }

    // (2) The grid cells the inflated penumbra cone sweeps, far bound capped at the group reach + the query pad — the
    // same robust-slabs clip + cell rasterization the beam cull uses (see collectInstanceGridMask). The slab walk is
    // uniform across the lanes; each slab's cell box is linearized and strided across them.
    float3 gridMin = grid.origin;
    float3 gridMax = (grid.origin + (float3(grid.dims) * grid.cellSize));
    float3 farCorner = float3(
        ((direction.x > 0.0) ? gridMax.x : gridMin.x),
        ((direction.y > 0.0) ? gridMax.y : gridMin.y),
        ((direction.z > 0.0) ? gridMax.z : gridMin.z)
    );
    float projection = max(dot((farCorner - origin), direction), 0.0);
    float tFar = min(((projection + grid.footprintPad + inflate) / max((1.0 - chord), 0.01)), (groupReach + grid.footprintPad));

    float pad = (grid.footprintPad + inflate); // the query pad, widened by the group's enclosing radius
    float inflateBox = ((chord * tFar) + pad);  // the widest query radius any slab uses (chord grows it with t)
    float3 clipMin = (gridMin - inflateBox);
    float3 clipMax = (gridMax + inflateBox);
    float tEnter = 0.0;
    float tExit = tFar;
    bool missesGrid = false;

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
            missesGrid = true; // the cone provably misses the grid on this axis — only the always-list bits matter
        }
    }

    if (!missesGrid && (tEnter <= tExit)) {
        int3 dimensionsMinusOne = (int3(grid.dims) - int3(1, 1, 1));
        float slabStep = (grid.cellSize * SDF_GRID_SLAB_CELLS);
        float t0 = tEnter;

        [loop]
        for (uint slab = 0u; (slab < SDF_GRID_MAX_SLABS); slab++) {
            float t1 = (((slab + 1u) < SDF_GRID_MAX_SLABS) ? min((t0 + slabStep), tExit) : tExit);
            float3 c0 = (origin + (direction * t0));
            float3 c1 = (origin + (direction * t1));
            float radius = ((chord * t1) + pad);
            float3 low = (min(c0, c1) - radius);
            float3 high = (max(c0, c1) + radius);

            if (all(high >= gridMin) && all(low <= gridMax)) {
                int3 cellLow = clamp(int3(floor((low - grid.origin) * grid.invCellSize)), int3(0, 0, 0), dimensionsMinusOne);
                int3 cellHigh = clamp(int3(floor((high - grid.origin) * grid.invCellSize)), int3(0, 0, 0), dimensionsMinusOne);
                uint3 span = uint3(cellHigh - cellLow) + uint3(1u, 1u, 1u);
                uint cellCount = ((span.x * span.y) * span.z);

                [loop]
                for (uint c = lane; (c < cellCount); c += SDF_GROUP_SHADOW_LANES) {
                    uint cx = (c % span.x);
                    uint rest = (c / span.x);
                    uint cy = (rest % span.y);
                    uint cz = (rest / span.y);
                    uint cell = (((((uint)cellLow.z + cz) * grid.dims.y) + ((uint)cellLow.y + cy)) * grid.dims.x) + ((uint)cellLow.x + cx);
                    uint entryStart = sdfGridWordAt(grid, grid.cellStartWord + cell);
                    uint entryEnd = sdfGridWordAt(grid, grid.cellStartWord + cell + 1u);

                    [loop]
                    for (uint k = entryStart; (k < entryEnd); k++) {
                        uint index = sdfGridWordAt(grid, grid.entryWord + k);
                        float4 bound = sdfInstanceBoundAt(instanceOffset, index);

                        if (bound.w >= 0.0) {
                            bound.w += inflate;
                        }

                        // Per-instance shadow-participation skip — same raw-condition test as the always-list loop above.
                        if (sdfInstancePassesTileCone(bound, origin, direction, chord, inverseAperture) && !(shadowProxy && sdfInstanceShadowTransparent(instanceOffset, index)) && !sdfInstanceShadowSuppressed(instanceOffset, index)) {
                            InterlockedOr(sdfShadowMaskWords[index >> 5u], (1u << (index & 31u)));
                        }
                    }
                }
            }

            if (t1 >= tExit) {
                break;
            }

            t0 = t1;
        }
    }

    // Every lane's bits are visible to every lane's march after this.
    GroupMemoryBarrierWithGroupSync();

    return 2u;
}
#endif
#endif

// De-scale a Lipschitz-CLAMPED field sample back to WORLD units — the ONE primitive genuinely shared by the three
// shading-epilogue field walks (softShadowVisibility, calcAO, coverage-AA). mapMasked/map return the field pre-multiplied by the
// per-program stepScale (the <= 1-Lipschitz clamp); a consumer that COMPARES a sample against a world-space
// quantity must divide that clamp back out FIRST, or its result tracks the program's stepScale bake instead of geometry
// — the ~30%-darkening chamfer bug (stepScale = 1/sqrt(2)) a prior fix already closed. The three consumers each divide
// it back for a DELIBERATELY DIFFERENT world-space comparison — this is the divide-back FOOT-GUN the docs warn re-fixers
// about, so factor only the divide, never the surrounding intent:
//   - softShadowVisibility — the RAW clamped sample drives the step advance (an under-step is conservative) and the
//                   occlusion test, which scales the THRESHOLD up instead; the penumbra ESTIMATE divides a clearance
//                   by the world-unit distance travelled, so that clearance is de-scaled.
//   - calcAO      — the rung distance d in the (h - d) open-space deficit (a world-space rung minus a field sample).
//   - coverage-AA — the open-space RISE (aheadField - terminalRadius), a world-space DIFFERENCE. It deliberately does
//                   NOT de-scale the coverage RATIO, which stays in the SAME clamped units as the footprint termination
//                   test it mirrors; the ratio must remain in clamped units.
// stepScale == 1.0 EXACTLY for an isometric, warp-free program and x / 1.0f == x to the bit, so those scenes stay
// byte-identical whether the divide inlines here or is spelled at the call site.
// GRADIENT-SCALED CALLERS: softShadowVisibility/calcAO/calcFastAO receive a `stepScale` argument the renderView
// epilogue pre-composes as `stepScale * max(gradientMagnitude, GradientMagnitudeFloor)` — the program's own march
// clamp times the hit's LOCAL field gradient magnitude (see GradientMagnitudeFloor's remarks). This function stays
// unaware of the composition: it is still one division, so a warp-free, unit-gradient hit (both factors == 1.0)
// keeps every existing byte-identical guarantee.
float sdfDeScaleField(float clampedSample, float stepScale) {
    return (clampedSample / stepScale);
}

// The soft-shadow visibility toward lightDirection from a surface point: 1 fully lit, 0 fully occluded. The running
// minimum of k · c / t along the ray — the clearance the field reports at each sample over the distance travelled —
// marched by that clearance under a distance-proportional step ceiling. The estimate is self-sampling: the step never
// exceeds the clearance, so samples crowd toward any close approach and the sample nearest it reads a clearance
// within one step of the true minimum. The closest-approach fold (the chord between consecutive clearance spheres)
// is deliberately NOT used: it reads the previous sample too, so whether a pair straddles the close approach flips
// across neighbouring rays whenever the occluder is thinner than the step, banding a thin rim's penumbra into
// alternating lit and dark stripes; the fold also collapses to zero on a ray leaving its own surface along the
// normal, where the clearance doubles every step. The advance honors the published fold
// boundary (sdfMapStepBound) and the occlusion test reads the raw clearance in clamped units against the primary
// march's own accept threshold; the estimate divides a clearance by the world-unit distance travelled, so its
// clearance is de-scaled first (a clamped clearance would narrow a program's shadows with its stepScale bake).
float softShadowVisibility(float3 surfacePoint, float3 surfaceNormal, float3 lightDirection, uint instanceMaskBase, float stepScale, float reach) {
    bool fastMarch = worldUseFastSoftShadowMarch();
    int stepBudget = (fastMarch ? FastShadowSteps : ShadowSteps);
    float sharpness = (1.0 / worldShadowPenumbraSlope());
    float3 origin = (surfacePoint + (surfaceNormal * ShadowBias));
    float traveled = ShadowBias;
    float visibility = 1.0;

    reach = (fastMarch ? min(reach, FastShadowMaxDistance) : reach);

    [loop]
    for (int step = 0; (step < stepBudget); step++) {
        float clearance = mapDistanceMasked(origin + (lightDirection * traveled), instanceMaskBase);

        sdfEvalCount += 1.0;

        if (clearance < (SurfaceEpsilon * stepScale)) {
            return 0.0;
        }

        // Samples closer to the origin than ShadowEstimateStart read the origin surface itself (a ray skimming its
        // own curved surface at grazing incidence) and are skipped, never clamped.
        if (traveled >= ShadowEstimateStart) {
            visibility = min(visibility, ((sharpness * sdfDeScaleField(clearance, stepScale)) / traveled));
        }

        if (visibility < 0.005) {
            return 0.0;
        }

        float radius = min(clearance, sdfMapStepBound);
        float ceiling = (fastMarch
            ? max(FastShadowStepMax, (traveled * FastShadowStepFarSlope))
            : max(ShadowStepNear, (traveled * ShadowStepFarSlope)));

        traveled += clamp(radius, ShadowStepMin, ceiling);

        if (traveled > reach) {
            break;
        }
    }

    // A smoothstep so the penumbra's core and edge read as one soft band rather than a linear wedge.
    visibility = saturate(visibility);

    return ((visibility * visibility) * (3.0 - (2.0 * visibility)));
}
// Normal-ladder ambient occlusion (calcAO): from the hit, step a short ladder of fixed rungs OUTWARD along
// the surface normal; at each rung compare the distance expected to travel (h) against what the field actually reports
// (d) — where nearby geometry crowds the normal the field under-reports and the deficit (h - d) accumulates as
// occlusion, with an outer-rung falloff and a gain/clamp. THREE mapMasked() calls (was five): the rungs are
// re-spaced to span the SAME 0.01..0.13 reach at double pitch, the per-rung falloff squared (0.95^2 = 0.9025) to hold
// the same spatial decay, and the gain re-tuned 3.0 -> 5.07 so the fully-occluded floor matches the 5-tap value to
// ~0.01 (verified analytically over constant-factor and constant-gap occluder models) — a same-look AO at 60% of the
// taps. The rung loop is a [loop], NOT [unroll] (2026-09-03): every mapDistanceMasked call site inlines a full copy of
// the tape interpreter, so three unrolled rungs were three interpreter copies in the hottest kernel — measured on the
// RTX 2060 shipped world as a 60 ms AO term for three evaluations per lit pixel, ten times the primary march's cost per
// evaluation; one rolled call site is the fix, not fewer taps. Paid
// ONLY on lit hits. The exact path includes every live instance: a camera cone cannot prove an instance irrelevant
// to a tap displaced along the normal. Purely local — no hemisphere or history — but reads as contact
// shadowing in creases and under overhangs.
//
// Applied to the AMBIENT/sky fill ONLY, never the sun: soft shadows govern direct light, and multiplying occlusion into
// direct light double-darkens it (occlusion and shadow would both attenuate the same light twice). The (h - d)
// subtract mixes a WORLD-space rung h with a mapMasked distance pre-scaled by the Lipschitz clamp, so d is divided
// back to world units FIRST (d / stepScale) — the same divide-back the shadow escape exit applies; without it occlusion strength
// tracks each program's stepScale bake, not geometry.
// `stepScale` is renderView's hoisted Lipschitz clamp (see softShadowVisibility): the (h - d) rung subtract mixes a world-space
// rung with a mapMasked distance, so d is divided back to world units by it first.
static const float AmbientOcclusionReach = 0.13;
float calcAO(float3 surfacePoint, float3 surfaceNormal, uint instanceMaskBase, float stepScale) {
    float occlusion = 0.0;
    float scale = 1.0;

    [loop]
    for (int i = 0; (i < 3); i++) {
        float h = (0.01 + (((AmbientOcclusionReach - 0.01) * float(i)) / 2.0));
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
// Stylized curvature/NPR shading (render.lighting.curvature) — artistic, not physically-based, and inert until a
// world authors a gain. The runtime gate is "any gain above zero": the curvature normal costs a 5th map() centre tap
// beyond the four the normal already takes, and the enrichment carries a divide, so both hang off this one predicate
// rather than an arithmetic *0 that DXC's DXIL backend does not fold away.
bool worldCurvatureShadingEnabled() {
    return (max(worldCurvatureCavity(), max(worldCurvatureRim(), worldCurvatureInk())) > 0.0);
}
// Folds the stylized curvature terms into an already-lit surface color. Every term reads the curvature through the
// authored band: a ridge or cavity saturates at the band's low edge and the ink line spans the band, so a gain is a
// fraction in [0, 1] whatever the geometry's fillet radii (a 0.02-unit fillet has curvature 50; the raw value would
// blow every rounded edge to white). Cavity darkening scales the color down in concavities, the ridge light adds on
// convexities, and the ink outline lerps toward the ink color where the magnitude spikes.
float3 applyCurvatureShading(float3 shaded, float curvature) {
    float low = max(worldCurvatureInkLow(), 1.0e-3);
    float ridge = smoothstep(0.0, low, max(curvature, 0.0));
    float cavity = smoothstep(0.0, low, max(-curvature, 0.0));

    shaded *= (1.0 - (worldCurvatureCavity() * cavity));
    shaded += (worldCurvatureRim() * ridge);

    float ink = (worldCurvatureInk() * smoothstep(low, worldCurvatureInkHigh(), abs(curvature)));

    return lerp(shaded, worldCurvatureInkColor(), saturate(ink));
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
float marchOvershootDepth(float3 rayOrigin, float3 rayDirection, float marchStart, float firstExit, float secondEntry, float farDistance, uint instanceMaskBase, float pixelFootprint, float stepMultiplier) {
    if (marchStart < 0.0) {
        return farDistance; // a beam-culled tile — nothing to march; both marches agree at the far plane
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

        // Plain omega = 1 steps are provably clear by the 1-Lipschitz bound, so crossing the far plane here IS a
        // validated escape (unlike renderView's over-relaxed step, which must fall back to the plain step first).
        if (traveled > farDistance) {
            traveled = farDistance;
            break;
        }
    }

    return traveled;
}

#ifdef SDF_PART_RAY_BOUNDS
#include "sdf-part-bounds.hlsli"
#endif
#include "sdf-primary.hlsli"

// `lane` is the caller's index within its 8x8 workgroup and `active` whether this lane owns a rendered pixel: an
// inactive lane (past the render extent) still runs the march-free prologue and the group shadow gather's barriers
// (UNIFORM control flow — see sdfShadowGatherGroup) and then returns black, which the caller never stores.
float3 renderView(ViewportData view, float2 localUv, float marchStart, float firstExit, float secondEntry, float farBound, uint instanceMaskBase, float pixelFootprint, uint2 pixel, uint viewIndex, uint lane, bool active) {
    float3 rayOrigin = view.position.xyz;
    float3 rayDirection = cameraRayDirection(view, localUv);
    int viewMode = (int)round(view.forward.w);
    float time = view.position.w;
    float farDistance = worldFarDistance(view);

    sdfEvalCount = 0.0; // fresh tally for this pixel — see debug.view.evals (case 10 below)

    float traveled = max(marchStart, 0.0);
    bool hitSurface = false;
    int material = 0;
    // The winning instance's four anonymous lane values and frame.
    float4 hitLanes = float4(0.0, 0.0, 0.0, 0.0);
    int hitFrameSlot = -1;
    // Material blend at smooth seams (sdf-vm.hlsli's sdfMaterialBlendWeight): captured from the ACCEPT-sample march call
    // alongside `material`, because the normal/AO/shadow map calls after the loop clobber the per-thread channel. Weight 0
    // (no smooth seam within a blend radius of the hit) => the shade below is the exact table lookup, unchanged.
    float materialBlendWeight = 0.0;
    int materialBlendOther = 0;
    int marchStep = 0;
    // Tier-0 coverage AA: the CLAMPED field at the accepted hit (the terminal-step residual), captured by both march
    // paths at their hit-accept. The coverage metric derived from it in the epilogue must live in the SAME units as
    // the footprint-adaptive termination test (clamped radius vs hitThreshold) — do NOT divide by stepScale here.
    // The divide-back that is correct for softShadowVisibility/calcAO (world-space geometric comparisons) is WRONG for this
    // metric: de-scaling inflates the ratio by 1/stepScale and saturates solid hits, erasing the coverage signal.
    float terminalRadius = 0.0;
    // The footprint-adaptive hit threshold captured at the SAME accept step as terminalRadius (both march paths). The
    // epilogue's coverage = terminalRadius / hitThreshold, and `traveled` is frozen at the hit after the loop breaks, so
    // this equals a recompute of max(SurfaceEpsilon, pixelFootprint * traveled) there — capture once instead.
    float terminalHitThreshold = SurfaceEpsilon;
    // The per-program Lipschitz clamp, read ONCE and shared by softShadowVisibility, calcAO, and the coverage-AA epilogue (each
    // divides it back out of a WORLD-space comparison — a penumbra ratio, an AO rung, the open-space rise; NOT the
    // coverage ratio itself, which lives in the same clamped units as the termination test). Hoisting the single
    // sdfStepScale() read here drops three redundant reads of the same segment-directory header lane.
    float stepScale = sdfStepScale();

    // The SLICE view never marches: it evaluates the field on a plane instead (its case below), and the beam prepass
    // force-survives every tile for it — marching those would be pure waste (sky pixels would run the full MaxSteps).
    // MASK (reads the tile mask buffer directly) and OVERSHOOT (runs its OWN two marches in its case) skip the primary
    // march too — for MASK it is unused work, for OVERSHOOT running it AS WELL would be a third march. Every non-debug
    // and every OTHER debug mode still marches exactly as before (the added compares are false for them).
#ifdef SDF_PRIMARY_READ
    if (active) {
        uint hitOffset = sdfPrimaryHitOffset(pixel, viewIndex);
        float4 geometry = sdfLoadPrimaryRow(hitOffset);
        float4 attributes = sdfLoadPrimaryRow(hitOffset + 8u);
        uint flags = asuint(attributes.w);
        traveled = geometry.x;
        terminalRadius = geometry.y;
        terminalHitThreshold = geometry.z;
        material = asint(geometry.w);
        hitLanes = sdfLoadPrimaryRow(hitOffset + 4u);
        hitFrameSlot = asint(attributes.x);
        materialBlendWeight = attributes.y;
        materialBlendOther = asint(attributes.z);
        marchStep = (int)(flags & 255u);
        sdfEvalCount = (float)((flags >> 8u) & 0x7FFFFFu);
        hitSurface = ((flags & 0x80000000u) != 0u);
    }
#else
    if ((marchStart >= 0.0) && (viewMode != DebugViewModeSlice) && (viewMode != DebugViewModeMask) && (viewMode != DebugViewModeOvershoot)) {
        SdfPrimaryHit primary = sdfTracePrimary(rayOrigin, rayDirection, marchStart, firstExit, secondEntry,
            farBound, farDistance, instanceMaskBase, pixelFootprint);
        traveled = primary.traveled;
        terminalRadius = primary.radius;
        terminalHitThreshold = primary.threshold;
        material = primary.material;
        hitLanes = primary.lanes;
        hitFrameSlot = primary.frameSlot;
        materialBlendWeight = primary.blendWeight;
        materialBlendOther = primary.blendOther;
        marchStep = (int)primary.steps;
        hitSurface = primary.found;
    }

#endif // SDF_PRIMARY_READ

#ifdef SDF_PRIMARY_PASS
    if (active) {
        uint hitOffset = sdfPrimaryHitOffset(pixel, viewIndex);
        // Selected march steps occupy bits 0..7; total queries across all marches saturate in bits 8..30.
        // Bit 31 marks a hit. Local traces can execute more than 255 queries; none may overwrite the hit bit.
        uint flags = min((uint)marchStep, 255u) | (min((uint)sdfEvalCount, 0x7FFFFFu) << 8u) | (hitSurface ? 0x80000000u : 0u);
        sdfStorePrimaryRow(hitOffset, float4(traveled, terminalRadius, terminalHitThreshold, asfloat(material)));
        sdfStorePrimaryRow(hitOffset + 4u, hitLanes);
        sdfStorePrimaryRow(hitOffset + 8u, float4(asfloat(hitFrameSlot), materialBlendWeight, asfloat(materialBlendOther), asfloat(flags)));
    }
    return 0.0;
#else
    // THE GROUP SHADOW GATHER — at the one seam every lane of the workgroup reaches (the march loops above carry no
    // barrier; the epilogue below is per-lane divergent): reduce the group's hit points and build ONE shadow candidate
    // mask for all of them (sdfShadowGatherGroup, uniform control flow). The decisions feeding it are uniform: the
    // view's mode, the engine levers, the reach. A lane that is not lit still publishes (as unlit) and still walks its
    // share of the grid — its own epilogue never marches a shadow, so nothing it gathered is wasted on itself.
#ifdef SDF_SCREEN_SOURCES
    bool cullOn = worldShadowCullEnabled();
    uint groupGather = (cullOn ? 1u : 0u); // without the group gather: the camera-tile mask (1) or the flat field (0)
    uint groupAmbientGather = 0u;
#ifdef SDF_GROUP_SHADOW_GATHER
    {
        bool finalShadingMode = ((viewMode <= 0) || (viewMode >= DebugViewModeCount) || (viewMode == DebugViewModeEvals));
        bool ambientGatherWanted = (finalShadingMode && !worldAoDisabled() && !worldUseFastAmbientOcclusion());
        bool groupGatherWanted = (finalShadingMode && cullOn && !worldUseCameraTileShadowMask() && !worldSoftShadowsDisabled());

        if (ambientGatherWanted) {
            // AO measures the field, including its positive clearances, rather than binary ray visibility.
            // A camera cone or a finite contact sphere cannot preserve every ladder contribution. Build the
            // full live-instance mask once per group, excluding only the parked slots that contribute nothing.
            uint ambientInstanceCount = min(sdfInstanceCount(), SDF_MAX_INSTANCES);
            uint ambientInstanceOffset = sdfInstanceDirectoryOffset();
            for (uint word = lane; word < SDF_SHADOW_MASK_WORDS; word += SDF_GROUP_SHADOW_LANES) {
                uint bits = 0u;
                uint end = min(((word + 1u) * 32u), ambientInstanceCount);
                for (uint index = word * 32u; index < end; index++) {
                    if (sdfInstanceBoundAt(ambientInstanceOffset, index).w >= 0.0) {
                        bits |= (1u << (index & 31u));
                    }
                }
                sdfAmbientMaskWords[word] = bits;
            }
            GroupMemoryBarrierWithGroupSync();
            groupAmbientGather = 2u;
        }

        if (groupGatherWanted) {
            float groupShadowReach = (ShadowMaxDistance * worldShadowDistanceScale());

            groupGather = sdfShadowGatherGroup(hitSurface, (rayOrigin + (rayDirection * traveled)), worldSunDirection(), groupShadowReach, lane);
        }
    }
#endif
#endif

    if (!active) {
        return float3(0.0, 0.0, 0.0); // past the render extent: the caller stores nothing (no barrier follows)
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
        bool curvatureShading = worldCurvatureShadingEnabled();
        // The hit's local (program-stepScale-EXCLUDED) field gradient magnitude — see GradientMagnitudeFloor's
        // remarks. Defaults to 1 (no correction) so a view that skips needsNormal never reaches the shadow/AO
        // branches below, which are gated on needsLitColor and therefore always imply needsNormal ran.
        float gradientMagnitude = 1.0;

        if (needsNormal) {
            // Detail shapes (SDF_SHAPE_DETAIL_FLAG) perturb the normal ONLY here — the one hit-only re-evaluation,
            // never a per-step march. Every normal path shares the toggle so switching sdf.normals/curvature never
            // silently drops a detail shape's dent.
            sdfDetailShadingActive = true;

            // Authored curvature uses four taps and a center distance, reused from primary when admitted.
            // Otherwise the runtime toggle selects between the ANALYTIC forward-mode dual normal (the default — one dual
            // eval, exact through the op chain) and the 4-tap finite-difference probe (worldUseTapNormals, for the
            // A/B lever). The 4-tap path stays compiled; the toggle picks at runtime. Every path also reports the
            // hit's local gradient magnitude (see GradientMagnitudeFloor) for the shadow/AO de-scale below.
            if (curvatureShading) {
                normal = calculateNormalCurvature(surfacePoint, instanceMaskBase, terminalRadius, curvature, gradientMagnitude);
            } else if (worldUseTapNormals()) {
                normal = calculateNormal(surfacePoint, instanceMaskBase, gradientMagnitude);
            } else {
                normal = calculateNormalAnalytic(surfacePoint, instanceMaskBase, gradientMagnitude);
            }

            sdfDetailShadingActive = false;
        }

        if (needsLitColor) {
            // The shadow light's Lambert term under its soft-shadow visibility (the ambient lights still fill shadowed
            // regions, so shadows read soft, not black). The march is skipped where the surface faces away from the
            // light, where no light shadows, or when the engine-bench sdf.soft-shadows lever disables it (the light
            // then goes unshadowed). The procedural screen branch below consumes sunDiffuse too, so this march is
            // not dead there.
            float3 keyDirection = worldSunDirection();
            float sunDiffuse = max(dot(normal, keyDirection), 0.0);
            float keyVisibility = 1.0;
            // Local gradient-scaled de-scale (see GradientMagnitudeFloor): composes multiplicatively with the
            // program's own stepScale into ONE effective de-scale factor for the shadow/AO shading ESTIMATES —
            // softShadowVisibility/calcAO/calcFastAO treat it exactly like stepScale (their only uses of the
            // parameter are a world-space ratio and a clamped-units threshold, both wanting the SAME correction
            // whether it comes from the program's global march clamp or the hit's own local gradient magnitude); it
            // never reaches march step-length/soundness logic, which stays keyed on the raw `stepScale` alone.
            float shadingStepScale = (stepScale * max(gradientMagnitude, GradientMagnitudeFloor));

            // The environment scales dim the room so the diegetic screen glow dominates. They default to 1 outside the
            // world-views path (every other path shades exactly as before); the overworld sets them low per frame.
            float ambientScale = 1.0;
            float sunScale = 1.0;
#ifdef SDF_SCREEN_SOURCES
            float4 environment = sdfScreenLights[SdfScreenLightEnv];
            ambientScale = environment.x;
            sunScale = environment.y;
#endif

            if ((sunDiffuse > 0.0) && (worldShadowLightIndex() >= 0) && !worldSoftShadowsDisabled()) {
                // ONE shared scaled reach for BOTH the gather cull cone and the march ceiling (the sdf.shadow-distance
                // lever) — they MUST use the same length or the gathered occluder set is unsound for the shadow ray.
                float shadowReach = (ShadowMaxDistance * worldShadowDistanceScale());
                sdfSecondaryMarchActive = true;
#ifdef SDF_SCREEN_SOURCES
                // The shadow GRID CULL (default ON). The group phase above built this workgroup's shadow candidate mask
                // (sdfShadowMaskWords, groupshared) and decided the fallback for every lane: 2 = mask BUILT — march it
                // (the cull, bit-identical to the flat all-instances march, restricted to the instances the group's
                // shadow rays can reach); 1 = the camera-tile lever is set → the camera-tile mask; 0 = NO grid → the
                // flat all-instances fallback, which is cheap for a few-instance program and keeps the grid toggle
                // render-invariant. The cull OFF marches flat all-instances — the ground-truth reference.
                uint gather = groupGather;
                bool culled = (gather == 2u);
                uint shadowFallbackMask = ((cullOn && (gather == 1u)) ? instanceMaskBase : SDF_INSTANCE_MASK_ALL);

                sdfShadowMaskActive = culled;
                // Per-instance soft-shadow participation is live for THIS march ONLY (set unconditionally, not gated on
                // `culled`): all three fallback modes resolve through sdfNextVisibleInstanceRange, so a shadow-suppressed
                // dynamic instance (packed position.w > 0.5) must drop out of every one of them identically.
                sdfShadowParticipationActive = true;
                keyVisibility = softShadowVisibility(surfacePoint, normal, keyDirection, shadowFallbackMask, shadingStepScale, shadowReach);
                sdfShadowParticipationActive = false;
                sdfShadowMaskActive = false;
#else
                keyVisibility = softShadowVisibility(surfacePoint, normal, keyDirection, instanceMaskBase, shadingStepScale, shadowReach);
#endif
                sdfSecondaryMarchActive = false;
                sunDiffuse *= keyVisibility;
            }

            if (material >= SDF_SCREEN_MATERIAL) {
                // The procedural test-card face: a declared screen with no source bound this frame (or the plain
                // sentinel). Unlit apart from a faint sun tint — it is its own emitter, so the radiance accumulation
                // below would be discarded. Test the whole sentinel RANGE, never `==`: a screen-instance id is
                // SDF_SCREEN_MATERIAL + 1 + screenIndex and must never index the material table.
                color = (screenContent(surfacePoint, time) * (ScreenCardBase + (ScreenCardSunTint * sunDiffuse)));
            } else {
                // DETAIL RE-RESOLVE, moved ahead of AO/lighting (Puck.SignedDistance.SdfMaterial's wrap/soften/eye
                // lanes need the resolved material before either): one extra hit-only field evaluation, WITH Detail
                // shapes included, so a rivet or seam's own material wins its footprint. When the host proves
                // there are no Detail shapes, reuse the primary hit's complete attributes and seam instead.
                if (!sdfProgramLayout.noDetailShapes) {
                    sdfDetailShadingActive = true;
                    SdfHit detailHit = mapMasked(surfacePoint, instanceMaskBase);
                    sdfEvalCount += 1.0;
                    sdfDetailShadingActive = false;
                    material = detailHit.material;
                    hitLanes = detailHit.lanes;
                    hitFrameSlot = detailHit.frameSlot;
                    materialBlendWeight = sdfMaterialBlendWeight;
                    materialBlendOther = sdfMaterialBlendOther;
                }

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

                float3 layerPoint = surfacePoint;
                float3 layerNormal = normal;
                float3 layerRay = rayDirection;
#ifdef SDF_DYNAMIC_TRANSFORMS
                if (hitFrameSlot >= 0) {
                    float3 frameOrigin = sdfDynamicTransforms[3u * (uint)hitFrameSlot].xyz;
                    float4 frameRotation = sdfDynamicTransforms[3u * (uint)hitFrameSlot + 1u];
                    layerPoint = rotatePointByInverseQuaternion(surfacePoint - frameOrigin, frameRotation);
                    layerNormal = rotatePointByInverseQuaternion(normal, frameRotation);
                    layerRay = rotatePointByInverseQuaternion(rayDirection, frameRotation);
                }
#endif
                applyInset(layerPoint, layerNormal, layerRay, shadeMaterial);
                if (shadeMaterial.weathering.x > 0.0 && !curvatureShading) {
                    float unusedMagnitude;
                    calculateNormalCurvature(surfacePoint, instanceMaskBase, terminalRadius, curvature, unusedMagnitude);
                }
                applyWeathering(layerPoint, layerNormal, normal.y, curvature, pixelFootprint * traveled, hitLanes, shadeMaterial);

                // Shading-normal soften (SdfMaterial.Soften): widens the LIT normal toward a wide-stencil field
                // gradient before AO/lighting read it — the geometric normal (and the normal debug view, computed
                // upstream) stays untouched.
                applySoften(normal, surfacePoint, instanceMaskBase, shadeMaterial.soften);

                // 3-tap normal-ladder AO, into the AMBIENT fill ONLY (the sun stays governed by softShadowVisibility above).
                // Computed in the material branch so the emissive screen-card path never pays its five taps. The
                // engine-bench sdf.ao lever forces occlusion to 1 (skipping the ladder's map() evals — creases brighten).
                uint ambientMaskBase = instanceMaskBase;
#ifdef SDF_SCREEN_SOURCES
                if (!worldUseFastAmbientOcclusion()) {
                    sdfAmbientMaskActive = (groupAmbientGather == 2u);
                    ambientMaskBase = SDF_INSTANCE_MASK_ALL; // exact no-grid fallback; an active shared mask overrides it
                }
#endif
                sdfSecondaryMarchActive = true;
                float ambientOcclusion = (worldAoDisabled()
                    ? 1.0
                    : (worldUseFastAmbientOcclusion()
                        ? calcFastAO(surfacePoint, normal, ambientMaskBase, shadingStepScale)
                        : calcAO(surfacePoint, normal, ambientMaskBase, shadingStepScale)));
                sdfSecondaryMarchActive = false;
#ifdef SDF_SCREEN_SOURCES
                sdfAmbientMaskActive = false;
#endif
                // A wrapped (skin-like) material relaxes its ambient fill toward 1 — mix(ao, 1, wrap*.35), the
                // study's boolean skin flag generalized to the continuous wrap lane. wrap = 0 is a no-op.
                ambientOcclusion = lerp(ambientOcclusion, 1.0, saturate(shadeMaterial.wrap * 0.35));

                // Every light in the environment: a directional adds its Lambert term — the shadow light's under its
                // visibility, every other's under ambient occlusion — and a hemisphere its floor-plus-gradient under
                // ambient occlusion. Rim lights are view-dependent and join after the material shade. The env scales
                // dim the directional and ambient families for the room mood. A directional/point Lambert term reads
                // through sdfWrapDiffuse: wrap = 0 reduces it to the plain max(n·l, 0) term exactly.
                float3 radiance = float3(0.0, 0.0, 0.0);
                uint lightCount = worldLightCount();
                int shadowLight = worldShadowLightIndex();

                [loop]
                for (uint lightIndex = 0u; (lightIndex < lightCount); lightIndex++) {
                    SdfEnvLight light = worldLight(lightIndex);

                    if (light.kind == SdfEnvLightDirectional) {
                        float lambert = sdfWrapDiffuse(dot(normal, light.direction), shadeMaterial.wrap);
                        float occlusion = (((int)lightIndex == shadowLight) ? keyVisibility : ambientOcclusion);

                        radiance += (light.color * (((light.weight * lambert) * occlusion) * sunScale));
                    } else if (light.kind == SdfEnvLightHemisphere) {
                        float ambient = (light.weight + (light.param * normal.y));

                        radiance += (light.color * ((ambient * ambientScale) * ambientOcclusion));
                    } else if (light.kind == SdfEnvLightPoint) {
                        float3 toLight = (worldPointLightPosition(light) - surfacePoint);
                        float pointDistance = length(toLight);
                        float3 pointDirection = (toLight / max(pointDistance, 1.0e-4));
                        float pointRatio = (pointDistance / max(light.param, 1.0e-3));
                        float pointFalloff = (light.weight / (1.0 + (pointRatio * pointRatio)));
                        float pointLambert = sdfWrapDiffuse(dot(normal, pointDirection), shadeMaterial.wrap);

                        radiance += (light.color * ((pointFalloff * pointLambert) * ambientOcclusion));
                    }
                }

#ifdef SDF_SCREEN_SOURCES
                // Every BOUND diegetic screen is a colored area light: its position/orientation come from the
                // screen-surface table, its color from the per-frame framebuffer average. The dot(screenNormal, -L) gate
                // is the "light through the glass" cue — a screen only lights what sits in front of its face.
                // SdfScreenLightEnv doubles as the screen-slot COUNT (the environment entry sits right after 0..count-1).
                // right/up are orthonormal by contract (SdfScreenSurface); the normalize absorbs upload float drift.
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

                color = sdfMaterialShade(shadeMaterial, radiance, normal, rayDirection, worldSunDirection(), sunScale);

                // Warm/cool bounce (SdfMaterial.Bounce): a restrained, art-directed fill on the side of the surface
                // the key (shadow) light does not reach. Black (the default) contributes exactly 0.
                color += ((shadeMaterial.albedo * shadeMaterial.bounce) * ((1.0 - max(dot(normal, keyDirection), 0.0)) * ambientOcclusion));

                // render.environment studio reflections: the horizon gradient plus every authored softbox, sampled
                // about the mirror direction and weighted by the surface's own Fresnel response and ambient occlusion
                // (the same "specular AO" proxy the GGX lobe above has no separate occlusion term for). Zero when the
                // section is unauthored (worldStudioReflection returns exactly 0), so this is a byte-identical no-op
                // addition on an unauthored world.
                {
                    float3 f0 = lerp(float3(shadeMaterial.specular, shadeMaterial.specular, shadeMaterial.specular), shadeMaterial.albedo, shadeMaterial.metal);
                    float3 viewDirection = -rayDirection;
                    float nDotV = saturate(dot(normal, viewDirection));
                    float3 fresnel = (f0 + ((max(float3(1.0, 1.0, 1.0) - shadeMaterial.roughness, f0) - f0) * pow((1.0 - nDotV), 5.0)));
                    float3 reflectDirection = reflect(rayDirection, normal);

                    // No baked gain here: a softbox's authored `weight` and the horizon's authored colors are the
                    // levers (a constant multiplier on top of them would be a tunable baked as a constant).
                    color += ((worldStudioReflection(reflectDirection, shadeMaterial.roughness) * fresnel) * ambientOcclusion);
                }

                // The view-dependent rim lights: an additive silhouette brighten, applied after the material shade
                // because it is a look, not a light the material's specular should answer.
                [loop]
                for (uint rimIndex = 0u; (rimIndex < lightCount); rimIndex++) {
                    SdfEnvLight rim = worldLight(rimIndex);

                    if (rim.kind == SdfEnvLightRim) {
                        color += ((rim.weight * rim.color) * pow((1.0 - saturate(dot(normal, -rayDirection))), rim.param));
                    }
                }
                // Each point light's own GGX specular lobe, from its own direction rather than the shadow light's —
                // the diffuse term above already folded its Lambert contribution into `radiance`. Scaled by ambient
                // occlusion like the diffuse term (no shadow march in v1).
                [loop]
                for (uint pointIndex = 0u; (pointIndex < lightCount); pointIndex++) {
                    SdfEnvLight pointLight = worldLight(pointIndex);

                    if (pointLight.kind == SdfEnvLightPoint) {
                        float3 toLight = (worldPointLightPosition(pointLight) - surfacePoint);
                        float pointDistance = length(toLight);
                        float3 pointDirection = (toLight / max(pointDistance, 1.0e-4));
                        float pointRatio = (pointDistance / max(pointLight.param, 1.0e-3));
                        float pointFalloff = (pointLight.weight / (1.0 + (pointRatio * pointRatio)));

                        color += (pointLight.color * sdfMaterialSpecular(shadeMaterial, normal, -rayDirection, pointDirection, (pointFalloff * ambientOcclusion)));
                    }
                }

                // Occluders attenuate reflected light; self-emission remains independent.
                float attenuation = 1.0;
                [loop] for (uint index = 0u; index < lightCount; index++) {
                    SdfEnvLight field = worldLight(index);
                    if (field.kind != SdfEnvLightOccluder || field.weight <= 0.0) continue;
                    float3 delta = worldPointLightPosition(field) - surfacePoint;
                    float distanceSquared = dot(delta, delta);
                    float facing = distanceSquared > 1.0e-12 ? saturate(dot(normal, delta * rsqrt(distanceSquared))) : 1.0;
                    float radius = max(field.param, 1.0e-6);
                    attenuation *= 1.0 - saturate(field.weight * exp(-distanceSquared / (radius * radius)) * facing);
                }
                float3 selfEmission = shadeMaterial.albedo * shadeMaterial.emissive;
                color = selfEmission + (color - selfEmission) * attenuation;

                // Stylized curvature enrichment (cavity darken / rim light / ink outline). The compile-time guard strips
                // it (and the extra center tap upstream) from the shipped build on both backends.
                if (curvatureShading) {
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

            float fog = (1.0 - exp(-worldSkyFogDensity() * traveled));
            color = lerp(color, skyGradient(rayDirection), fog);

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
            //       softShadowVisibility/calcAO) — fault 1 was de-scaling the absolute metric, not a difference.
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
                // (same rule as softShadowVisibility/calcAO — fault 1 was de-scaling the ABSOLUTE coverage metric, not a difference).
                opened = smoothstep(0.0, (0.5 * probeSpan), sdfDeScaleField((aheadField - terminalRadius), stepScale));
            }

            color = lerp(color, skyGradient(rayDirection), (edgeWeight * opened));
        }
    }

#ifdef SDF_SCREEN_SOURCES
    // Bounded emissive volumes composite last, after the surface/sky color is final and before tonemap — so a
    // volume's own emission rides the same curve as everything else (see the tonemap comment below) and never paints
    // through solid geometry (clipped to the hit distance, or the far distance on a miss).
    color = shadeVolumes(color, rayOrigin, rayDirection, (hitSurface ? traveled : farDistance), pixel, view.position.w);
#endif

    // render.tonemap: applied last, to the frame's actual final color — a hit's shaded color AND a miss's sky alike,
    // so a silhouette's sky blend and the open sky beside it sit on the same curve (tonemapping hits alone haloed
    // every silhouette against an un-mapped sky). sdf-sky.comp applies the SAME curve to the sky it writes into a
    // beam-culled tile, so the tile seam stays bit-identical. Every debug view overwrites viewColor below and never
    // reads `color` again, so it stays untouched; None (the default) is a no-op.
    if (worldTonemapMode() == SdfTonemapFilmic) {
        color = sdfFilmicTonemap(color);
    }

    float3 viewColor = color;

    switch (viewMode) {
        case 1: { // depth
            float depth = saturate(traveled / farDistance);
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
                  // green = epsilon-dominated hit, cyan = footprint-dominated hit (either arm — a closest-approach
                  // candidate accepted at exhaustion classifies by the same dominance test, because it satisfied the
                  // same rule), red = MaxSteps exhausted with no candidate inside the rule (the ground-notch
                  // hypothesis), dark blue = escaped (a validated step past the far distance or the F1 far bound, or a
                  // tile the beam culled empty). A break leaves marchStep below MaxSteps; only exhaustion reaches it.
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
            else if ((marchStart < 0.0) || (marchStep < MaxSteps)) {
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
            float clampedDepth = marchOvershootDepth(rayOrigin, rayDirection, marchStart, firstExit, secondEntry, farDistance, instanceMaskBase, pixelFootprint, 1.0);
            float unclampedDepth = marchOvershootDepth(rayOrigin, rayDirection, marchStart, firstExit, secondEntry, farDistance, instanceMaskBase, pixelFootprint, (1.0 / stepScale));
            float disagreement = abs(clampedDepth - unclampedDepth);
            // Log-scaled against the march reach so a sub-unit tunnel still reads while a full escape saturates.
            float hot = saturate(log2(1.0 + disagreement) / log2(1.0 + farDistance));
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
#endif // SDF_PRIMARY_PASS
}

#endif
