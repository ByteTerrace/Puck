// Generated from shader interface 'sdf-world' (sha256/e52f8cee4a15fc881dc02d9a7cf45c0c818153a53c0f0a8264f4303b2ca4f88f). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_WORLD
#define PUCK_SHADER_INTERFACE_SDF_WORLD

struct SdfLight {
    [[vk::offset(0)]] float3 Direction;
    [[vk::offset(12)]] float Weight;
    [[vk::offset(16)]] float3 Color;
    [[vk::offset(28)]] uint Kind;
    [[vk::offset(32)]] float Param;
    [[vk::offset(36)]] uint Shadows;
    [[vk::offset(40)]] int DynamicSlot;
    [[vk::offset(44)]] float Bounce;
    [[vk::offset(48)]] float3 SpotAxis;
    [[vk::offset(60)]] float SpotOffset;
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
struct SdfWorldFrame {
    [[vk::offset(0)]] float2 pointer;
    [[vk::offset(8)]] uint2 tick;
    [[vk::offset(16)]] float time;
    [[vk::offset(20)]] float timeDelta;
    [[vk::offset(24)]] uint frame;
    [[vk::offset(28)]] uint tickRate;
    [[vk::offset(32)]] uint pointerDown;
};
[[vk::binding(0, 0)]] ConstantBuffer<SdfWorldFrame> frameGroup : register(b0, space0);

// The World group: descriptor set 1, register space 1.
[[vk::binding(0, 1)]] StructuredBuffer<uint4> sdfWords : register(t0, space1);
[[vk::binding(1, 1)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t1, space1);
[[vk::binding(2, 1)]] StructuredBuffer<uint> sdfSampledValues : register(t2, space1);
[[vk::binding(3, 1)]] StructuredBuffer<float4> sdfMeshBakePoints : register(t3, space1);
[[vk::binding(4, 1)]] Texture2D<float4> sdfGlyphAtlas : register(t4, space1);
[[vk::binding(5, 1)]] SamplerState samplers[2] : register(s5, space1);
[[vk::binding(7, 1)]] StructuredBuffer<float4> sdfPreviousDynamicTransforms : register(t7, space1);
[[vk::binding(8, 1)]] StructuredBuffer<uint> sdfPreviousMeshTransforms : register(t8, space1);
[[vk::binding(9, 1)]] StructuredBuffer<float4> screenSurfaces : register(t9, space1);
[[vk::binding(10, 1)]] StructuredBuffer<float4> screenMappings : register(t10, space1);
[[vk::binding(11, 1)]] StructuredBuffer<float4> sdfScreenLights : register(t11, space1);
[[vk::binding(12, 1)]] StructuredBuffer<uint4> sdfDecalCells : register(t12, space1);
[[vk::binding(13, 1)]] StructuredBuffer<float4> sdfVolumes : register(t13, space1);
[[vk::binding(14, 1)]] StructuredBuffer<uint> sdfMeshRegion : register(t14, space1);
[[vk::binding(15, 1)]] StructuredBuffer<uint> sdfMeshIrradiance : register(t15, space1);
[[vk::binding(16, 1)]] StructuredBuffer<uint> sdfFrameSampled : register(t16, space1);
[[vk::binding(17, 1)]] StructuredBuffer<SdfLight> sdfLightsLayout27ca437eafcfb2ef7e652cdc81f326face73209e6adffa61f04dffc997d7c8c5 : register(t17, space1);
#define sdfLights sdfLightsLayout27ca437eafcfb2ef7e652cdc81f326face73209e6adffa61f04dffc997d7c8c5
[[vk::binding(18, 1)]] StructuredBuffer<SdfSkyBlock> sdfSkyLayoutcf5989bcab395177650d42c028df69d633f031173a236e87e6f40e104d525aa9 : register(t18, space1);
#define sdfSky sdfSkyLayoutcf5989bcab395177650d42c028df69d633f031173a236e87e6f40e104d525aa9
[[vk::binding(19, 1)]] StructuredBuffer<SdfSkyLayer> sdfSkyLayersLayoutb8b05e94adeef57d901b9217964b288f590ab8e7f74318d31922800728b169a8 : register(t19, space1);
#define sdfSkyLayers sdfSkyLayersLayoutb8b05e94adeef57d901b9217964b288f590ab8e7f74318d31922800728b169a8
[[vk::binding(20, 1)]] StructuredBuffer<float4> sdfSkyCoefficients : register(t20, space1);
[[vk::binding(21, 1)]] StructuredBuffer<SdfShadowHandoff> sdfShadowHandoffsLayout981efb60b212c94dac2862f0fb486ea3aaed2504fac947a2a5d6ffc91403e13e : register(t21, space1);
#define sdfShadowHandoffs sdfShadowHandoffsLayout981efb60b212c94dac2862f0fb486ea3aaed2504fac947a2a5d6ffc91403e13e
[[vk::binding(22, 1)]] StructuredBuffer<uint2> sdfSkyEnvironment : register(t22, space1);

// The Pass group: descriptor set 3, register space 3.
struct SdfWorldPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float airLight;
    [[vk::offset(12)]] float aspectRatio;
    [[vk::offset(16)]] float curvatureCavity;
    [[vk::offset(20)]] float curvatureInk;
    [[vk::offset(24)]] uint _pad24;
    [[vk::offset(28)]] uint _pad28;
    [[vk::offset(32)]] float3 curvatureInkColor;
    [[vk::offset(44)]] float curvatureInkHigh;
    [[vk::offset(48)]] float curvatureInkLow;
    [[vk::offset(52)]] float curvatureRim;
    [[vk::offset(56)]] uint debugMode;
    [[vk::offset(60)]] uint disableAmbientOcclusion;
    [[vk::offset(64)]] uint disableScreenLights;
    [[vk::offset(68)]] uint disableSoftShadows;
    [[vk::offset(72)]] float exposeShare;
    [[vk::offset(76)]] float exposure;
    [[vk::offset(80)]] float farDistance;
    [[vk::offset(84)]] float flowClock;
    [[vk::offset(88)]] float2 frustumOffset;
    [[vk::offset(96)]] uint geometryOnly;
    [[vk::offset(100)]] uint gridFlags;
    [[vk::offset(104)]] float gridLineWidth;
    [[vk::offset(108)]] uint _pad108;
    [[vk::offset(112)]] float4 gridObjectFrame;
    [[vk::offset(128)]] float3 gridObjectOrigin;
    [[vk::offset(140)]] float gridObjectPatchRadius;
    [[vk::offset(144)]] float3 gridObjectPitch;
    [[vk::offset(156)]] float gridPlaneY;
    [[vk::offset(160)]] float4 gridWorldFrame;
    [[vk::offset(176)]] float3 gridWorldOrigin;
    [[vk::offset(188)]] uint _pad188;
    [[vk::offset(192)]] float3 gridWorldPitch;
    [[vk::offset(204)]] uint historyFrames;
    [[vk::offset(208)]] uint2 imageExtent;
    [[vk::offset(216)]] uint _pad216;
    [[vk::offset(220)]] uint _pad220;
    [[vk::offset(224)]] float4 indirectApply;
    [[vk::offset(240)]] float indirectContact;
    [[vk::offset(244)]] uint _pad244;
    [[vk::offset(248)]] uint _pad248;
    [[vk::offset(252)]] uint _pad252;
    [[vk::offset(256)]] float4 indirectSourceGains;
    [[vk::offset(272)]] uint indirectSources;
    [[vk::offset(276)]] uint _pad276;
    [[vk::offset(280)]] float2 jitter;
    [[vk::offset(288)]] uint lightCount;
    [[vk::offset(292)]] uint _pad292;
    [[vk::offset(296)]] uint _pad296;
    [[vk::offset(300)]] uint _pad300;
    [[vk::offset(304)]] uint4 lightMask;
    [[vk::offset(320)]] float localAdaptation;
    [[vk::offset(324)]] uint meshDraws;
    [[vk::offset(328)]] uint _pad328;
    [[vk::offset(332)]] uint _pad332;
    [[vk::offset(336)]] int4 meshIrradianceLevels[3];
    [[vk::offset(384)]] float4 meshIrradianceSpacings[3];
    [[vk::offset(432)]] uint meshIrradianceVolume;
    [[vk::offset(436)]] uint _pad436;
    [[vk::offset(440)]] uint _pad440;
    [[vk::offset(444)]] uint _pad444;
    [[vk::offset(448)]] float4 meshShadowCascades[16];
    [[vk::offset(704)]] uint meshShadowCount;
    [[vk::offset(708)]] uint _pad708;
    [[vk::offset(712)]] uint _pad712;
    [[vk::offset(716)]] uint _pad716;
    [[vk::offset(720)]] float4 meshShadowParameters;
    [[vk::offset(736)]] uint meshShadowTexels;
    [[vk::offset(740)]] float meterLuminance;
    [[vk::offset(744)]] float nearDistance;
    [[vk::offset(748)]] uint _pad748;
    [[vk::offset(752)]] float4 previousView[6];
    [[vk::offset(848)]] uint screenCount;
    [[vk::offset(852)]] float screenFidelity;
    [[vk::offset(856)]] uint _pad856;
    [[vk::offset(860)]] uint _pad860;
    [[vk::offset(864)]] uint4 shadeDefault;
    [[vk::offset(880)]] float4 shadeDemand;
    [[vk::offset(896)]] uint4 shadeIdentity[2];
    [[vk::offset(928)]] uint shadePrograms;
    [[vk::offset(932)]] uint shadowFadeCount;
    [[vk::offset(936)]] uint shadowSlotCount;
    [[vk::offset(940)]] uint _pad940;
    [[vk::offset(944)]] int4 shadowSlots;
    [[vk::offset(960)]] float skyPeakLuminance;
    [[vk::offset(964)]] uint _pad964;
    [[vk::offset(968)]] uint _pad968;
    [[vk::offset(972)]] uint _pad972;
    [[vk::offset(976)]] float3 stampFade;
    [[vk::offset(988)]] float tanHalfFieldOfView;
    [[vk::offset(992)]] uint temporal;
    [[vk::offset(996)]] uint _pad996;
    [[vk::offset(1000)]] uint _pad1000;
    [[vk::offset(1004)]] uint _pad1004;
    [[vk::offset(1008)]] float4 toonBands;
    [[vk::offset(1024)]] float4 toonInk;
    [[vk::offset(1040)]] float4 toonOutline;
    [[vk::offset(1056)]] float4 toonShadow;
    [[vk::offset(1072)]] uint viewBase;
    [[vk::offset(1076)]] uint _pad1076;
    [[vk::offset(1080)]] uint _pad1080;
    [[vk::offset(1084)]] uint _pad1084;
    [[vk::offset(1088)]] float3 viewForward;
    [[vk::offset(1100)]] uint _pad1100;
    [[vk::offset(1104)]] float3 viewPosition;
    [[vk::offset(1116)]] uint _pad1116;
    [[vk::offset(1120)]] float3 viewRight;
    [[vk::offset(1132)]] uint _pad1132;
    [[vk::offset(1136)]] float3 viewUp;
    [[vk::offset(1148)]] uint viewportCount;
    [[vk::offset(1152)]] uint workCounterRow;
    [[vk::offset(1156)]] uint workCounterRowDetail;
    [[vk::offset(1160)]] uint _pad1160;
    [[vk::offset(1164)]] uint _pad1164;
    [[vk::offset(1168)]] float4 worldLanes;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfWorldPass> passGroupIsa0E64E4DD : register(b0, space3);
#define passGroup passGroupIsa0E64E4DD
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t1, space3);
[[vk::binding(2, 3)]] RWStructuredBuffer<uint> sdfVisibilityRecordsRW : register(u2, space3);
[[vk::binding(3, 3)]] RWStructuredBuffer<float> reactivityRW : register(u3, space3);
[[vk::binding(4, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> output : register(u4, space3);
[[vk::binding(5, 3)]] Texture2D<float4> screenSources[32] : register(t5, space3);
[[vk::binding(37, 3)]] Texture2D<float4> meshVisibility : register(t37, space3);
[[vk::binding(38, 3)]] Texture2D<float4> meshShadowNear : register(t38, space3);
[[vk::binding(39, 3)]] Texture2D<float4> meshShadowFar : register(t39, space3);
[[vk::binding(40, 3)]] Texture2D<float4> meshShadowSecond : register(t40, space3);
[[vk::binding(41, 3)]] Texture2D<float4> meshShadowNearExit : register(t41, space3);
[[vk::binding(42, 3)]] Texture2D<float4> meshShadowFarExit : register(t42, space3);
[[vk::binding(43, 3)]] Texture2D<float4> meshShadowSecondExit : register(t43, space3);
[[vk::binding(44, 3)]] RWStructuredBuffer<uint> workCounters : register(u44, space3);
[[vk::binding(45, 3)]] StructuredBuffer<uint> meshShadowNearOptical : register(t45, space3);
[[vk::binding(46, 3)]] StructuredBuffer<uint> meshShadowFarOptical : register(t46, space3);
[[vk::binding(47, 3)]] StructuredBuffer<uint> meshShadowSecondOptical : register(t47, space3);
[[vk::binding(48, 3)]] StructuredBuffer<uint> sdfMeshPyramid : register(t48, space3);
[[vk::binding(49, 3)]] RWStructuredBuffer<uint> sdfMeshPyramidRW : register(u49, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfWorldPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfWorldPushedIndex> pushedIndex : register(b0, space4);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, sky evaluations, sky hashes,
// sky texture loads, shape evaluations and shape gradients, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 14u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShapesWord = 10u;
static const uint PuckWorkGradientsWord = 12u;
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

#endif // PUCK_SHADER_INTERFACE_SDF_WORLD
