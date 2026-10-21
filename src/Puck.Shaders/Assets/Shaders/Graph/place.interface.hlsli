// Generated from shader interface 'place' (sha256/88e8e8a5a36b2593a5362b3d878dddb4a123b300194171c210ecb61a5c0ac042). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_PLACE
#define PUCK_SHADER_INTERFACE_PLACE

// The Frame group: descriptor set 0, register space 0.
struct PlaceFrame {
    [[vk::offset(0)]] float2 pointer;
    [[vk::offset(8)]] uint2 tick;
    [[vk::offset(16)]] float time;
    [[vk::offset(20)]] float timeDelta;
    [[vk::offset(24)]] uint frame;
    [[vk::offset(28)]] uint tickRate;
    [[vk::offset(32)]] uint pointerDown;
};
[[vk::binding(0, 0)]] ConstantBuffer<PlaceFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct PlacePass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] uint compareMode;
    [[vk::offset(12)]] uint _pad12;
    [[vk::offset(16)]] float4 crop;
    [[vk::offset(32)]] float4 fill;
    [[vk::offset(48)]] uint letterbox;
    [[vk::offset(52)]] uint pixelated;
    [[vk::offset(56)]] uint _pad56;
    [[vk::offset(60)]] uint _pad60;
    [[vk::offset(64)]] float4 rect;
    [[vk::offset(80)]] uint sharpen;
    [[vk::offset(84)]] float sharpness;
    [[vk::offset(88)]] uint _pad88;
    [[vk::offset(92)]] uint _pad92;
    [[vk::offset(96)]] float4 surround;
    [[vk::offset(112)]] uint tonemap;
    [[vk::offset(116)]] float wipe;
    [[vk::offset(120)]] uint workCounterRow;
    [[vk::offset(124)]] uint workCounterRowDetail;
};
[[vk::binding(0, 3)]] ConstantBuffer<PlacePass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] Texture2D<float4> base : register(t1, space3);
[[vk::binding(2, 3)]] SamplerState baseSampler : register(s2, space3);
[[vk::binding(3, 3)]] Texture2D<float4> source : register(t3, space3);
[[vk::binding(4, 3)]] SamplerState sourceSampler : register(s4, space3);
[[vk::binding(5, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> destination : register(u5, space3);
[[vk::binding(6, 3)]] RWStructuredBuffer<uint> workCounters : register(u6, space3);

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

#endif // PUCK_SHADER_INTERFACE_PLACE
