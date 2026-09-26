// Generated from shader interface 'overlay' (sha256/10413a520b1b76525c0927727d5264a746309e3834374ebb22fd993229f79705). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_OVERLAY
#define PUCK_SHADER_INTERFACE_OVERLAY

// The Frame group: descriptor set 0, register space 0.
struct OverlayFrame {
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
};
[[vk::binding(0, 0)]] ConstantBuffer<OverlayFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct OverlayPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] uint _pad8;
    [[vk::offset(12)]] uint _pad12;
    [[vk::offset(16)]] float4 counts;
    [[vk::offset(32)]] float4 misc;
    [[vk::offset(48)]] float4 sdf;
};
[[vk::binding(0, 3)]] ConstantBuffer<OverlayPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] Texture2D<float4> source : register(t1, space3);
[[vk::binding(2, 3)]] Texture2D<float4> frameSlot0 : register(t2, space3);
[[vk::binding(3, 3)]] Texture2D<float4> frameSlot1 : register(t3, space3);
[[vk::binding(4, 3)]] Texture2D<float4> frameSlot2 : register(t4, space3);
[[vk::binding(5, 3)]] Texture2D<float4> frameSlot3 : register(t5, space3);
[[vk::binding(6, 3)]] Texture2D<float4> frameSlot4 : register(t6, space3);
[[vk::binding(7, 3)]] Texture2D<float4> frameSlot5 : register(t7, space3);
[[vk::binding(8, 3)]] Texture2D<float4> frameSlot6 : register(t8, space3);
[[vk::binding(9, 3)]] Texture2D<float4> frameSlot7 : register(t9, space3);
[[vk::binding(10, 3)]] SamplerState linearSampler : register(s10, space3);
[[vk::binding(11, 3)]] ByteAddressBuffer overlayData : register(t11, space3);

#endif // PUCK_SHADER_INTERFACE_OVERLAY
