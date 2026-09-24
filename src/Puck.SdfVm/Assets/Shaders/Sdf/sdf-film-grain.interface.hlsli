// Generated from shader interface 'sdf-film-grain' (sha256/b4a334e6d6564eb5fbc873a50f25b05b1613dc2c2ff6e55e09e121eb1845f86c). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_FILM_GRAIN
#define PUCK_SHADER_INTERFACE_SDF_FILM_GRAIN

// The Frame group: push constants, register space 0.
struct SdfFilmGrainFrame {
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
    [[vk::offset(92)]] uint flickerHz;
    [[vk::offset(96)]] float intensity;
    [[vk::offset(100)]] uint seed;
    [[vk::offset(104)]] float size;
};
[[vk::push_constant]] ConstantBuffer<SdfFilmGrainFrame> frameGroup : register(b0, space0);

#endif // PUCK_SHADER_INTERFACE_SDF_FILM_GRAIN
