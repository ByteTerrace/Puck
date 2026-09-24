// The declarations generated for shader interface 'perturbed' (sha256/4cfa8f87f0f8b3b62b8e43f2b9e9b16bde74a4f5f56efeef6e917787a558ec94), perturbed by hand: time and timeDelta read each other's offset.
#ifndef PUCK_SHADER_INTERFACE_PERTURBED
#define PUCK_SHADER_INTERFACE_PERTURBED

// The Frame group: push constants, register space 0.
struct PerturbedFrame {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float2 pointer;
    [[vk::offset(16)]] uint2 tick;
    [[vk::offset(24)]] float timeDelta;
    [[vk::offset(28)]] float time;
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
    [[vk::offset(96)]] float3 bias;
    [[vk::offset(108)]] uint count;
    [[vk::offset(112)]] float2 scale;
    [[vk::offset(120)]] int shift;
};
[[vk::push_constant]] ConstantBuffer<PerturbedFrame> frameGroup : register(b0, space0);

#endif // PUCK_SHADER_INTERFACE_PERTURBED
