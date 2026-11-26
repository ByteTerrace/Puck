// Generated from shader interface 'sdf-shadow-blocks' (sha256/12992b42789d8f89708a78277dd59e6d481ea6f7c708d582f4a363aec2176be0). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_SHADOW_BLOCKS
#define PUCK_SHADER_INTERFACE_SDF_SHADOW_BLOCKS

// The Pass group: descriptor set 3, register space 3.
[[vk::binding(0, 3)]] Texture2D<float4> shadowBlocksDepth : register(t0, space3);
[[vk::binding(1, 3)]] Texture2D<float4> shadowBlocksExit : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<uint> shadowBlocksOptical : register(t2, space3);
[[vk::binding(3, 3)]] RWStructuredBuffer<uint> shadowBlocksRW : register(u3, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfShadowBlocksPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfShadowBlocksPushedIndex> pushedIndex : register(b0, space4);

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

#endif // PUCK_SHADER_INTERFACE_SDF_SHADOW_BLOCKS
