// Generated from shader interface 'sdf-sky-environment' (sha256/b8859fca70024dac7ef4329377a56eb4c0171b21a1abcb93d0c30ad10880c60c). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_SKY_ENVIRONMENT
#define PUCK_SHADER_INTERFACE_SDF_SKY_ENVIRONMENT

struct SdfSkyBlock {
    [[vk::offset(0)]] uint AirFlags;
    [[vk::offset(4)]] uint StopCount;
    [[vk::offset(8)]] uint SoftboxCount;
    [[vk::offset(12)]] int DiscLight;
    [[vk::offset(16)]] float3 DiscDirection;
    [[vk::offset(28)]] float DiscIntensity;
    [[vk::offset(32)]] float DiscExponent;
    [[vk::offset(36)]] uint AirLightCount;
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
    [[vk::offset(172)]] float FogExtinction;
    [[vk::offset(176)]] float3 FogColor;
    [[vk::offset(188)]] float FogBase;
    [[vk::offset(192)]] float FogFalloff;
    [[vk::offset(196)]] float HazeExtinction;
    [[vk::offset(200)]] float HazeBase;
    [[vk::offset(204)]] float HazeFalloff;
    [[vk::offset(208)]] float3 MediumColor;
    [[vk::offset(220)]] float MediumExtinction;
    [[vk::offset(224)]] float MediumSurface;
    [[vk::offset(228)]] float HazeAnisotropy;
    [[vk::offset(232)]] uint _pad232;
    [[vk::offset(236)]] uint _pad236;
    [[vk::offset(240)]] float3 AirLight0Direction;
    [[vk::offset(252)]] uint _pad252;
    [[vk::offset(256)]] float3 AirLight0Radiance;
    [[vk::offset(268)]] uint _pad268;
    [[vk::offset(272)]] float3 AirLight1Direction;
    [[vk::offset(284)]] uint _pad284;
    [[vk::offset(288)]] float3 AirLight1Radiance;
    [[vk::offset(300)]] uint _pad300;
    [[vk::offset(304)]] float3 AirLight2Direction;
    [[vk::offset(316)]] uint _pad316;
    [[vk::offset(320)]] float3 AirLight2Radiance;
    [[vk::offset(332)]] uint _pad332;
    [[vk::offset(336)]] float3 AirLight3Direction;
    [[vk::offset(348)]] uint _pad348;
    [[vk::offset(352)]] float3 AirLight3Radiance;
    [[vk::offset(364)]] uint _pad364;
};

struct SdfSkyStop {
    [[vk::offset(0)]] float3 Color;
    [[vk::offset(12)]] float Elevation;
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
[[vk::binding(0, 0)]] ConstantBuffer<SdfSkyEnvironmentFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct SdfSkyEnvironmentPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] uint workCounterRow;
    [[vk::offset(12)]] uint workCounterRowDetail;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfSkyEnvironmentPass> passGroupIsa506E1C21 : register(b0, space3);
#define passGroup passGroupIsa506E1C21
[[vk::binding(1, 3)]] StructuredBuffer<SdfSkyBlock> sdfSkyLayout700384204459cafc8ea769a9caa5c1b1600eed679e2b1f6a1a2cb1d3c9bbd3e5 : register(t1, space3);
#define sdfSky sdfSkyLayout700384204459cafc8ea769a9caa5c1b1600eed679e2b1f6a1a2cb1d3c9bbd3e5
[[vk::binding(2, 3)]] StructuredBuffer<SdfSkyStop> sdfSkyStopsLayoutc67b283a50546f3478912c5a37ff211db26fe56f6c28a0d47e41ba92e1238224 : register(t2, space3);
#define sdfSkyStops sdfSkyStopsLayoutc67b283a50546f3478912c5a37ff211db26fe56f6c28a0d47e41ba92e1238224
[[vk::binding(3, 3)]] RWStructuredBuffer<uint2> sdfSkyEnvironmentRW : register(u3, space3);
[[vk::binding(4, 3)]] RWStructuredBuffer<float4> sdfSkyCoefficientsRW : register(u4, space3);
[[vk::binding(5, 3)]] RWStructuredBuffer<uint> workCounters : register(u5, space3);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, sky evaluations, sky hashes,
// sky texture loads, then six shadow-slot step counts, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 22u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShadowWord = 10u;
static const uint PuckWorkShadowSlots = 6u;
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
// The slot is uniform across the wave. Stable slots precede active handoffs in the shadow pass's row.
void puckCountShadow(uint slot, uint steps) {
    uint waveSteps = WaveActiveSum(steps);

    if ((slot < PuckWorkShadowSlots) && WaveIsFirstLane()) {
        puckAddWork(((passGroup.workCounterRow * PuckWorkRowWords) + PuckWorkShadowWord + (slot * 2u)), waveSteps);
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
