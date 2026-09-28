// Generated from shader interface 'sdf-mesh' (sha256/4105d91952c4525cb17ccd58eb7cba3ae8f772ba0d56807f2e361fc2dfa47352). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_MESH
#define PUCK_SHADER_INTERFACE_SDF_MESH

// The Pass group: descriptor set 3, register space 3.
struct SdfMeshPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float ambientScale;
    [[vk::offset(12)]] float aspectRatio;
    [[vk::offset(16)]] uint cameraTileShadowMask;
    [[vk::offset(20)]] uint debugMode;
    [[vk::offset(24)]] float debugSliceAxis;
    [[vk::offset(28)]] float debugSliceOffset;
    [[vk::offset(32)]] uint disableAmbientOcclusion;
    [[vk::offset(36)]] uint disableFarBound;
    [[vk::offset(40)]] uint disableScreenLights;
    [[vk::offset(44)]] uint disableShadowCull;
    [[vk::offset(48)]] uint disableSoftShadows;
    [[vk::offset(52)]] uint enableShadowProxy;
    [[vk::offset(56)]] uint _pad56;
    [[vk::offset(60)]] uint _pad60;
    [[vk::offset(64)]] float4 environment[53];
    [[vk::offset(912)]] float farDistance;
    [[vk::offset(916)]] uint fastAmbientOcclusion;
    [[vk::offset(920)]] uint fastSoftShadowMarch;
    [[vk::offset(924)]] uint finiteDifferenceNormals;
    [[vk::offset(928)]] float2 frustumOffset;
    [[vk::offset(936)]] uint gridFlags;
    [[vk::offset(940)]] float gridFloorY;
    [[vk::offset(944)]] float4 gridObjectFrame;
    [[vk::offset(960)]] float3 gridObjectOrigin;
    [[vk::offset(972)]] float gridObjectPatchRadius;
    [[vk::offset(976)]] float2 gridObjectPitch;
    [[vk::offset(984)]] float2 gridWorldPitch;
    [[vk::offset(992)]] uint2 imageExtent;
    [[vk::offset(1000)]] uint instanceMaskWordCount;
    [[vk::offset(1004)]] uint meshDraws;
    [[vk::offset(1008)]] float nearDistance;
    [[vk::offset(1012)]] uint sampleIndex;
    [[vk::offset(1016)]] float sceneTime;
    [[vk::offset(1020)]] uint screenCount;
    [[vk::offset(1024)]] float shadowDistanceScale;
    [[vk::offset(1028)]] float sunScale;
    [[vk::offset(1032)]] float tanHalfFieldOfView;
    [[vk::offset(1036)]] uint _pad1036;
    [[vk::offset(1040)]] uint2 tileGrid;
    [[vk::offset(1048)]] uint viewBase;
    [[vk::offset(1052)]] uint _pad1052;
    [[vk::offset(1056)]] float3 viewForward;
    [[vk::offset(1068)]] uint _pad1068;
    [[vk::offset(1072)]] float3 viewPosition;
    [[vk::offset(1084)]] uint _pad1084;
    [[vk::offset(1088)]] float3 viewRight;
    [[vk::offset(1100)]] uint _pad1100;
    [[vk::offset(1104)]] float3 viewUp;
    [[vk::offset(1116)]] uint viewportCount;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfMeshPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfMeshPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfMeshPushedIndex> pushedIndex : register(b0, space4);

#endif // PUCK_SHADER_INTERFACE_SDF_MESH
