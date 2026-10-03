// Generated from shader interface 'sdf-film-grain' (sha256/26adc9ddb6f1052f9f17641ab7271f8aa7831cfb68f3e7a47834433a4dda49f2). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_FILM_GRAIN
#define PUCK_SHADER_INTERFACE_SDF_FILM_GRAIN

// The Frame group: descriptor set 0, register space 0.
struct SdfFilmGrainFrame {
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
[[vk::binding(0, 0)]] ConstantBuffer<SdfFilmGrainFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct SdfFilmGrainPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] uint flickerHz;
    [[vk::offset(12)]] float intensity;
    [[vk::offset(16)]] uint seed;
    [[vk::offset(20)]] float size;
    [[vk::offset(24)]] uint workCounterRow;
    [[vk::offset(28)]] uint workCounterRowDetail;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfFilmGrainPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] Texture2D<float4> source : register(t1, space3);
[[vk::binding(2, 3)]] SamplerState sourceSampler : register(s2, space3);
[[vk::binding(3, 3)]] RWStructuredBuffer<uint> workCounters : register(u3, space3);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, sky evaluations, sky hashes,
// sky texture loads, then six shadow-slot step counts, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 28u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShadowWord = 10u;
static const uint PuckWorkShadowSlots = 6u;
static const uint PuckWorkIndirectWord = 22u;
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
// Each invocation names its level or proof detail; those rows sum into the pass once at readback.
void puckCountIndirect(uint detail, uint hits, uint samples, uint unresolved) {
    uint row = ((passGroup.workCounterRowDetail == 0u)
        ? passGroup.workCounterRow
        : (passGroup.workCounterRowDetail + detail)) * PuckWorkRowWords;
    puckAddWork((row + PuckWorkIndirectWord), hits);
    puckAddWork((row + PuckWorkIndirectWord + 2u), samples);
    puckAddWork((row + PuckWorkIndirectWord + 4u), unresolved);
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

#endif // PUCK_SHADER_INTERFACE_SDF_FILM_GRAIN
