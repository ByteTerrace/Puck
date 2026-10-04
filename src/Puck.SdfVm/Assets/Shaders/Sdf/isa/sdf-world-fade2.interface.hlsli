// Generated from shader interface 'sdf-world-fade2' (sha256/6369c52440a7dcb91b85fae77f28d8ca0b35ba742ce2ac36ee533fa021ed87f2). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_WORLD_FADE2
#define PUCK_SHADER_INTERFACE_SDF_WORLD_FADE2

struct SdfLight {
    [[vk::offset(0)]] float3 Direction;
    [[vk::offset(12)]] float Weight;
    [[vk::offset(16)]] float3 Color;
    [[vk::offset(28)]] uint Kind;
    [[vk::offset(32)]] float Param;
    [[vk::offset(36)]] uint Shadows;
    [[vk::offset(40)]] int DynamicSlot;
    [[vk::offset(44)]] float Bounce;
};

struct SdfSkyBlock {
    [[vk::offset(0)]] uint AirFlags;
    [[vk::offset(4)]] uint LayerCount;
    [[vk::offset(8)]] float Ambient;
    [[vk::offset(12)]] uint Quality;
    [[vk::offset(16)]] float3 FrameRight;
    [[vk::offset(28)]] uint BaseRun;
    [[vk::offset(32)]] float3 FrameUp;
    [[vk::offset(44)]] uint UpperRuns;
    [[vk::offset(48)]] float3 FrameForward;
    [[vk::offset(60)]] float Reflection;
    [[vk::offset(64)]] uint AirLightCount;
    [[vk::offset(68)]] float FogExtinction;
    [[vk::offset(72)]] float FogBase;
    [[vk::offset(76)]] float FogFalloff;
    [[vk::offset(80)]] float3 FogColor;
    [[vk::offset(92)]] float MediumExtinction;
    [[vk::offset(96)]] float3 MediumColor;
    [[vk::offset(108)]] float MediumSurface;
    [[vk::offset(112)]] float HazeExtinction;
    [[vk::offset(116)]] float HazeBase;
    [[vk::offset(120)]] float HazeFalloff;
    [[vk::offset(124)]] float HazeAnisotropy;
    [[vk::offset(128)]] float3 AirLight0Direction;
    [[vk::offset(140)]] uint _pad140;
    [[vk::offset(144)]] float3 AirLight0Radiance;
    [[vk::offset(156)]] uint _pad156;
    [[vk::offset(160)]] float3 AirLight1Direction;
    [[vk::offset(172)]] uint _pad172;
    [[vk::offset(176)]] float3 AirLight1Radiance;
    [[vk::offset(188)]] uint _pad188;
    [[vk::offset(192)]] float3 AirLight2Direction;
    [[vk::offset(204)]] uint _pad204;
    [[vk::offset(208)]] float3 AirLight2Radiance;
    [[vk::offset(220)]] uint _pad220;
    [[vk::offset(224)]] float3 AirLight3Direction;
    [[vk::offset(236)]] uint _pad236;
    [[vk::offset(240)]] float3 AirLight3Radiance;
    [[vk::offset(252)]] uint _pad252;
};

struct SdfSkyLayer {
    [[vk::offset(0)]] uint Kind;
    [[vk::offset(4)]] uint Blend;
    [[vk::offset(8)]] uint Detail;
    [[vk::offset(12)]] uint Visibility;
    [[vk::offset(16)]] float Opacity;
    [[vk::offset(20)]] uint Mask;
    [[vk::offset(24)]] float MaskSoftness;
    [[vk::offset(28)]] float Phase;
    [[vk::offset(32)]] float4 MaskBand;
    [[vk::offset(48)]] float4 Rotation;
    [[vk::offset(64)]] float4 P0;
    [[vk::offset(80)]] float4 P1;
    [[vk::offset(96)]] float4 P2;
    [[vk::offset(112)]] float4 P3;
    [[vk::offset(128)]] float4 P4;
    [[vk::offset(144)]] float4 P5;
    [[vk::offset(160)]] float4 P6;
    [[vk::offset(176)]] float4 P7;
};

struct SdfShadowHandoff {
    [[vk::offset(0)]] int Outgoing;
    [[vk::offset(4)]] int Incoming;
    [[vk::offset(8)]] int Slot;
    [[vk::offset(12)]] float Weight;
};

// The Frame group: descriptor set 0, register space 0.
struct SdfWorldFade2Frame {
    [[vk::offset(0)]] float2 pointer;
    [[vk::offset(8)]] uint2 tick;
    [[vk::offset(16)]] float time;
    [[vk::offset(20)]] float timeDelta;
    [[vk::offset(24)]] uint frame;
    [[vk::offset(28)]] uint tickRate;
    [[vk::offset(32)]] uint pointerDown;
    [[vk::offset(36)]] uint pointerPresses;
    [[vk::offset(40)]] uint _pad40;
    [[vk::offset(44)]] uint _pad44;
    [[vk::offset(48)]] float3 cameraPosition;
    [[vk::offset(60)]] float cameraFov;
    [[vk::offset(64)]] float3 cameraTarget;
    [[vk::offset(76)]] uint _pad76;
    [[vk::offset(80)]] float3 cameraUp;
    [[vk::offset(92)]] uint _pad92;
    [[vk::offset(96)]] float2 placedExtent;
};
[[vk::binding(0, 0)]] ConstantBuffer<SdfWorldFade2Frame> frameGroup : register(b0, space0);

// The World group: descriptor set 1, register space 1.
[[vk::binding(0, 1)]] StructuredBuffer<uint4> sdfWords : register(t0, space1);
[[vk::binding(1, 1)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t1, space1);
[[vk::binding(2, 1)]] StructuredBuffer<float4> sdfPreviousDynamicTransforms : register(t2, space1);
[[vk::binding(3, 1)]] StructuredBuffer<float4> sdfPreviousMeshTransforms : register(t3, space1);
[[vk::binding(4, 1)]] StructuredBuffer<uint> sdfFrameInstanceGrid : register(t4, space1);
[[vk::binding(5, 1)]] StructuredBuffer<float4> screenSurfaces : register(t5, space1);
[[vk::binding(6, 1)]] StructuredBuffer<float4> screenMappings : register(t6, space1);
[[vk::binding(7, 1)]] StructuredBuffer<float4> sdfScreenLights : register(t7, space1);
[[vk::binding(8, 1)]] StructuredBuffer<uint4> sdfDecalCells : register(t8, space1);
[[vk::binding(9, 1)]] StructuredBuffer<float> sdfBrickPool : register(t9, space1);
[[vk::binding(10, 1)]] StructuredBuffer<float4> sdfVolumes : register(t10, space1);
[[vk::binding(11, 1)]] StructuredBuffer<uint> sdfMeshRegion : register(t11, space1);
[[vk::binding(12, 1)]] Texture2D<float4> sdfGlyphAtlas : register(t12, space1);
[[vk::binding(13, 1)]] SamplerState samplers[2] : register(s13, space1);
[[vk::binding(15, 1)]] Texture2D<float4> sdfMeshAlbedo : register(t15, space1);
[[vk::binding(16, 1)]] Texture2D<float4> sdfMeshNormals : register(t16, space1);
[[vk::binding(17, 1)]] Texture2D<float4> sdfMeshOcclusion : register(t17, space1);
[[vk::binding(18, 1)]] Texture2D<float4> sdfMeshMaterials : register(t18, space1);
[[vk::binding(19, 1)]] Texture2D<float4> sdfMeshEmission : register(t19, space1);
[[vk::binding(20, 1)]] Texture2D<float4> sdfImpostorAlbedo : register(t20, space1);
[[vk::binding(21, 1)]] Texture2D<float4> sdfImpostorNormals : register(t21, space1);
[[vk::binding(22, 1)]] Texture2D<float4> sdfImpostorDepth : register(t22, space1);
[[vk::binding(23, 1)]] Texture2D<float4> sdfImpostorMaterials : register(t23, space1);
[[vk::binding(24, 1)]] Texture2D<float4> sdfImpostorEmission : register(t24, space1);
[[vk::binding(25, 1)]] StructuredBuffer<SdfLight> sdfLightsLayout1e038717dee1c3e1f05c178136b7d59290218483a92b3534280bf2f001fc985b : register(t25, space1);
#define sdfLights sdfLightsLayout1e038717dee1c3e1f05c178136b7d59290218483a92b3534280bf2f001fc985b
[[vk::binding(26, 1)]] StructuredBuffer<SdfSkyBlock> sdfSkyLayoutcf5989bcab395177650d42c028df69d633f031173a236e87e6f40e104d525aa9 : register(t26, space1);
#define sdfSky sdfSkyLayoutcf5989bcab395177650d42c028df69d633f031173a236e87e6f40e104d525aa9
[[vk::binding(27, 1)]] StructuredBuffer<SdfSkyLayer> sdfSkyLayersLayoutb8b05e94adeef57d901b9217964b288f590ab8e7f74318d31922800728b169a8 : register(t27, space1);
#define sdfSkyLayers sdfSkyLayersLayoutb8b05e94adeef57d901b9217964b288f590ab8e7f74318d31922800728b169a8
[[vk::binding(28, 1)]] StructuredBuffer<float4> sdfSkyCoefficients : register(t28, space1);
[[vk::binding(29, 1)]] StructuredBuffer<SdfShadowHandoff> sdfShadowHandoffsLayout981efb60b212c94dac2862f0fb486ea3aaed2504fac947a2a5d6ffc91403e13e : register(t29, space1);
#define sdfShadowHandoffs sdfShadowHandoffsLayout981efb60b212c94dac2862f0fb486ea3aaed2504fac947a2a5d6ffc91403e13e
[[vk::binding(30, 1)]] StructuredBuffer<uint2> sdfSkyEnvironment : register(t30, space1);

// The Pass group: descriptor set 3, register space 3.
struct SdfWorldFade2Pass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float aspectRatio;
    [[vk::offset(12)]] uint cameraTileShadowMask;
    [[vk::offset(16)]] float curvatureCavity;
    [[vk::offset(20)]] float curvatureInk;
    [[vk::offset(24)]] uint _pad24;
    [[vk::offset(28)]] uint _pad28;
    [[vk::offset(32)]] float3 curvatureInkColor;
    [[vk::offset(44)]] float curvatureInkHigh;
    [[vk::offset(48)]] float curvatureInkLow;
    [[vk::offset(52)]] float curvatureRim;
    [[vk::offset(56)]] uint debugMode;
    [[vk::offset(60)]] float debugSliceAxis;
    [[vk::offset(64)]] float debugSliceOffset;
    [[vk::offset(68)]] uint disableAmbientOcclusion;
    [[vk::offset(72)]] uint disableFarBound;
    [[vk::offset(76)]] uint disableScreenLights;
    [[vk::offset(80)]] uint disableShadowCull;
    [[vk::offset(84)]] uint disableSoftShadows;
    [[vk::offset(88)]] uint enableShadowProxy;
    [[vk::offset(92)]] float farDistance;
    [[vk::offset(96)]] uint fastAmbientOcclusion;
    [[vk::offset(100)]] uint fastSoftShadowMarch;
    [[vk::offset(104)]] uint finiteDifferenceNormals;
    [[vk::offset(108)]] uint _pad108;
    [[vk::offset(112)]] float2 frustumOffset;
    [[vk::offset(120)]] uint geometryOnly;
    [[vk::offset(124)]] uint gridFlags;
    [[vk::offset(128)]] float gridLineWidth;
    [[vk::offset(132)]] uint _pad132;
    [[vk::offset(136)]] uint _pad136;
    [[vk::offset(140)]] uint _pad140;
    [[vk::offset(144)]] float4 gridObjectFrame;
    [[vk::offset(160)]] float3 gridObjectOrigin;
    [[vk::offset(172)]] float gridObjectPatchRadius;
    [[vk::offset(176)]] float3 gridObjectPitch;
    [[vk::offset(188)]] float gridPlaneY;
    [[vk::offset(192)]] float4 gridWorldFrame;
    [[vk::offset(208)]] float3 gridWorldOrigin;
    [[vk::offset(220)]] uint _pad220;
    [[vk::offset(224)]] float3 gridWorldPitch;
    [[vk::offset(236)]] uint historyFrames;
    [[vk::offset(240)]] uint2 imageExtent;
    [[vk::offset(248)]] uint2 indirectAllocation;
    [[vk::offset(256)]] uint indirectCertificateRevision;
    [[vk::offset(260)]] uint indirectEpoch;
    [[vk::offset(264)]] uint indirectFrame;
    [[vk::offset(268)]] uint indirectMethod;
    [[vk::offset(272)]] uint4 indirectPickPixel;
    [[vk::offset(288)]] uint indirectReadGeneration;
    [[vk::offset(292)]] uint indirectReadPublication;
    [[vk::offset(296)]] uint indirectReceiverProofs;
    [[vk::offset(300)]] uint indirectSources;
    [[vk::offset(304)]] uint indirectTier;
    [[vk::offset(308)]] uint instanceMaskWordCount;
    [[vk::offset(312)]] float2 jitter;
    [[vk::offset(320)]] uint lightCount;
    [[vk::offset(324)]] uint lightMap;
    [[vk::offset(328)]] uint lightMapCount;
    [[vk::offset(332)]] uint _pad332;
    [[vk::offset(336)]] float4 lightMaps[84];
    [[vk::offset(1680)]] float lightSweepRadius;
    [[vk::offset(1684)]] uint meshDraws;
    [[vk::offset(1688)]] float nearDistance;
    [[vk::offset(1692)]] uint _pad1692;
    [[vk::offset(1696)]] float4 previousView[6];
    [[vk::offset(1792)]] uint screenCount;
    [[vk::offset(1796)]] uint shadowAmortize;
    [[vk::offset(1800)]] float shadowDistanceScale;
    [[vk::offset(1804)]] uint shadowFadeCount;
    [[vk::offset(1808)]] uint shadowLightReject;
    [[vk::offset(1812)]] uint shadowOwnershipReject;
    [[vk::offset(1816)]] uint shadowSlotCount;
    [[vk::offset(1820)]] uint _pad1820;
    [[vk::offset(1824)]] int4 shadowSlots;
    [[vk::offset(1840)]] float tanHalfFieldOfView;
    [[vk::offset(1844)]] uint temporal;
    [[vk::offset(1848)]] uint2 tileGrid;
    [[vk::offset(1856)]] uint viewBase;
    [[vk::offset(1860)]] uint _pad1860;
    [[vk::offset(1864)]] uint _pad1864;
    [[vk::offset(1868)]] uint _pad1868;
    [[vk::offset(1872)]] float3 viewForward;
    [[vk::offset(1884)]] uint _pad1884;
    [[vk::offset(1888)]] float3 viewPosition;
    [[vk::offset(1900)]] uint _pad1900;
    [[vk::offset(1904)]] float3 viewRight;
    [[vk::offset(1916)]] uint _pad1916;
    [[vk::offset(1920)]] float3 viewUp;
    [[vk::offset(1932)]] uint viewportCount;
    [[vk::offset(1936)]] uint workCounterRow;
    [[vk::offset(1940)]] uint workCounterRowDetail;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfWorldFade2Pass> passGroupIsa4308F2B0 : register(b0, space3);
#define passGroup passGroupIsa4308F2B0
[[vk::binding(1, 3)]] StructuredBuffer<float> indirectLightDepth : register(t1, space3);
[[vk::binding(2, 3)]] RWStructuredBuffer<float> indirectLightDepthRW : register(u2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<uint> indirectCache : register(t3, space3);
[[vk::binding(4, 3)]] RWStructuredBuffer<uint> indirectCacheRW : register(u4, space3);
[[vk::binding(5, 3)]] RWStructuredBuffer<uint> indirectPickRW : register(u5, space3);
[[vk::binding(6, 3)]] StructuredBuffer<int4> indirectBricks : register(t6, space3);
[[vk::binding(7, 3)]] StructuredBuffer<uint> sdfInstanceMasks : register(t7, space3);
[[vk::binding(8, 3)]] RWStructuredBuffer<uint> sdfInstanceMasksRW : register(u8, space3);
[[vk::binding(9, 3)]] StructuredBuffer<uint> sdfSegmentTapes : register(t9, space3);
[[vk::binding(10, 3)]] RWStructuredBuffer<uint> sdfSegmentTapesRW : register(u10, space3);
[[vk::binding(11, 3)]] StructuredBuffer<float> tiles : register(t11, space3);
[[vk::binding(12, 3)]] RWStructuredBuffer<float> tilesRW : register(u12, space3);
[[vk::binding(13, 3)]] StructuredBuffer<uint> cullBounds : register(t13, space3);
[[vk::binding(14, 3)]] RWStructuredBuffer<uint> cullBoundsRW : register(u14, space3);
[[vk::binding(15, 3)]] RWStructuredBuffer<uint> viewsArgsRW : register(u15, space3);
[[vk::binding(16, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t16, space3);
[[vk::binding(17, 3)]] RWStructuredBuffer<uint> sdfVisibilityRecordsRW : register(u17, space3);
[[vk::binding(18, 3)]] RWStructuredBuffer<float> reactivityRW : register(u18, space3);
[[vk::binding(19, 3)]] StructuredBuffer<uint> shadowHistory : register(t19, space3);
[[vk::binding(20, 3)]] RWStructuredBuffer<uint> shadowHistoryRW : register(u20, space3);
[[vk::binding(21, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> output : register(u21, space3);
[[vk::binding(22, 3)]] Texture2D<float4> screenSources[32] : register(t22, space3);
[[vk::binding(54, 3)]] Texture2D<float4> meshVisibility : register(t54, space3);
[[vk::binding(55, 3)]] RWStructuredBuffer<uint> workCounters : register(u55, space3);
[[vk::binding(56, 3)]] Texture2D<float2> incomingVisibility : register(t56, space3);
[[vk::binding(57, 3)]] [[vk::image_format("rg8")]] RWTexture2D<float2> incomingVisibilityRW : register(u57, space3);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, sky evaluations, sky hashes,
// sky texture loads, then six shadow-slot step counts, secondary-shadow pixels, indirect hits, samples and unresolved work,
// followed by shape evaluations and shape gradients, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 34u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShadowWord = 10u;
static const uint PuckWorkShadowSlots = 6u;
static const uint PuckWorkShadowPixelsWord = 22u;
static const uint PuckWorkIndirectWord = 24u;
static const uint PuckWorkShapesWord = 30u;
static const uint PuckWorkGradientsWord = 32u;
// Adds to one count: the low word atomically, then the high word by one when that addition carries.
void puckAddWork(uint word, uint amount) {
    if (amount == 0u) {
        return;
    }

    uint before;

    InterlockedAdd(workCounters[word], amount, before);

    if (before > (0xFFFFFFFFu - amount)) {
        InterlockedAdd(workCounters[word + 1u], 1u);
    }
}
// Adds an invocation's march steps and texels written to its pass's row: the wave sums both, and its first active
// lane adds each sum. Every lane that did work reaches the call, since a lane that returned before it counts nothing.
void puckCountWork(uint steps, uint texels) {
    uint waveSteps = WaveActiveSum(steps);
    uint waveTexels = WaveActiveSum(texels);

    if (WaveIsFirstLane()) {
        uint row = (passGroup.workCounterRow * PuckWorkRowWords);

        puckAddWork((row + PuckWorkStepsWord), waveSteps);
        puckAddWork((row + PuckWorkTexelsWord), waveTexels);
    }
}
// Named rows are disjoint from the plain pass row; the ledger sums both once the submission completes.
// Per-lane atomics permit divergent layer evaluation without merging lanes targeting different rows.
void puckCountDetail(uint detail, uint steps, uint texels, uint evaluations, uint hashes, uint loads) {
    if (passGroup.workCounterRowDetail == 0u) {
        return;
    }
    uint row = ((passGroup.workCounterRowDetail + detail) * PuckWorkRowWords);
    puckAddWork((row + PuckWorkStepsWord), steps);
    puckAddWork((row + PuckWorkTexelsWord), texels);
    puckAddWork((row + PuckWorkSkyWord), evaluations);
    puckAddWork((row + PuckWorkSkyHashesWord), hashes);
    puckAddWork((row + PuckWorkSkyTextureLoadsWord), loads);
}
void puckCountShapes(uint shapes, uint gradients) {
    uint waveShapes = WaveActiveSum(shapes);
    uint waveGradients = WaveActiveSum(gradients);
    if (WaveIsFirstLane()) {
        uint row = passGroup.workCounterRow * PuckWorkRowWords;
        puckAddWork(row + PuckWorkShapesWord, waveShapes);
        puckAddWork(row + PuckWorkGradientsWord, waveGradients);
    }
}
// Each invocation names its level or proof detail; those rows sum into the pass once at readback.
void puckCountIndirect(uint detail, uint hits, uint samples, uint unresolved) {
    uint row = ((passGroup.workCounterRowDetail == 0u)
        ? passGroup.workCounterRow
        : (passGroup.workCounterRowDetail + detail)) * PuckWorkRowWords;
    puckAddWork((row + PuckWorkIndirectWord), hits);
    puckAddWork((row + PuckWorkIndirectWord + 2u), samples);
    puckAddWork((row + PuckWorkIndirectWord + 4u), unresolved);
}
// Each invocation owns its slot delta. A divergent march need not reconverge its subgroup before a
// reduction and a separate election on Vulkan (SPIR-V Uniform Control Flow).
void puckCountShadow(uint slot, uint steps) {
    if (slot < PuckWorkShadowSlots) {
        puckAddWork(((passGroup.workCounterRow * PuckWorkRowWords) + PuckWorkShadowWord + (slot * 2u)), steps);
    }
}
// One secondary lit pixel belongs to one decision. Its march and slot counts are a partition of the pass,
// while its pixel count exposes rejections and reuse even when a march takes zero field samples.
void puckCountShadowDecision(uint detail, uint slot, uint steps) {
    uint row = ((passGroup.workCounterRowDetail == 0u ? passGroup.workCounterRow : passGroup.workCounterRowDetail + detail) * PuckWorkRowWords);
    puckAddWork(row + PuckWorkShadowPixelsWord, 1u);
    puckAddWork(row + PuckWorkStepsWord, steps);
    puckAddWork(row + PuckWorkShadowWord + (slot * 2u), steps);
}
// Adds a fragment's march steps and texels written to its pass's row: the wave sums its lanes that are not helper
// lanes, and the first of them adds each sum. A helper lane counts nothing and never adds, whether or not the
// backend lets it take part in wave operations, since its atomics have no effect.
void puckCountFragmentWork(uint steps, uint texels) {
    bool counting = !IsHelperLane();
    uint waveSteps = WaveActiveSum(counting ? steps : 0u);
    uint waveTexels = WaveActiveSum(counting ? texels : 0u);

    if (counting && (WavePrefixCountBits(counting) == 0u)) {
        uint row = (passGroup.workCounterRow * PuckWorkRowWords);

        puckAddWork((row + PuckWorkStepsWord), waveSteps);
        puckAddWork((row + PuckWorkTexelsWord), waveTexels);
    }
}

#endif // PUCK_SHADER_INTERFACE_SDF_WORLD_FADE2
