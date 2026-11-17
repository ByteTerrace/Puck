// Generated from shader interface 'sdf-probe-relight' (sha256/809add80840fac4304d242f251ffc32efec604c3b7807b953b8509c1cc99c07d). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_PROBE_RELIGHT
#define PUCK_SHADER_INTERFACE_SDF_PROBE_RELIGHT

// The Pass group: descriptor set 3, register space 3.
[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfProbeScene : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<uint> sdfMeshSlabs[64] : register(t2, space3);
[[vk::binding(66, 3)]] StructuredBuffer<float4> sdfProbeOrigins : register(t66, space3);
[[vk::binding(67, 3)]] StructuredBuffer<float4> sdfProbeDirections : register(t67, space3);
[[vk::binding(68, 3)]] StructuredBuffer<float4> sdfProbeLighting : register(t68, space3);
[[vk::binding(69, 3)]] StructuredBuffer<uint> sdfProbeInput : register(t69, space3);
[[vk::binding(70, 3)]] StructuredBuffer<uint> sdfMeshIrradiance : register(t70, space3);
[[vk::binding(71, 3)]] RWStructuredBuffer<uint> sdfProbeHitsRW : register(u71, space3);
[[vk::binding(72, 3)]] RWStructuredBuffer<uint> sdfProbeWorkRW : register(u72, space3);
[[vk::binding(73, 3)]] StructuredBuffer<uint> sdfProbeWork : register(t73, space3);
[[vk::binding(74, 3)]] RWStructuredBuffer<uint> sdfProbeCacheRW : register(u74, space3);
[[vk::binding(75, 3)]] StructuredBuffer<uint> sdfProbeCache : register(t75, space3);

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

#endif // PUCK_SHADER_INTERFACE_SDF_PROBE_RELIGHT
