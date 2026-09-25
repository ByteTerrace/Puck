// Generated from shader interface 'place' (sha256/adb9c90c39ac2bf73f2c79cd41cce6160b03276a2838a77a313eca95bb07a5a6). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_PLACE
#define PUCK_SHADER_INTERFACE_PLACE

// The Frame group: push constants, register space 0.
struct PlaceFrame {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float2 pointer;
    [[vk::offset(16)]] uint2 tick;
    [[vk::offset(24)]] float time;
    [[vk::offset(28)]] float timeDelta;
    [[vk::offset(32)]] uint frame;
    [[vk::offset(36)]] uint tickRate;
    [[vk::offset(40)]] uint pointerDown;
    [[vk::offset(44)]] uint pointerPresses;
    [[vk::offset(48)]] float3 cameraPosition;
    [[vk::offset(60)]] float cameraFov;
    [[vk::offset(64)]] float3 cameraTarget;
    [[vk::offset(76)]] uint _pad76;
    [[vk::offset(80)]] float3 cameraUp;
    [[vk::offset(92)]] uint _pad92;
    [[vk::offset(96)]] float4 rect;
    [[vk::offset(112)]] float sharpness;
};
[[vk::push_constant]] ConstantBuffer<PlaceFrame> frameGroup : register(b0, space0);

#endif // PUCK_SHADER_INTERFACE_PLACE
