// Generated from shader interface 'sdf-glass' (sha256/a04af3d8d8bfc4b9a89bba0a97470ff4b79970980b952656038dc2584461349f). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_GLASS
#define PUCK_SHADER_INTERFACE_SDF_GLASS

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

struct SdfOccluder {
    [[vk::offset(0)]] float3 Center;
    [[vk::offset(12)]] float Strength;
    [[vk::offset(16)]] float3 AxisX;
    [[vk::offset(28)]] uint Shape;
    [[vk::offset(32)]] float3 AxisY;
    [[vk::offset(44)]] float Radius;
    [[vk::offset(48)]] float3 AxisZ;
    [[vk::offset(60)]] float Reach;
    [[vk::offset(64)]] float Shadow;
    [[vk::offset(68)]] float Softness;
    [[vk::offset(72)]] int OwnSlot;
    [[vk::offset(76)]] uint OwnSlots;
};

// The Frame group: descriptor set 0, register space 0.
struct SdfGlassFrame {
    [[vk::offset(0)]] float2 pointer;
    [[vk::offset(8)]] uint2 tick;
    [[vk::offset(16)]] float time;
    [[vk::offset(20)]] float timeDelta;
    [[vk::offset(24)]] uint frame;
    [[vk::offset(28)]] uint tickRate;
    [[vk::offset(32)]] uint pointerDown;
};
[[vk::binding(0, 0)]] ConstantBuffer<SdfGlassFrame> frameGroup : register(b0, space0);

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
[[vk::binding(17, 1)]] StructuredBuffer<uint> sdfMeshDraws : register(t17, space1);
[[vk::binding(18, 1)]] StructuredBuffer<SdfLight> sdfLightsLayout27ca437eafcfb2ef7e652cdc81f326face73209e6adffa61f04dffc997d7c8c5 : register(t18, space1);
#define sdfLights sdfLightsLayout27ca437eafcfb2ef7e652cdc81f326face73209e6adffa61f04dffc997d7c8c5
[[vk::binding(19, 1)]] StructuredBuffer<SdfSkyBlock> sdfSkyLayoutcf5989bcab395177650d42c028df69d633f031173a236e87e6f40e104d525aa9 : register(t19, space1);
#define sdfSky sdfSkyLayoutcf5989bcab395177650d42c028df69d633f031173a236e87e6f40e104d525aa9
[[vk::binding(20, 1)]] StructuredBuffer<SdfSkyLayer> sdfSkyLayersLayoutb8b05e94adeef57d901b9217964b288f590ab8e7f74318d31922800728b169a8 : register(t20, space1);
#define sdfSkyLayers sdfSkyLayersLayoutb8b05e94adeef57d901b9217964b288f590ab8e7f74318d31922800728b169a8
[[vk::binding(21, 1)]] StructuredBuffer<float4> sdfSkyCoefficients : register(t21, space1);
[[vk::binding(22, 1)]] StructuredBuffer<SdfShadowHandoff> sdfShadowHandoffsLayout981efb60b212c94dac2862f0fb486ea3aaed2504fac947a2a5d6ffc91403e13e : register(t22, space1);
#define sdfShadowHandoffs sdfShadowHandoffsLayout981efb60b212c94dac2862f0fb486ea3aaed2504fac947a2a5d6ffc91403e13e
[[vk::binding(23, 1)]] StructuredBuffer<uint2> sdfSkyEnvironment : register(t23, space1);
[[vk::binding(24, 1)]] StructuredBuffer<float4> sdfFieldEntries : register(t24, space1);
[[vk::binding(25, 1)]] StructuredBuffer<SdfOccluder> sdfOccludersLayout1ee5b9129421340ef17acf996aed0e8e75902434e1e044ca975870520f7526fb : register(t25, space1);
#define sdfOccluders sdfOccludersLayout1ee5b9129421340ef17acf996aed0e8e75902434e1e044ca975870520f7526fb

// The Pass group: descriptor set 3, register space 3.
struct SdfGlassPass {
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
    [[vk::offset(60)]] uint _pad60;
    [[vk::offset(64)]] float4 debugOptions;
    [[vk::offset(80)]] uint disableAmbientOcclusion;
    [[vk::offset(84)]] uint disableScreenLights;
    [[vk::offset(88)]] uint disableSoftShadows;
    [[vk::offset(92)]] uint _pad92;
    [[vk::offset(96)]] float2 displayPixels;
    [[vk::offset(104)]] float exposeShare;
    [[vk::offset(108)]] float exposure;
    [[vk::offset(112)]] float farDistance;
    [[vk::offset(116)]] uint _pad116;
    [[vk::offset(120)]] uint _pad120;
    [[vk::offset(124)]] uint _pad124;
    [[vk::offset(128)]] float4 fieldPlane;
    [[vk::offset(144)]] float4 fieldScope;
    [[vk::offset(160)]] float4 fieldStyle;
    [[vk::offset(176)]] float flowClock;
    [[vk::offset(180)]] uint _pad180;
    [[vk::offset(184)]] float2 frustumOffset;
    [[vk::offset(192)]] uint geometryOnly;
    [[vk::offset(196)]] uint gridFlags;
    [[vk::offset(200)]] float gridLineWidth;
    [[vk::offset(204)]] uint _pad204;
    [[vk::offset(208)]] float4 gridObjectFrame;
    [[vk::offset(224)]] float3 gridObjectOrigin;
    [[vk::offset(236)]] float gridObjectPatchRadius;
    [[vk::offset(240)]] float3 gridObjectPitch;
    [[vk::offset(252)]] float gridPlaneY;
    [[vk::offset(256)]] float4 gridWorldFrame;
    [[vk::offset(272)]] float3 gridWorldOrigin;
    [[vk::offset(284)]] uint _pad284;
    [[vk::offset(288)]] float3 gridWorldPitch;
    [[vk::offset(300)]] uint historyFrames;
    [[vk::offset(304)]] uint2 imageExtent;
    [[vk::offset(312)]] uint _pad312;
    [[vk::offset(316)]] uint _pad316;
    [[vk::offset(320)]] float4 indirectApply;
    [[vk::offset(336)]] float indirectContact;
    [[vk::offset(340)]] uint _pad340;
    [[vk::offset(344)]] uint _pad344;
    [[vk::offset(348)]] uint _pad348;
    [[vk::offset(352)]] float4 indirectSourceGains;
    [[vk::offset(368)]] uint indirectSources;
    [[vk::offset(372)]] uint inkEdges;
    [[vk::offset(376)]] float2 jitter;
    [[vk::offset(384)]] uint lightCount;
    [[vk::offset(388)]] uint _pad388;
    [[vk::offset(392)]] uint _pad392;
    [[vk::offset(396)]] uint _pad396;
    [[vk::offset(400)]] uint4 lightMask;
    [[vk::offset(416)]] float lightScale;
    [[vk::offset(420)]] float localAdaptation;
    [[vk::offset(424)]] uint meshDraws;
    [[vk::offset(428)]] uint _pad428;
    [[vk::offset(432)]] uint2 meshIrradianceGeometry;
    [[vk::offset(440)]] uint _pad440;
    [[vk::offset(444)]] uint _pad444;
    [[vk::offset(448)]] int4 meshIrradianceLevels[3];
    [[vk::offset(496)]] float4 meshIrradianceSpacings[3];
    [[vk::offset(544)]] float4 meshShadowCascades[16];
    [[vk::offset(800)]] uint meshShadowCount;
    [[vk::offset(804)]] uint _pad804;
    [[vk::offset(808)]] uint _pad808;
    [[vk::offset(812)]] uint _pad812;
    [[vk::offset(816)]] float4 meshShadowParameters;
    [[vk::offset(832)]] uint4 meshShadowRoles;
    [[vk::offset(848)]] float4 meshShadowSlopes;
    [[vk::offset(864)]] uint4 meshShadowTexels;
    [[vk::offset(880)]] float4 meterLight[6];
    [[vk::offset(976)]] float meterLuminance;
    [[vk::offset(980)]] float nearDistance;
    [[vk::offset(984)]] uint occluderCount;
    [[vk::offset(988)]] uint _pad988;
    [[vk::offset(992)]] uint4 occluderMask;
    [[vk::offset(1008)]] uint3 poseOverlay;
    [[vk::offset(1020)]] uint _pad1020;
    [[vk::offset(1024)]] float4 previousView[6];
    [[vk::offset(1120)]] uint reflectionSteps;
    [[vk::offset(1124)]] uint screenCount;
    [[vk::offset(1128)]] float screenFidelity;
    [[vk::offset(1132)]] uint screenReflections;
    [[vk::offset(1136)]] float4 screenWindows[128];
    [[vk::offset(3184)]] uint4 shadeBinWidths;
    [[vk::offset(3200)]] uint4 shadeDefault;
    [[vk::offset(3216)]] float4 shadeDemand;
    [[vk::offset(3232)]] uint4 shadeIdentity[2];
    [[vk::offset(3264)]] uint shadePrograms;
    [[vk::offset(3268)]] uint shadowFadeCount;
    [[vk::offset(3272)]] uint _pad3272;
    [[vk::offset(3276)]] uint _pad3276;
    [[vk::offset(3280)]] int4 shadowRoles;
    [[vk::offset(3296)]] uint shadowSlotCount;
    [[vk::offset(3300)]] uint _pad3300;
    [[vk::offset(3304)]] uint _pad3304;
    [[vk::offset(3308)]] uint _pad3308;
    [[vk::offset(3312)]] int4 shadowSlots;
    [[vk::offset(3328)]] float skyPeakLuminance;
    [[vk::offset(3332)]] uint _pad3332;
    [[vk::offset(3336)]] uint _pad3336;
    [[vk::offset(3340)]] uint _pad3340;
    [[vk::offset(3344)]] float3 stampFade;
    [[vk::offset(3356)]] float tanHalfFieldOfView;
    [[vk::offset(3360)]] uint temporal;
    [[vk::offset(3364)]] float toonMode;
    [[vk::offset(3368)]] uint viewBase;
    [[vk::offset(3372)]] uint _pad3372;
    [[vk::offset(3376)]] float3 viewForward;
    [[vk::offset(3388)]] uint _pad3388;
    [[vk::offset(3392)]] float3 viewPosition;
    [[vk::offset(3404)]] uint _pad3404;
    [[vk::offset(3408)]] float3 viewRight;
    [[vk::offset(3420)]] uint _pad3420;
    [[vk::offset(3424)]] float3 viewUp;
    [[vk::offset(3436)]] uint viewportCount;
    [[vk::offset(3440)]] uint4 viewsRate;
    [[vk::offset(3456)]] uint workCounterRow;
    [[vk::offset(3460)]] uint workCounterRowDetail;
    [[vk::offset(3464)]] uint _pad3464;
    [[vk::offset(3468)]] uint _pad3468;
    [[vk::offset(3472)]] float4 worldLanes;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfGlassPass> passGroupIsa737E25FB : register(b0, space3);
#define passGroup passGroupIsa737E25FB
[[vk::binding(1, 3)]] Texture2D<float4> lit : register(t1, space3);
[[vk::binding(2, 3)]] Texture2D<float4> glassVisibility : register(t2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t3, space3);
[[vk::binding(4, 3)]] StructuredBuffer<uint> sdfGlassSums : register(t4, space3);
[[vk::binding(5, 3)]] RWStructuredBuffer<float> reactivityRW : register(u5, space3);
[[vk::binding(6, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> output : register(u6, space3);
[[vk::binding(7, 3)]] StructuredBuffer<uint> sdfGlassProbeHistory : register(t7, space3);
[[vk::binding(8, 3)]] RWStructuredBuffer<uint> sdfGlassProbeHistoryRW : register(u8, space3);
[[vk::binding(9, 3)]] RWStructuredBuffer<uint> sdfGlassSkyFilterRW : register(u9, space3);
[[vk::binding(10, 3)]] RWStructuredBuffer<uint> workCounters : register(u10, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfGlassPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfGlassPushedIndex> pushedIndex : register(b0, space4);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, sky evaluations, sky hashes,
// sky texture loads, shape evaluations, shape gradients, rejected mesh hits and light visits, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 18u;
static const uint PuckWorkDisabledRow = 0xFFFFFFFFu;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShapesWord = 10u;
static const uint PuckWorkGradientsWord = 12u;
static const uint PuckWorkRejectedHitsWord = 14u;
static const uint PuckWorkLightsWord = 16u;
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
// Adds to one count of the pass's own row, or nothing while the pass counts nothing. The disabled row's scaled
// words wrap to indices just below the buffer, an out-of-bounds write a Vulkan device without robust buffer
// access faults on, so every lane's add to the pass row passes this door, which tests the row before scaling.
void puckAddRowWork(uint word, uint amount) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
    puckAddWork(((passGroup.workCounterRow * PuckWorkRowWords) + word), amount);
}
#if !defined(__SHADER_TARGET_STAGE) || (__SHADER_TARGET_STAGE == __SHADER_STAGE_COMPUTE)
groupshared uint puckWorkGroupALo;
groupshared uint puckWorkGroupAHi;
groupshared uint puckWorkGroupBLo;
groupshared uint puckWorkGroupBHi;
groupshared uint puckWorkGroupLeader;
// Reduces two counters across a compute thread group in groupshared memory so each group issues at most one
// global atomic per non-zero counter word instead of one per wave. Every thread in the group must reach the call.
void puckAddGroupWork(uint wordA, uint a, uint wordB, uint b) {
    uint waveA = WaveActiveSum(a);
    uint waveB = WaveActiveSum(b);

    if (WaveIsFirstLane()) {
        uint ignored;

        InterlockedExchange(puckWorkGroupALo, 0u, ignored);
        InterlockedExchange(puckWorkGroupAHi, 0u, ignored);
        InterlockedExchange(puckWorkGroupBLo, 0u, ignored);
        InterlockedExchange(puckWorkGroupBHi, 0u, ignored);
        InterlockedExchange(puckWorkGroupLeader, 0u, ignored);
    }
    GroupMemoryBarrierWithGroupSync();

    bool leader = false;

    if (WaveIsFirstLane()) {
        uint prev;

        InterlockedAdd(puckWorkGroupLeader, 1u, prev);
        leader = (prev == 0u);
        if (waveA != 0u) {
            InterlockedAdd(puckWorkGroupALo, waveA, prev);
            if (prev > (0xFFFFFFFFu - waveA)) {
                InterlockedAdd(puckWorkGroupAHi, 1u);
            }
        }
        if (waveB != 0u) {
            InterlockedAdd(puckWorkGroupBLo, waveB, prev);
            if (prev > (0xFFFFFFFFu - waveB)) {
                InterlockedAdd(puckWorkGroupBHi, 1u);
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    uint groupALo = 0u;
    uint groupAHi = 0u;
    uint groupBLo = 0u;
    uint groupBHi = 0u;

    if (leader) {
        groupALo = puckWorkGroupALo;
        groupAHi = puckWorkGroupAHi;
        groupBLo = puckWorkGroupBLo;
        groupBHi = puckWorkGroupBHi;
    }
    GroupMemoryBarrierWithGroupSync();

    if (leader) {
        puckAddWork(wordA, groupALo);
        if (groupAHi != 0u) {
            InterlockedAdd(workCounters[wordA + 1u], groupAHi);
        }
        puckAddWork(wordB, groupBLo);
        if (groupBHi != 0u) {
            InterlockedAdd(workCounters[wordB + 1u], groupBHi);
        }
    }
}
#endif
// Adds an invocation's march steps and texels written to its pass's row: skips uniformly when counting is
// disabled, otherwise reduces across the compute thread group in groupshared memory and adds once per group.
void puckCountWork(uint steps, uint texels) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
#if !defined(__SHADER_TARGET_STAGE) || (__SHADER_TARGET_STAGE == __SHADER_STAGE_COMPUTE)
    uint row = (passGroup.workCounterRow * PuckWorkRowWords);

    puckAddGroupWork((row + PuckWorkStepsWord), steps, (row + PuckWorkTexelsWord), texels);
#endif
}
// Named rows are disjoint from the plain pass row; the ledger sums both once the submission completes.
// The active lanes may name different rows (divergent layer evaluation): each distinct row among them in turn
// sums its lanes' counts across the wave, and that row's first lane adds each sum, so a wave adds once a row
// rather than once a lane.
void puckCountDetail(uint detail, uint steps, uint texels, uint evaluations, uint hashes, uint loads) {
    if (passGroup.workCounterRowDetail == 0u) {
        return;
    }
    [loop] for (;;) {
        if (WaveReadLaneFirst(detail) == detail) {
            uint waveSteps = WaveActiveSum(steps);
            uint waveTexels = WaveActiveSum(texels);
            uint waveEvaluations = WaveActiveSum(evaluations);
            uint waveHashes = WaveActiveSum(hashes);
            uint waveLoads = WaveActiveSum(loads);

            if (WaveIsFirstLane()) {
                uint row = ((passGroup.workCounterRowDetail + detail) * PuckWorkRowWords);
                puckAddWork((row + PuckWorkStepsWord), waveSteps);
                puckAddWork((row + PuckWorkTexelsWord), waveTexels);
                puckAddWork((row + PuckWorkSkyWord), waveEvaluations);
                puckAddWork((row + PuckWorkSkyHashesWord), waveHashes);
                puckAddWork((row + PuckWorkSkyTextureLoadsWord), waveLoads);
            }
            break;
        }
    }
}
void puckCountShapes(uint shapes, uint gradients) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
#if !defined(__SHADER_TARGET_STAGE) || (__SHADER_TARGET_STAGE == __SHADER_STAGE_COMPUTE)
    uint row = (passGroup.workCounterRow * PuckWorkRowWords);

    puckAddGroupWork((row + PuckWorkShapesWord), shapes, (row + PuckWorkGradientsWord), gradients);
#endif
}
// Adds a fragment's march steps and texels written to its pass's row: skips uniformly when counting is disabled,
// otherwise the wave sums its lanes that are not helper lanes, and the first of them adds each sum. A helper
// lane counts nothing and never adds, whether or not the backend lets it take part in wave operations, since its
// atomics have no effect.
void puckCountFragmentWork(uint steps, uint texels) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
    bool counting = !IsHelperLane();
    uint waveSteps = WaveActiveSum(counting ? steps : 0u);
    uint waveTexels = WaveActiveSum(counting ? texels : 0u);

    if (counting && (WavePrefixCountBits(counting) == 0u)) {
        puckAddRowWork(PuckWorkStepsWord, waveSteps);
        puckAddRowWork(PuckWorkTexelsWord, waveTexels);
    }
}
// Adds one hit the mesh hit door rejected to its pass's row, from the rejecting lane alone: a rejection is rare
// and may sit in divergent control flow, so it takes no wave operation.
void puckCountRejectedHit() {
    puckAddRowWork(PuckWorkRejectedHitsWord, 1u);
}
// Adds a lane's sky evaluations and sky texture loads to its pass's row, from the lane alone, since a sky lookup
// sits in divergent shading.
void puckCountSky(uint evaluations, uint loads) {
    puckAddRowWork(PuckWorkSkyWord, evaluations);
    puckAddRowWork(PuckWorkSkyTextureLoadsWord, loads);
}
// Adds the lights a lane's shading walks visited to its pass's row: the wave sums its lanes and the first adds
// the sum. Every lane of the wave must reach the call.
void puckCountLights(uint visits) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
    uint waveVisits = WaveActiveSum(visits);

    if (WaveIsFirstLane()) {
        puckAddRowWork(PuckWorkLightsWord, waveVisits);
    }
}

#endif // PUCK_SHADER_INTERFACE_SDF_GLASS
