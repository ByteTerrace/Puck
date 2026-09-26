// Generated from shader interface 'place' (sha256/71c26220087d60e4a77b084e5e70f400414bced24f5244907efa7967373331a9). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_PLACE
#define PUCK_SHADER_INTERFACE_PLACE

// The Frame group: descriptor set 0, register space 0.
struct PlaceFrame {
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
[[vk::binding(0, 0)]] ConstantBuffer<PlaceFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct PlacePass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] uint _pad8;
    [[vk::offset(12)]] uint _pad12;
    [[vk::offset(16)]] float4 rect;
    [[vk::offset(32)]] float sharpness;
};
[[vk::binding(0, 3)]] ConstantBuffer<PlacePass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] Texture2D<float4> base : register(t1, space3);
[[vk::binding(2, 3)]] SamplerState baseSampler : register(s2, space3);
[[vk::binding(3, 3)]] Texture2D<float4> source : register(t3, space3);
[[vk::binding(4, 3)]] SamplerState sourceSampler : register(s4, space3);
[[vk::binding(5, 3)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> destination : register(u5, space3);

#endif // PUCK_SHADER_INTERFACE_PLACE
