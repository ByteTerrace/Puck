// Generated from shader interface 'sdf-world' (sha256/adb16f92aa23e79d2cb079a33db59e933ced5b5863ca425dd0079f0203fe4e8a). Regenerate it from the interface; never edit it.
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
    [[vk::offset(28)]] uint screenMask;
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
[[vk::binding(16, 3)]] StructuredBuffer<float4> sdfScreenLights : register(t16, space3);
[[vk::binding(17, 3)]] StructuredBuffer<uint4> sdfDecalCells : register(t17, space3);
[[vk::binding(18, 3)]] StructuredBuffer<float> sdfBrickPool : register(t18, space3);
[[vk::binding(19, 3)]] StructuredBuffer<float4> sdfVolumes : register(t19, space3);
[[vk::binding(20, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t20, space3);
[[vk::binding(21, 3)]] Texture2D<float4> screenSource0 : register(t21, space3);
[[vk::binding(22, 3)]] Texture2D<float4> screenSource1 : register(t22, space3);
[[vk::binding(23, 3)]] Texture2D<float4> screenSource2 : register(t23, space3);
[[vk::binding(24, 3)]] Texture2D<float4> screenSource3 : register(t24, space3);
[[vk::binding(25, 3)]] Texture2D<float4> screenSource4 : register(t25, space3);
[[vk::binding(26, 3)]] Texture2D<float4> screenSource5 : register(t26, space3);
[[vk::binding(27, 3)]] Texture2D<float4> screenSource6 : register(t27, space3);
[[vk::binding(28, 3)]] Texture2D<float4> screenSource7 : register(t28, space3);
[[vk::binding(29, 3)]] Texture2D<float4> screenSource8 : register(t29, space3);
[[vk::binding(30, 3)]] Texture2D<float4> screenSource9 : register(t30, space3);
[[vk::binding(31, 3)]] Texture2D<float4> screenSource10 : register(t31, space3);
[[vk::binding(32, 3)]] Texture2D<float4> screenSource11 : register(t32, space3);
[[vk::binding(33, 3)]] Texture2D<float4> screenSource12 : register(t33, space3);
[[vk::binding(34, 3)]] Texture2D<float4> screenSource13 : register(t34, space3);
[[vk::binding(35, 3)]] Texture2D<float4> screenSource14 : register(t35, space3);
[[vk::binding(36, 3)]] Texture2D<float4> screenSource15 : register(t36, space3);
[[vk::binding(37, 3)]] Texture2D<float4> screenSource16 : register(t37, space3);
[[vk::binding(38, 3)]] Texture2D<float4> screenSource17 : register(t38, space3);
[[vk::binding(39, 3)]] Texture2D<float4> screenSource18 : register(t39, space3);
[[vk::binding(40, 3)]] Texture2D<float4> screenSource19 : register(t40, space3);
[[vk::binding(41, 3)]] Texture2D<float4> screenSource20 : register(t41, space3);
[[vk::binding(42, 3)]] Texture2D<float4> screenSource21 : register(t42, space3);
[[vk::binding(43, 3)]] Texture2D<float4> screenSource22 : register(t43, space3);
[[vk::binding(44, 3)]] Texture2D<float4> screenSource23 : register(t44, space3);
[[vk::binding(45, 3)]] Texture2D<float4> screenSource24 : register(t45, space3);
[[vk::binding(46, 3)]] Texture2D<float4> screenSource25 : register(t46, space3);
[[vk::binding(47, 3)]] Texture2D<float4> screenSource26 : register(t47, space3);
[[vk::binding(48, 3)]] Texture2D<float4> screenSource27 : register(t48, space3);
[[vk::binding(49, 3)]] Texture2D<float4> screenSource28 : register(t49, space3);
[[vk::binding(50, 3)]] Texture2D<float4> screenSource29 : register(t50, space3);
[[vk::binding(51, 3)]] Texture2D<float4> screenSource30 : register(t51, space3);
[[vk::binding(52, 3)]] Texture2D<float4> screenSource31 : register(t52, space3);
[[vk::binding(53, 3)]] Texture2D<float4> sdfGlyphAtlas : register(t53, space3);
[[vk::binding(54, 3)]] Texture2D<float4> meshVisibility : register(t54, space3);
[[vk::binding(55, 3)]] SamplerState screenSampler : register(s55, space3);

#endif // PUCK_SHADER_INTERFACE_SDF_WORLD
