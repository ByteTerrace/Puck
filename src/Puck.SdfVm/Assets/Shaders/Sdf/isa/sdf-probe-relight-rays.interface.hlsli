// Generated from shader interface 'sdf-probe-relight-rays' (sha256/62c054dc9630182531951ee5bd90ea10b2beff1b50d12db94a9b323356e38823). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_PROBE_RELIGHT_RAYS
#define PUCK_SHADER_INTERFACE_SDF_PROBE_RELIGHT_RAYS

// The Pass group: descriptor set 3, register space 3.
[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfProbeScene : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<float4> sdfProbeOrigins : register(t2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<float4> sdfProbeDirections : register(t3, space3);
[[vk::binding(4, 3)]] StructuredBuffer<float4> sdfProbeLighting : register(t4, space3);
[[vk::binding(5, 3)]] StructuredBuffer<uint> sdfProbeInput : register(t5, space3);
[[vk::binding(6, 3)]] StructuredBuffer<uint> sdfMeshIrradiance : register(t6, space3);
[[vk::binding(7, 3)]] RWStructuredBuffer<uint> sdfProbeHitsRW : register(u7, space3);
[[vk::binding(8, 3)]] RWStructuredBuffer<uint> sdfProbeWorkRW : register(u8, space3);
[[vk::binding(9, 3)]] StructuredBuffer<uint> sdfProbeWork : register(t9, space3);
[[vk::binding(10, 3)]] RWStructuredBuffer<uint> sdfProbeCacheRW : register(u10, space3);
[[vk::binding(11, 3)]] StructuredBuffer<uint> sdfProbeCache : register(t11, space3);
[[vk::binding(12, 3)]] RaytracingAccelerationStructure sdfProbeStructure : register(t12, space3);

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
void puckCountSky(uint evaluations, uint loads) {
}

#endif // PUCK_SHADER_INTERFACE_SDF_PROBE_RELIGHT_RAYS
