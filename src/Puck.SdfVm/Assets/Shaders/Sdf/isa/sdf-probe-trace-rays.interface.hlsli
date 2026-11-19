// Generated from shader interface 'sdf-probe-trace-rays' (sha256/9610ca2064e2e4bfe66f3938fa4b0f42da00a80c1f32592418d46c7e4655ee3f). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_PROBE_TRACE_RAYS
#define PUCK_SHADER_INTERFACE_SDF_PROBE_TRACE_RAYS

// The Pass group: descriptor set 3, register space 3.
[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfProbeScene : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<float4> sdfProbeOrigins : register(t2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<float4> sdfProbeDirections : register(t3, space3);
[[vk::binding(4, 3)]] RWStructuredBuffer<uint> sdfProbeHitsRW : register(u4, space3);
[[vk::binding(5, 3)]] RWStructuredBuffer<uint2> sdfProbeIdentitiesRW : register(u5, space3);
[[vk::binding(6, 3)]] RaytracingAccelerationStructure sdfProbeStructure : register(t6, space3);

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

#endif // PUCK_SHADER_INTERFACE_SDF_PROBE_TRACE_RAYS
