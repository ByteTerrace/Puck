// Runs the production pixel gather (sdfMeshProbesAt) over a published irradiance volume the law hands it: the volume's
// table words, its pass-block parameters as SdfFrameBlock packs them, and one receiver a thread.
[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfMeshIrradiance : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<float4> gatherReceivers : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<uint4> gatherParameters : register(t2, space3);
[[vk::binding(3, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> gatherResults : register(u3, space3);
struct GatherPass {
    uint4 meshIrradianceVolume;
    int4 meshIrradianceLevels[3];
    uint indirectSources;
    float4 indirectSourceGains;
};
static GatherPass passGroup;
float3 sdfSkyPhysicalIrradiance(float3 normal) { return 0.0; }
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-mesh-irradiance.hlsli"

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    passGroup.meshIrradianceVolume = gatherParameters[0];
    [unroll] for (uint level = 0u; (level < 3u); level++) {
        passGroup.meshIrradianceLevels[level] = asint(gatherParameters[1u + level]);
    }
    passGroup.indirectSources = 0u;
    passGroup.indirectSourceGains = 0.0;

    SdfMeshProbes probes = sdfMeshProbesAt(gatherReceivers[2u * id.x].xyz, gatherReceivers[(2u * id.x) + 1u].xyz);
    float slots[8];
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        // A slot index is exact in a float below 2^24; -1 marks a corner that does not answer.
        slots[corner] = (sdfMeshProbeAnswers(probes, corner) ? (float)(probes.address[corner] / SDF_MESH_IRRADIANCE_PROBE_WORDS) : -1.0);
    }
    gatherResults[uint2(id.x, 0u)] = float4(probes.weight[0], probes.weight[1], probes.weight[2], probes.weight[3]);
    gatherResults[uint2(id.x, 1u)] = float4(probes.weight[4], probes.weight[5], probes.weight[6], probes.weight[7]);
    gatherResults[uint2(id.x, 2u)] = float4(slots[0], slots[1], slots[2], slots[3]);
    gatherResults[uint2(id.x, 3u)] = float4(slots[4], slots[5], slots[6], slots[7]);
    gatherResults[uint2(id.x, 4u)] = float4((probes.measured ? 1.0 : 0.0), 0.0, 0.0, 0.0);
}
