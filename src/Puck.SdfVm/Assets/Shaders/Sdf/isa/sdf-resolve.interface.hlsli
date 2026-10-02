// Generated from shader interface 'sdf-resolve' (sha256/838aa6db38f4559da4182a89b02b3c82971e8ebec6dd56c3b12fd74b19a7c825). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_RESOLVE
#define PUCK_SHADER_INTERFACE_SDF_RESOLVE

struct SdfLight {
    [[vk::offset(0)]] float3 Direction;
    [[vk::offset(12)]] float Weight;
    [[vk::offset(16)]] float3 Color;
    [[vk::offset(28)]] uint Kind;
    [[vk::offset(32)]] float Param;
    [[vk::offset(36)]] uint Shadows;
    [[vk::offset(40)]] int DynamicSlot;
    [[vk::offset(44)]] uint _pad44;
};

struct SdfSkyBlock {
    [[vk::offset(0)]] float FogDensity;
    [[vk::offset(4)]] uint StopCount;
    [[vk::offset(8)]] uint SoftboxCount;
    [[vk::offset(12)]] int DiscLight;
    [[vk::offset(16)]] float3 DiscDirection;
    [[vk::offset(28)]] float DiscIntensity;
    [[vk::offset(32)]] float DiscExponent;
    [[vk::offset(36)]] uint _pad36;
    [[vk::offset(40)]] float StarDensity;
    [[vk::offset(44)]] float StarBrightness;
    [[vk::offset(48)]] uint StarSeed;
    [[vk::offset(52)]] float TwinkleShare;
    [[vk::offset(56)]] float TwinkleDepth;
    [[vk::offset(60)]] float TwinklePhase;
    [[vk::offset(64)]] float3 CloudColor;
    [[vk::offset(76)]] float CloudCoverage;
    [[vk::offset(80)]] float CloudSoftness;
    [[vk::offset(84)]] float CloudScale;
    [[vk::offset(88)]] uint CloudSeed;
    [[vk::offset(92)]] float CloudCurl;
    [[vk::offset(96)]] float2 CloudDriftOffset;
    [[vk::offset(104)]] float2 CloudShearOffset;
    [[vk::offset(112)]] float3 CloudLightDirection;
    [[vk::offset(124)]] float CloudSpinAngle;
    [[vk::offset(128)]] float3 CloudLightColor;
    [[vk::offset(140)]] uint _pad140;
    [[vk::offset(144)]] float3 HorizonLow;
    [[vk::offset(156)]] uint _pad156;
    [[vk::offset(160)]] float3 HorizonHigh;
    [[vk::offset(172)]] uint _pad172;
};

struct SdfSkyStop {
    [[vk::offset(0)]] float3 Color;
    [[vk::offset(12)]] float Elevation;
};

struct SdfSoftbox {
    [[vk::offset(0)]] float3 Direction;
    [[vk::offset(12)]] float Weight;
    [[vk::offset(16)]] float3 Color;
    [[vk::offset(28)]] uint _pad28;
    [[vk::offset(32)]] float2 Size;
    [[vk::offset(40)]] float Blur;
    [[vk::offset(44)]] uint _pad44;
};

// The Frame group: descriptor set 0, register space 0.
struct SdfResolveFrame {
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
};
[[vk::binding(0, 0)]] ConstantBuffer<SdfResolveFrame> frameGroup : register(b0, space0);

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
[[vk::binding(25, 1)]] StructuredBuffer<SdfLight> sdfLightsLayoutf91ddf69c59767c161ac0442f7efd9e65f6d70617851d0196da811011d56f2e2 : register(t25, space1);
#define sdfLights sdfLightsLayoutf91ddf69c59767c161ac0442f7efd9e65f6d70617851d0196da811011d56f2e2
[[vk::binding(26, 1)]] StructuredBuffer<SdfSkyBlock> sdfSkyLayoutc21dea5f8b5a7c8005406de8a2810f1fa8c9cd0b443a9e283a9cb686059d22c6 : register(t26, space1);
#define sdfSky sdfSkyLayoutc21dea5f8b5a7c8005406de8a2810f1fa8c9cd0b443a9e283a9cb686059d22c6
[[vk::binding(27, 1)]] StructuredBuffer<SdfSkyStop> sdfSkyStopsLayoutc67b283a50546f3478912c5a37ff211db26fe56f6c28a0d47e41ba92e1238224 : register(t27, space1);
#define sdfSkyStops sdfSkyStopsLayoutc67b283a50546f3478912c5a37ff211db26fe56f6c28a0d47e41ba92e1238224
[[vk::binding(28, 1)]] StructuredBuffer<SdfSoftbox> sdfSoftboxesLayoutddae489dd1b4237e319f8128eb0f94690a24eb786baf81f815c4d4d43d5d55cf : register(t28, space1);
#define sdfSoftboxes sdfSoftboxesLayoutddae489dd1b4237e319f8128eb0f94690a24eb786baf81f815c4d4d43d5d55cf

// The Pass group: descriptor set 3, register space 3.
struct SdfResolvePass {
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
    [[vk::offset(120)]] uint gridFlags;
    [[vk::offset(124)]] float gridLineWidth;
    [[vk::offset(128)]] float4 gridObjectFrame;
    [[vk::offset(144)]] float3 gridObjectOrigin;
    [[vk::offset(156)]] float gridObjectPatchRadius;
    [[vk::offset(160)]] float3 gridObjectPitch;
    [[vk::offset(172)]] float gridPlaneY;
    [[vk::offset(176)]] float4 gridWorldFrame;
    [[vk::offset(192)]] float3 gridWorldOrigin;
    [[vk::offset(204)]] uint _pad204;
    [[vk::offset(208)]] float3 gridWorldPitch;
    [[vk::offset(220)]] uint historyFrames;
    [[vk::offset(224)]] uint2 imageExtent;
    [[vk::offset(232)]] uint instanceMaskWordCount;
    [[vk::offset(236)]] uint _pad236;
    [[vk::offset(240)]] float2 jitter;
    [[vk::offset(248)]] uint lightCount;
    [[vk::offset(252)]] uint meshDraws;
    [[vk::offset(256)]] float nearDistance;
    [[vk::offset(260)]] uint _pad260;
    [[vk::offset(264)]] uint _pad264;
    [[vk::offset(268)]] uint _pad268;
    [[vk::offset(272)]] float4 previousView[6];
    [[vk::offset(368)]] uint screenCount;
    [[vk::offset(372)]] float shadowDistanceScale;
    [[vk::offset(376)]] int shadowLight;
    [[vk::offset(380)]] float tanHalfFieldOfView;
    [[vk::offset(384)]] uint temporal;
    [[vk::offset(388)]] uint _pad388;
    [[vk::offset(392)]] uint2 tileGrid;
    [[vk::offset(400)]] float upscaleSharpness;
    [[vk::offset(404)]] uint viewBase;
    [[vk::offset(408)]] uint _pad408;
    [[vk::offset(412)]] uint _pad412;
    [[vk::offset(416)]] float3 viewForward;
    [[vk::offset(428)]] uint _pad428;
    [[vk::offset(432)]] float3 viewPosition;
    [[vk::offset(444)]] uint _pad444;
    [[vk::offset(448)]] float3 viewRight;
    [[vk::offset(460)]] uint _pad460;
    [[vk::offset(464)]] float3 viewUp;
    [[vk::offset(476)]] uint viewportCount;
    [[vk::offset(480)]] uint workCounterRow;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfResolvePass> passGroupIsaCD9F88B9 : register(b0, space3);
#define passGroup passGroupIsaCD9F88B9
[[vk::binding(1, 3)]] Texture2D<float4> currentColor : register(t1, space3);
[[vk::binding(2, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> output : register(u2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t3, space3);
[[vk::binding(4, 3)]] StructuredBuffer<uint> cullBounds : register(t4, space3);
[[vk::binding(5, 3)]] RWStructuredBuffer<uint> transportRW : register(u5, space3);
[[vk::binding(6, 3)]] StructuredBuffer<float> reactivity : register(t6, space3);
[[vk::binding(7, 3)]] Texture2D<float4> historyColor : register(t7, space3);
[[vk::binding(8, 3)]] StructuredBuffer<uint> historySurface : register(t8, space3);
[[vk::binding(9, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> historyColorRW : register(u9, space3);
[[vk::binding(10, 3)]] RWStructuredBuffer<uint> historySurfaceRW : register(u10, space3);
[[vk::binding(11, 3)]] RWStructuredBuffer<uint> workCounters : register(u11, space3);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, then sky evaluations, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 6u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
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
// Adds an invocation's sky evaluations to its pass's row: the wave sums them, and its first active lane adds the sum.
void puckCountSky(uint evaluations) {
    uint waveEvaluations = WaveActiveSum(evaluations);

    if (WaveIsFirstLane()) {
        puckAddWork(((passGroup.workCounterRow * PuckWorkRowWords) + PuckWorkSkyWord), waveEvaluations);
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

#endif // PUCK_SHADER_INTERFACE_SDF_RESOLVE
