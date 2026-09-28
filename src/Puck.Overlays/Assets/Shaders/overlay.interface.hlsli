// Generated from shader interface 'overlay' (sha256/fb4e212313e769fbff2cd2f519102d70ae461f87388b9c9e31ad49097894afe5). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_OVERLAY
#define PUCK_SHADER_INTERFACE_OVERLAY

// The Frame group: descriptor set 0, register space 0.
struct OverlayFrame {
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
[[vk::binding(0, 0)]] ConstantBuffer<OverlayFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct OverlayPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] uint _pad8;
    [[vk::offset(12)]] uint _pad12;
    [[vk::offset(16)]] float4 counts;
    [[vk::offset(32)]] float4 misc;
    [[vk::offset(48)]] float4 sdf;
    [[vk::offset(64)]] uint workCounterRow;
};
[[vk::binding(0, 3)]] ConstantBuffer<OverlayPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] Texture2D<float4> source : register(t1, space3);
[[vk::binding(2, 3)]] Texture2D<float4> frameSlot0 : register(t2, space3);
[[vk::binding(3, 3)]] Texture2D<float4> frameSlot1 : register(t3, space3);
[[vk::binding(4, 3)]] Texture2D<float4> frameSlot2 : register(t4, space3);
[[vk::binding(5, 3)]] Texture2D<float4> frameSlot3 : register(t5, space3);
[[vk::binding(6, 3)]] Texture2D<float4> frameSlot4 : register(t6, space3);
[[vk::binding(7, 3)]] Texture2D<float4> frameSlot5 : register(t7, space3);
[[vk::binding(8, 3)]] Texture2D<float4> frameSlot6 : register(t8, space3);
[[vk::binding(9, 3)]] Texture2D<float4> frameSlot7 : register(t9, space3);
[[vk::binding(10, 3)]] SamplerState linearSampler : register(s10, space3);
[[vk::binding(11, 3)]] ByteAddressBuffer overlayData : register(t11, space3);
[[vk::binding(12, 3)]] RWStructuredBuffer<uint> workCounters : register(u12, space3);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps then texels written, as a 64-bit count in
// two words, low word first. A kernel that also compiles under an interface declaring no work counters (a
// document pass's) counts inside #if defined(PUCK_WORK_COUNTERS).
#define PUCK_WORK_COUNTERS 1
static const uint PuckWorkRowWords = 4u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
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

#endif // PUCK_SHADER_INTERFACE_OVERLAY
