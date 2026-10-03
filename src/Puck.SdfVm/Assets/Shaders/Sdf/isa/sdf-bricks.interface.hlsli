// Generated from shader interface 'sdf-bricks' (sha256/8c6c21bcd1b51acd67a805c3755b96360b2d3e8ffc0bec4d6d287a0eb71da7f7). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_BRICKS
#define PUCK_SHADER_INTERFACE_SDF_BRICKS

// The Frame group: descriptor set 0, register space 0.
struct SdfBricksFrame {
    [[vk::offset(0)]] float2 pointer;
    [[vk::offset(8)]] uint2 tick;
    [[vk::offset(16)]] float time;
    [[vk::offset(20)]] float timeDelta;
    [[vk::offset(24)]] uint frame;
    [[vk::offset(28)]] uint tickRate;
    [[vk::offset(32)]] uint pointerDown;
    [[vk::offset(36)]] uint pointerPresses;
    [[vk::offset(40)]] uint _pad40;
    [[vk::offset(44)]] uint _pad44;
    [[vk::offset(48)]] float3 cameraPosition;
    [[vk::offset(60)]] float cameraFov;
    [[vk::offset(64)]] float3 cameraTarget;
    [[vk::offset(76)]] uint _pad76;
    [[vk::offset(80)]] float3 cameraUp;
    [[vk::offset(92)]] uint _pad92;
    [[vk::offset(96)]] float2 placedExtent;
};
[[vk::binding(0, 0)]] ConstantBuffer<SdfBricksFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct SdfBricksPass {
    [[vk::offset(0)]] uint2 extent;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfBricksPass> passGroupIsaAC29C3DD : register(b0, space3);
#define passGroup passGroupIsaAC29C3DD
[[vk::binding(1, 3)]] StructuredBuffer<float4> bakeRequest : register(t1, space3);
[[vk::binding(2, 3)]] RWStructuredBuffer<float> brickPool : register(u2, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfBricksPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfBricksPushedIndex> pushedIndex : register(b0, space4);

// This interface declares no work counters, so its passes count nothing: the counting functions a kernel calls
// are declared empty, and a kernel written for a counting package compiles here unchanged.
void puckCountWork(uint steps, uint texels) {
}
void puckCountDetail(uint detail, uint steps, uint texels, uint evaluations, uint hashes, uint loads) {
}
void puckCountShadow(uint slot, uint steps) {
}
void puckCountFragmentWork(uint steps, uint texels) {
}

#endif // PUCK_SHADER_INTERFACE_SDF_BRICKS
