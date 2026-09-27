// Generated from shader interface 'sdf-world' (sha256/4c3b9b477ec39342dfb60ae4664063adc0a2312422d21e451de75f994a39e79e). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_WORLD
#define PUCK_SHADER_INTERFACE_SDF_WORLD

// The Frame group: descriptor set 0, register space 0.
struct SdfWorldFrame {
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
[[vk::binding(0, 0)]] ConstantBuffer<SdfWorldFrame> frameGroup : register(b0, space0);

// The Pass group: descriptor set 3, register space 3.
struct SdfWorldPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] uint2 imageExtent;
    [[vk::offset(16)]] uint2 tileGrid;
    [[vk::offset(24)]] uint viewportCount;
    [[vk::offset(28)]] uint screenCount;
    [[vk::offset(32)]] uint instanceMaskWordCount;
    [[vk::offset(36)]] uint sampleIndex;
    [[vk::offset(40)]] uint viewBase;
    [[vk::offset(44)]] uint meshDraws;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfWorldPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint4> sdfWords : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<float4> viewports : register(t2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t3, space3);
[[vk::binding(4, 3)]] StructuredBuffer<uint> sdfFrameInstanceGrid : register(t4, space3);
[[vk::binding(5, 3)]] StructuredBuffer<uint> sdfInstanceMasks : register(t5, space3);
[[vk::binding(6, 3)]] RWStructuredBuffer<uint> sdfInstanceMasksRW : register(u6, space3);
[[vk::binding(7, 3)]] StructuredBuffer<float> tiles : register(t7, space3);
[[vk::binding(8, 3)]] RWStructuredBuffer<float> tilesRW : register(u8, space3);
[[vk::binding(9, 3)]] StructuredBuffer<uint> cullBounds : register(t9, space3);
[[vk::binding(10, 3)]] RWStructuredBuffer<uint> cullBoundsRW : register(u10, space3);
[[vk::binding(11, 3)]] RWStructuredBuffer<uint> viewsArgsRW : register(u11, space3);
[[vk::binding(12, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t12, space3);
[[vk::binding(13, 3)]] RWStructuredBuffer<uint> sdfVisibilityRecordsRW : register(u13, space3);
[[vk::binding(14, 3)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> output : register(u14, space3);
[[vk::binding(15, 3)]] StructuredBuffer<float4> screenSurfaces : register(t15, space3);
[[vk::binding(16, 3)]] StructuredBuffer<float4> screenMappings : register(t16, space3);
[[vk::binding(17, 3)]] StructuredBuffer<float4> sdfScreenLights : register(t17, space3);
[[vk::binding(18, 3)]] StructuredBuffer<uint4> sdfDecalCells : register(t18, space3);
[[vk::binding(19, 3)]] StructuredBuffer<float> sdfBrickPool : register(t19, space3);
[[vk::binding(20, 3)]] StructuredBuffer<float4> sdfVolumes : register(t20, space3);
[[vk::binding(21, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t21, space3);
[[vk::binding(22, 3)]] Texture2D<float4> screenSources[32] : register(t22, space3);
[[vk::binding(54, 3)]] Texture2D<float4> sdfGlyphAtlas : register(t54, space3);
[[vk::binding(55, 3)]] Texture2D<float4> meshVisibility : register(t55, space3);
[[vk::binding(56, 3)]] SamplerState samplers[2] : register(s56, space3);

#endif // PUCK_SHADER_INTERFACE_SDF_WORLD
