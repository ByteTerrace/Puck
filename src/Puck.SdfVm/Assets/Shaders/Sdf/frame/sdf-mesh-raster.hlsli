// What the mesh pass's vertex stage (passes/sdf-mesh.vert.hlsl) hands the mesh and glass fragment stages: the clip
// position, the clip plane's distance, the world position the fragment measures its ray parameter and forward depth to,
// the triangle, which the surface pass resolves the surface of, and the draw. The includer's interface declares the
// pushed index, sdfMeshRegion and the work counters (isa/sdf-mesh.interface.hlsli), and includes frame/sdf-mesh.hlsli first.
#ifndef SDF_MESH_RASTER_HLSLI
#define SDF_MESH_RASTER_HLSLI

struct MeshRasterVertex {
    float4 position : SV_Position;
    float clip : SV_ClipDistance0;
    float3 world : WORLD;
    nointerpolation uint triangleIndex : MESHTRIANGLE;
    nointerpolation uint draw : MESHDRAW;
};

// Direct3D 12 ExecuteIndirect pushes the record's identity. Vulkan pushes a word base and reads the same identity
// through DrawIndex, including the fixed multi-draw and single-slot fallbacks when native counting is unavailable.
uint sdfMeshRasterIdentity(uint drawId) {
#ifdef __spirv__
    return sdfMeshIndirectCommands[pushedIndex.index + 5u * drawId];
#else
    return pushedIndex.index;
#endif
}
// The level of detail the draw draws, carried from bit SDF_MESH_LEVEL_SHIFT up the same way.
uint sdfMeshRasterDraw(uint drawId) {
    return sdfMeshRasterIdentity(drawId) & SdfMeshDrawMask;
}
uint sdfMeshRasterLevel(uint drawId) {
    return sdfMeshRasterIdentity(drawId) >> SDF_MESH_LEVEL_SHIFT;
}

#ifdef __spirv__
#define SDF_MESH_DRAW_INPUT , [[vk::builtin("DrawIndex")]] uint drawId : DRAWID
#else
#define SDF_MESH_DRAW_INPUT , uint drawId : SV_InstanceID
#endif

#endif
