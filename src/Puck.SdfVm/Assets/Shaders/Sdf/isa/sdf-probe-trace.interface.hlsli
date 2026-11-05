// Generated from shader interface 'sdf-probe-trace' (sha256/5051a32e5761e009f5c35badc1189e9ba8c07d96e83cadfa61a81de10affeeb7). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_PROBE_TRACE
#define PUCK_SHADER_INTERFACE_SDF_PROBE_TRACE

// The Pass group: descriptor set 3, register space 3.
[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfProbeScene : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<float4> sdfProbeOrigins : register(t2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<float4> sdfProbeDirections : register(t3, space3);
[[vk::binding(4, 3)]] RWStructuredBuffer<uint> sdfProbeHitsRW : register(u4, space3);
[[vk::binding(5, 3)]] RWStructuredBuffer<uint2> sdfProbeIdentitiesRW : register(u5, space3);

// This interface declares no work counters, so its passes count nothing: the counting functions a kernel calls
// are declared empty, and a kernel written for a counting package compiles here unchanged.
void puckCountWork(uint steps, uint texels) {
}
void puckCountDetail(uint detail, uint steps, uint texels, uint evaluations, uint hashes, uint loads) {
}
void puckCountShapes(uint shapes, uint gradients) {
}
void puckCountFragmentWork(uint steps, uint texels) {
}

#endif // PUCK_SHADER_INTERFACE_SDF_PROBE_TRACE
