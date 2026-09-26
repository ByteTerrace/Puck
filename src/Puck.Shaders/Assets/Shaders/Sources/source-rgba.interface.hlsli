// Generated from shader interface 'source-rgba' (sha256/c885b0a9568fc332f6beb0bef948855fe2fb32862c5f912d25bcd81af26727df). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SOURCE_RGBA
#define PUCK_SHADER_INTERFACE_SOURCE_RGBA

// The Frame group: descriptor set 0, register space 0.
struct SourceRgbaFrame {
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
[[vk::binding(0, 0)]] ConstantBuffer<SourceRgbaFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct SourceRgbaPass {
    [[vk::offset(0)]] uint2 extent;
};
[[vk::binding(0, 3)]] ConstantBuffer<SourceRgbaPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] ByteAddressBuffer region : register(t1, space3);
[[vk::binding(2, 3)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> image : register(u2, space3);

#endif // PUCK_SHADER_INTERFACE_SOURCE_RGBA
