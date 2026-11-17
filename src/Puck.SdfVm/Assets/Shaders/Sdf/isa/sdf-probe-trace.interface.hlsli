// Generated from shader interface 'sdf-probe-trace' (sha256/4b9b9620cbc2daf64685b140f9ed1f600c868953c0fbc58b304507390b2308a4). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_PROBE_TRACE
#define PUCK_SHADER_INTERFACE_SDF_PROBE_TRACE

// The Pass group: descriptor set 3, register space 3.
[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfProbeScene : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<uint> sdfMeshSlabs[64] : register(t2, space3);
[[vk::binding(66, 3)]] StructuredBuffer<float4> sdfProbeOrigins : register(t66, space3);
[[vk::binding(67, 3)]] StructuredBuffer<float4> sdfProbeDirections : register(t67, space3);
[[vk::binding(68, 3)]] RWStructuredBuffer<uint> sdfProbeHitsRW : register(u68, space3);
[[vk::binding(69, 3)]] RWStructuredBuffer<uint2> sdfProbeIdentitiesRW : register(u69, space3);

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
void puckCountRejectedHit() {
}

#endif // PUCK_SHADER_INTERFACE_SDF_PROBE_TRACE
