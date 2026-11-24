// Generated from shader interface 'sdf-probe-trace-rays' (sha256/edb73ab4c16fd04cd299bd6f5185651a6660754aac8a02edd01f49b5f15a3be5). Regenerate it from the interface; never edit it.
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

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfProbeTraceRaysPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfProbeTraceRaysPushedIndex> pushedIndex : register(b0, space4);

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

#endif // PUCK_SHADER_INTERFACE_SDF_PROBE_TRACE_RAYS
