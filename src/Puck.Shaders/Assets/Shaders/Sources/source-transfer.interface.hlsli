// Generated from shader interface 'source-transfer' (sha256/16106fa99ccc013f08426e40d6864cc86e58cff0f90cadc91a7b5156309dcfaa). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SOURCE_TRANSFER
#define PUCK_SHADER_INTERFACE_SOURCE_TRANSFER

// The Frame group: descriptor set 0, register space 0.
struct SourceTransferFrame {
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
[[vk::binding(0, 0)]] ConstantBuffer<SourceTransferFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct SourceTransferPass {
    [[vk::offset(0)]] uint2 extent;
};
[[vk::binding(0, 3)]] ConstantBuffer<SourceTransferPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] ByteAddressBuffer region : register(t1, space3);
[[vk::binding(2, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> image : register(u2, space3);

#endif // PUCK_SHADER_INTERFACE_SOURCE_TRANSFER
