// Generated from shader interface 'sdf-optics' (sha256/e165ad8f96d53db0d23fe04e33b1e55739d14f84800fb263220474ea8ee6be47). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_OPTICS
#define PUCK_SHADER_INTERFACE_SDF_OPTICS

// The Frame group: descriptor set 0, register space 0.
struct SdfOpticsFrame {
    [[vk::offset(0)]] float2 pointer;
    [[vk::offset(8)]] uint2 tick;
    [[vk::offset(16)]] float time;
    [[vk::offset(20)]] float timeDelta;
    [[vk::offset(24)]] uint frame;
    [[vk::offset(28)]] uint tickRate;
    [[vk::offset(32)]] uint pointerDown;
};
[[vk::binding(0, 0)]] ConstantBuffer<SdfOpticsFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct SdfOpticsPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float bloomCutoff;
    [[vk::offset(12)]] float bloomMix;
    [[vk::offset(16)]] float bloomRadius;
    [[vk::offset(20)]] float bloomScatter;
    [[vk::offset(24)]] uint displayEncode;
    [[vk::offset(28)]] float displayFidelity;
    [[vk::offset(32)]] float displaySharpness;
    [[vk::offset(36)]] uint _pad36;
    [[vk::offset(40)]] uint _pad40;
    [[vk::offset(44)]] uint _pad44;
    [[vk::offset(48)]] float4 toneShape;
    [[vk::offset(64)]] float toneToe;
    [[vk::offset(68)]] uint workCounterRow;
    [[vk::offset(72)]] uint workCounterRowDetail;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfOpticsPass> passGroupIsa499D5F44 : register(b0, space3);
#define passGroup passGroupIsa499D5F44
[[vk::binding(1, 3)]] Texture2D<float4> bloomSource : register(t1, space3);
[[vk::binding(2, 3)]] Texture2D<float4> bloomFine : register(t2, space3);
[[vk::binding(3, 3)]] SamplerState bloomSampler : register(s3, space3);
[[vk::binding(4, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> bloomOutput : register(u4, space3);
[[vk::binding(5, 3)]] RWStructuredBuffer<uint> workCounters : register(u5, space3);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, sky evaluations, sky hashes,
// sky texture loads, shape evaluations, shape gradients and rejected mesh hits, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 16u;
static const uint PuckWorkDisabledRow = 0xFFFFFFFFu;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShapesWord = 10u;
static const uint PuckWorkGradientsWord = 12u;
static const uint PuckWorkRejectedHitsWord = 14u;
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

#endif // PUCK_SHADER_INTERFACE_SDF_OPTICS
