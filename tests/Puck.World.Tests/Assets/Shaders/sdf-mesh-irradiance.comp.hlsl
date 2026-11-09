// Runs the production pixel gather (sdfMeshProbesAt) over a published irradiance volume the law hands it: the volume's
// table words, its pass-block parameters as SdfFrameBlock packs them, and one receiver a thread.
[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfMeshIrradiance : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<float4> gatherReceivers : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<uint4> gatherParameters : register(t2, space3);
[[vk::binding(3, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> gatherResults : register(u3, space3);
struct GatherPass {
    int4 meshIrradianceLevels[3];
    float4 meshIrradianceSpacings[3];
    uint indirectSources;
    float4 indirectSourceGains;
};
static GatherPass passGroup;
float3 sdfSkyPhysicalIrradiance(float3 normal) { return 0.0; }
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-mesh-irradiance.hlsli"

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    [unroll] for (uint level = 0u; (level < 3u); level++) {
        passGroup.meshIrradianceLevels[level] = asint(gatherParameters[1u + level]);
        passGroup.meshIrradianceSpacings[level] = asfloat(gatherParameters[4u + level]);
    }
    passGroup.indirectSources = 0u;
    passGroup.indirectSourceGains = 0.0;

    SdfMeshProbes probes = sdfMeshProbesAt(gatherReceivers[2u * id.x].xyz, gatherReceivers[(2u * id.x) + 1u].xyz);
    SdfMeshProbeStages stages = sdfMeshProbeStagesOf(probes);
    float slots[16];
    [unroll] for (uint corner = 0u; (corner < 16u); corner++) {
        // A probe index is exact in a float below 2^24; -1 marks a slot that does not answer.
        uint address;
        slots[corner] = (sdfMeshProbeAt(probes, stages, corner, address) ? (float)(address / SDF_MESH_IRRADIANCE_PROBE_WORDS) : -1.0);
    }
    [unroll] for (uint row = 0u; (row < 4u); row++) {
        gatherResults[uint2(id.x, row)] = float4(probes.weight[4u * row], probes.weight[(4u * row) + 1u], probes.weight[(4u * row) + 2u], probes.weight[(4u * row) + 3u]);
        gatherResults[uint2(id.x, 4u + row)] = float4(slots[4u * row], slots[(4u * row) + 1u], slots[(4u * row) + 2u], slots[(4u * row) + 3u]);
    }
    // The law's light 0 is its sun, a shadowing directional a view with no cascade reads from the probes.
    gatherResults[uint2(id.x, 8u)] = float4(probes.share, (probes.measured ? 1.0 : 0.0), (probes.remainderMeasured ? 1.0 : 0.0), sdfMeshLightVisibility(probes, 0u));
}
