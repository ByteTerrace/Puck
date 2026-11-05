// Generated from shader interface 'sdf-probe-relight' (sha256/b08062c3bead97e9cec62614f3ba13c10adaf4ad48c9568ede073b1496b08819). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_PROBE_RELIGHT
#define PUCK_SHADER_INTERFACE_SDF_PROBE_RELIGHT

// The Pass group: descriptor set 3, register space 3.
[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfProbeScene : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<float4> sdfProbeOrigins : register(t2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<float4> sdfProbeDirections : register(t3, space3);
[[vk::binding(4, 3)]] StructuredBuffer<float4> sdfProbeLighting : register(t4, space3);
[[vk::binding(5, 3)]] StructuredBuffer<uint> sdfProbeInput : register(t5, space3);
[[vk::binding(6, 3)]] StructuredBuffer<uint> sdfMeshIrradiance : register(t6, space3);
[[vk::binding(7, 3)]] RWStructuredBuffer<uint> sdfProbeHitsRW : register(u7, space3);

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

#endif // PUCK_SHADER_INTERFACE_SDF_PROBE_RELIGHT
