// Generated from shader interface 'sdf-tonemap' (sha256/0983c776152afa813702482aa731aa90dbfdc6ea118435c535b16d9b995144f4). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_TONEMAP
#define PUCK_SHADER_INTERFACE_SDF_TONEMAP

// The Frame group: descriptor set 0, register space 0.
struct SdfTonemapFrame {
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
[[vk::binding(0, 0)]] ConstantBuffer<SdfTonemapFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct SdfTonemapPass {
    [[vk::offset(0)]] uint2 extent;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfTonemapPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] Texture2D<float4> source : register(t1, space3);
[[vk::binding(2, 3)]] SamplerState sourceSampler : register(s2, space3);

#endif // PUCK_SHADER_INTERFACE_SDF_TONEMAP
