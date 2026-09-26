// Generated from shader interface 'sdf-brick-bake' (sha256/7a500ee001b41f81bbf34d2c01a08a250c60d9d5450daf52dc21ab1e6829336f). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_BRICK_BAKE
#define PUCK_SHADER_INTERFACE_SDF_BRICK_BAKE

// The Pass group: descriptor set 3, register space 3.
struct SdfBrickBakePass {
    [[vk::offset(0)]] uint sliceVoxels;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfBrickBakePass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<float4> bakeRequest : register(t1, space3);
[[vk::binding(2, 3)]] RWStructuredBuffer<float> brickPool : register(u2, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfBrickBakePushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfBrickBakePushedIndex> pushedIndex : register(b0, space4);

#endif // PUCK_SHADER_INTERFACE_SDF_BRICK_BAKE
