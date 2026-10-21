// Generated from shader interface 'sdf-sky-environment' (sha256/63efa3db49ae15c0e2f332f5e9ab2c95fd7faf824945394879d0f6ef2d3a79a2). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_SKY_ENVIRONMENT
#define PUCK_SHADER_INTERFACE_SDF_SKY_ENVIRONMENT

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

// The Frame group: descriptor set 0, register space 0.
struct SdfSkyEnvironmentFrame {
    [[vk::offset(0)]] float2 pointer;
    [[vk::offset(8)]] uint2 tick;
    [[vk::offset(16)]] float time;
    [[vk::offset(20)]] float timeDelta;
    [[vk::offset(24)]] uint frame;
    [[vk::offset(28)]] uint tickRate;
    [[vk::offset(32)]] uint pointerDown;
};
[[vk::binding(0, 0)]] ConstantBuffer<SdfSkyEnvironmentFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct SdfSkyEnvironmentPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] uint screenEmissionMask;
    [[vk::offset(12)]] uint workCounterRow;
    [[vk::offset(16)]] uint workCounterRowDetail;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfSkyEnvironmentPass> passGroupIsaC9CF3F47 : register(b0, space3);
#define passGroup passGroupIsaC9CF3F47
[[vk::binding(1, 3)]] StructuredBuffer<SdfSkyBlock> sdfSkyLayoutcf5989bcab395177650d42c028df69d633f031173a236e87e6f40e104d525aa9 : register(t1, space3);
#define sdfSky sdfSkyLayoutcf5989bcab395177650d42c028df69d633f031173a236e87e6f40e104d525aa9
[[vk::binding(2, 3)]] StructuredBuffer<SdfSkyLayer> sdfSkyLayersLayoutb8b05e94adeef57d901b9217964b288f590ab8e7f74318d31922800728b169a8 : register(t2, space3);
#define sdfSkyLayers sdfSkyLayersLayoutb8b05e94adeef57d901b9217964b288f590ab8e7f74318d31922800728b169a8
[[vk::binding(3, 3)]] StructuredBuffer<float4> screenMappings : register(t3, space3);
[[vk::binding(4, 3)]] Texture2D<float4> screenSources[32] : register(t4, space3);
[[vk::binding(36, 3)]] SamplerState samplers[2] : register(s36, space3);
[[vk::binding(38, 3)]] StructuredBuffer<uint2> sdfSkyEnvironment : register(t38, space3);
[[vk::binding(39, 3)]] RWStructuredBuffer<uint2> sdfSkyEnvironmentRW : register(u39, space3);
[[vk::binding(40, 3)]] RWStructuredBuffer<float4> sdfSkyCoefficientsRW : register(u40, space3);
[[vk::binding(41, 3)]] RWStructuredBuffer<float4> sdfScreenEmissionRW : register(u41, space3);
[[vk::binding(42, 3)]] RWStructuredBuffer<uint> workCounters : register(u42, space3);

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

#endif // PUCK_SHADER_INTERFACE_SDF_SKY_ENVIRONMENT
