// Generated from shader interface 'sdf-glass' (sha256/169fa96417de2b605495442e012d8bbe58351c5c9fe2eaef0bf5f7ba9a48bad4). Regenerate it from the interface; never edit it.
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
    [[vk::offset(140)]] float FogAnisotropy;
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
[[vk::binding(19, 1)]] StructuredBuffer<SdfSkyBlock> sdfSkyLayoutfdef00bee49d9d72862c99f6620825913262775e3be134143ddefc291414f0b1 : register(t19, space1);
#define sdfSky sdfSkyLayoutfdef00bee49d9d72862c99f6620825913262775e3be134143ddefc291414f0b1
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
    [[vk::offset(196)]] uint _pad196;
    [[vk::offset(200)]] uint2 glassJobs;
    [[vk::offset(208)]] uint gridFlags;
    [[vk::offset(212)]] float gridLineWidth;
    [[vk::offset(216)]] uint _pad216;
    [[vk::offset(220)]] uint _pad220;
    [[vk::offset(224)]] float4 gridObjectFrame;
    [[vk::offset(240)]] float3 gridObjectOrigin;
    [[vk::offset(252)]] float gridObjectPatchRadius;
    [[vk::offset(256)]] float3 gridObjectPitch;
    [[vk::offset(268)]] float gridPlaneY;
    [[vk::offset(272)]] float4 gridWorldFrame;
    [[vk::offset(288)]] float3 gridWorldOrigin;
    [[vk::offset(300)]] uint _pad300;
    [[vk::offset(304)]] float3 gridWorldPitch;
    [[vk::offset(316)]] uint historyFrames;
    [[vk::offset(320)]] uint2 imageExtent;
    [[vk::offset(328)]] uint _pad328;
    [[vk::offset(332)]] uint _pad332;
    [[vk::offset(336)]] float4 indirectApply;
    [[vk::offset(352)]] float indirectContact;
    [[vk::offset(356)]] uint _pad356;
    [[vk::offset(360)]] uint _pad360;
    [[vk::offset(364)]] uint _pad364;
    [[vk::offset(368)]] float4 indirectSourceGains;
    [[vk::offset(384)]] uint indirectSources;
    [[vk::offset(388)]] uint inkEdges;
    [[vk::offset(392)]] float2 jitter;
    [[vk::offset(400)]] uint lightCount;
    [[vk::offset(404)]] uint _pad404;
    [[vk::offset(408)]] uint _pad408;
    [[vk::offset(412)]] uint _pad412;
    [[vk::offset(416)]] uint4 lightMask;
    [[vk::offset(432)]] float lightScale;
    [[vk::offset(436)]] float localAdaptation;
    [[vk::offset(440)]] uint meshDraws;
    [[vk::offset(444)]] uint _pad444;
    [[vk::offset(448)]] uint2 meshIrradianceGeometry;
    [[vk::offset(456)]] uint _pad456;
    [[vk::offset(460)]] uint _pad460;
    [[vk::offset(464)]] int4 meshIrradianceLevels[3];
    [[vk::offset(512)]] float4 meshIrradianceSpacings[3];
    [[vk::offset(560)]] float4 meshShadowCascades[16];
    [[vk::offset(816)]] uint meshShadowCount;
    [[vk::offset(820)]] uint _pad820;
    [[vk::offset(824)]] uint _pad824;
    [[vk::offset(828)]] uint _pad828;
    [[vk::offset(832)]] float4 meshShadowParameters;
    [[vk::offset(848)]] uint4 meshShadowRoles;
    [[vk::offset(864)]] float4 meshShadowSlopes;
    [[vk::offset(880)]] uint4 meshShadowTexels;
    [[vk::offset(896)]] float4 meterLight[6];
    [[vk::offset(992)]] float meterLuminance;
    [[vk::offset(996)]] float nearDistance;
    [[vk::offset(1000)]] uint occluderCount;
    [[vk::offset(1004)]] uint _pad1004;
    [[vk::offset(1008)]] uint4 occluderMask;
    [[vk::offset(1024)]] uint3 poseOverlay;
    [[vk::offset(1036)]] uint _pad1036;
    [[vk::offset(1040)]] float4 previousView[6];
    [[vk::offset(1136)]] uint reflectionSteps;
    [[vk::offset(1140)]] uint screenCount;
    [[vk::offset(1144)]] float screenFidelity;
    [[vk::offset(1148)]] uint screenReflections;
    [[vk::offset(1152)]] float4 screenWindows[128];
    [[vk::offset(3200)]] uint4 shadeBinWidths;
    [[vk::offset(3216)]] uint4 shadeDefault;
    [[vk::offset(3232)]] float4 shadeDemand;
    [[vk::offset(3248)]] uint4 shadeIdentity[2];
    [[vk::offset(3280)]] uint shadePrograms;
    [[vk::offset(3284)]] uint shadowFadeCount;
    [[vk::offset(3288)]] uint _pad3288;
    [[vk::offset(3292)]] uint _pad3292;
    [[vk::offset(3296)]] int4 shadowRoles;
    [[vk::offset(3312)]] uint shadowSlotCount;
    [[vk::offset(3316)]] uint _pad3316;
    [[vk::offset(3320)]] uint _pad3320;
    [[vk::offset(3324)]] uint _pad3324;
    [[vk::offset(3328)]] int4 shadowSlots;
    [[vk::offset(3344)]] float skyPeakLuminance;
    [[vk::offset(3348)]] uint _pad3348;
    [[vk::offset(3352)]] uint _pad3352;
    [[vk::offset(3356)]] uint _pad3356;
    [[vk::offset(3360)]] float3 stampFade;
    [[vk::offset(3372)]] float tanHalfFieldOfView;
    [[vk::offset(3376)]] uint temporal;
    [[vk::offset(3380)]] float toonMode;
    [[vk::offset(3384)]] uint viewBase;
    [[vk::offset(3388)]] uint _pad3388;
    [[vk::offset(3392)]] float3 viewForward;
    [[vk::offset(3404)]] uint _pad3404;
    [[vk::offset(3408)]] float3 viewPosition;
    [[vk::offset(3420)]] uint _pad3420;
    [[vk::offset(3424)]] float3 viewRight;
    [[vk::offset(3436)]] uint _pad3436;
    [[vk::offset(3440)]] float3 viewUp;
    [[vk::offset(3452)]] uint viewportCount;
    [[vk::offset(3456)]] uint4 viewsRate;
    [[vk::offset(3472)]] uint workCounterRow;
    [[vk::offset(3476)]] uint workCounterRowDetail;
    [[vk::offset(3480)]] uint _pad3480;
    [[vk::offset(3484)]] uint _pad3484;
    [[vk::offset(3488)]] float4 worldLanes;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfGlassPass> passGroupIsa98CC9CA9 : register(b0, space3);
#define passGroup passGroupIsa98CC9CA9
[[vk::binding(1, 3)]] Texture2D<float4> lit : register(t1, space3);
[[vk::binding(2, 3)]] Texture2D<float4> glassVisibility : register(t2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t3, space3);
[[vk::binding(4, 3)]] StructuredBuffer<uint> sdfGlassSums : register(t4, space3);
[[vk::binding(5, 3)]] RWStructuredBuffer<float> reactivityRW : register(u5, space3);
[[vk::binding(6, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> output : register(u6, space3);
[[vk::binding(7, 3)]] StructuredBuffer<uint> sdfGlassProbeHistory : register(t7, space3);
[[vk::binding(8, 3)]] RWStructuredBuffer<uint> sdfGlassProbeHistoryRW : register(u8, space3);
[[vk::binding(9, 3)]] RWStructuredBuffer<uint> sdfGlassTilesRW : register(u9, space3);
[[vk::binding(10, 3)]] RWStructuredBuffer<uint> sdfGlassSumsClearRW : register(u10, space3);
[[vk::binding(11, 3)]] RWStructuredBuffer<uint> sdfGlassArgumentsRW : register(u11, space3);
[[vk::binding(12, 3)]] RWStructuredBuffer<uint> workCounters : register(u12, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfGlassPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfGlassPushedIndex> pushedIndex : register(b0, space4);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, sky evaluations, sky hashes,
// sky texture loads, shape evaluations, shape gradients, rejected mesh hits, light visits, face light visits, shade
// program operations and environment lookups, as a 64-bit count in two words, low word first. An interface
// declaring no work counters declares the same functions empty.
static const uint PuckWorkRowWords = 24u;
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
static const uint PuckWorkFaceLightsWord = 18u;
static const uint PuckWorkShadeOperationsWord = 20u;
static const uint PuckWorkEnvironmentLookupsWord = 22u;
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
// Adds the shade program operations and cached environment lookups a lane's shading made to its pass's row: both
// sit in divergent shading, so the lane accumulates them and the wave sums them here, and the first lane adds the
// sums. Every lane of the wave must reach the call.
void puckCountShading(uint operations, uint lookups) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
    uint waveOperations = WaveActiveSum(operations);
    uint waveLookups = WaveActiveSum(lookups);

    if (WaveIsFirstLane()) {
        puckAddRowWork(PuckWorkShadeOperationsWord, waveOperations);
        puckAddRowWork(PuckWorkEnvironmentLookupsWord, waveLookups);
    }
}
// Adds the lights a lane's shading walks visited, and the face lights among them, to its pass's row: the wave
// sums its lanes and the first adds the sums. Every lane of the wave must reach the call.
void puckCountLights(uint visits, uint faces) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
    uint waveVisits = WaveActiveSum(visits);
    uint waveFaces = WaveActiveSum(faces);

    if (WaveIsFirstLane()) {
        puckAddRowWork(PuckWorkLightsWord, waveVisits);
        puckAddRowWork(PuckWorkFaceLightsWord, waveFaces);
    }
}

#endif // PUCK_SHADER_INTERFACE_SDF_GLASS
