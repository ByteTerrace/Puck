// Generated from shader interface 'source-nv12' (sha256/2bbaf370f68018a9bc60fd2d6d1d64f8e6cc21ab5454fd84a57240bed18c0581). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SOURCE_NV12
#define PUCK_SHADER_INTERFACE_SOURCE_NV12

// The Frame group: descriptor set 0, register space 0.
struct SourceNv12Frame {
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
[[vk::binding(0, 0)]] ConstantBuffer<SourceNv12Frame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct SourceNv12Pass {
    [[vk::offset(0)]] uint2 extent;
};
[[vk::binding(0, 3)]] ConstantBuffer<SourceNv12Pass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] ByteAddressBuffer region : register(t1, space3);
[[vk::binding(2, 3)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> image : register(u2, space3);

#endif // PUCK_SHADER_INTERFACE_SOURCE_NV12
