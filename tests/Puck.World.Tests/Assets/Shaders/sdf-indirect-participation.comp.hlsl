#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
// The ordinary field, the indirect sample and gradient, then the ordinary field again are the four queries of one
// procedure, so the probe inlines the interpreter once: the ordinary reads are plain field queries.
#define SDF_INDIRECT_PROCS_CUSTOM
#define SDF_INDIRECT_PROC_KERNEL
#define SDF_INDIRECT_PLAIN_QUERIES
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-field.hlsli"

[[vk::binding(126, 3)]] StructuredBuffer<float4> cases : register(t126, space3);
[[vk::binding(127, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> results : register(u127, space3);
struct ProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<ProbeIndex> probeIndex : register(b0, space4);

struct ParticipationProbeProc {
    float3 samplePosition;
    float ordinary;
    float indirect;
    float3 gradient;
    bool masksRestored;
    float restored;
    uint phase;
};
static ParticipationProbeProc participationProbeProc = (ParticipationProbeProc)0;

uint sdfIndirectKernelStep() {
    float3 samplePosition = participationProbeProc.samplePosition;
    uint phase = participationProbeProc.phase;
    participationProbeProc.phase++;
    if (phase == 0u) {
        return sdfIndirectAskPlain(samplePosition, SDF_INSTANCE_MASK_ALL);
    }
    if (phase == 1u) {
        participationProbeProc.ordinary = sdfIndirectReply.distance;
        // Indirect policy must override the separately suppressed direct-shadow bit, then restore the caller.
        sdfShadowParticipationActive = true;
        // A full-field distance and gradient must also ignore an outer lighting mask without destroying it.
        sdfShadowMaskWords[0] = 0u;
        sdfAmbientMaskWords[0] = 0u;
        GroupMemoryBarrierWithGroupSync();
        sdfShadowMaskActive = true;
        sdfAmbientMaskActive = true;
        return sdfIndirectAsk(samplePosition, SDF_INSTANCE_MASK_ALL);
    }
    if (phase == 2u) {
        participationProbeProc.indirect = sdfIndirectReply.distance;
        return sdfIndirectAskGradient(samplePosition + float3(1.0, 0.0, 0.0));
    }
    if (phase == 3u) {
        participationProbeProc.gradient = sdfIndirectReplyGradient;
        participationProbeProc.masksRestored = sdfShadowMaskActive && sdfAmbientMaskActive;
        sdfShadowMaskActive = false;
        sdfAmbientMaskActive = false;
        sdfShadowParticipationActive = false;
        return sdfIndirectAskPlain(samplePosition, SDF_INSTANCE_MASK_ALL);
    }
    participationProbeProc.restored = sdfIndirectReply.distance;
    return SdfIndirectStepReturn;
}
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-procedures.hlsli"

[numthreads(1, 1, 1)]
void CSMain() {
    sdfProgramLayout = sdfLoadProgramLayout();
    participationProbeProc.samplePosition = cases[0].xyz;
    participationProbeProc.phase = 0u;
    sdfIndirectRun(SdfIndirectProcKernel);
    results[uint2(probeIndex.index, 0u)] = float4(participationProbeProc.ordinary, participationProbeProc.indirect,
        participationProbeProc.masksRestored ? participationProbeProc.restored : SDF_FAR_DISTANCE, participationProbeProc.gradient.x);
}