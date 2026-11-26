// What the mesh pass's vertex stage (passes/sdf-mesh.vert.hlsl) hands the mesh and glass fragment stages: the clip
// position, the clip plane's distance, the world position the fragment measures its ray parameter and forward depth to,
// the first triangle of the draw's level of detail, from which a fragment names the triangle it covers
// (sdfMeshRasterTriangle), and the draw. The includer's interface declares the pushed index, sdfMeshRegion and the work
// counters (isa/sdf-mesh.interface.hlsli), and includes frame/sdf-mesh.hlsli first.
//
// Every raster draw is indexed (SdfMeshOcclusion.RecordDraws): its indices are its level's in the mesh region, read
// from the draw's first index with no vertex offset, so a vertex stage's vertex id is the vertex the mesh's index names
// (sdfMeshWorldVertexOf) and a vertex shared by several triangles runs once where the hardware reuses it. A vertex
// belongs to no one triangle, so the triangle a fragment covers is its primitive id within the draw counted from the
// level's first triangle, which the vertex stage hands on flat.
#ifndef SDF_MESH_RASTER_HLSLI
#define SDF_MESH_RASTER_HLSLI

struct MeshRasterVertex {
    float4 position : SV_Position;
    float clip : SV_ClipDistance0;
    float3 world : WORLD;
    nointerpolation uint levelTriangle : MESHLEVELTRIANGLE;
    nointerpolation uint draw : MESHDRAW;
};

// GpuDrawIndirectCommand.Words: the words of one counted indirect draw's record, the identity first.
static const uint SdfMeshRasterCommandWords = 6u;

// Direct3D 12 ExecuteIndirect pushes the record's identity. Vulkan pushes a word base and reads the same identity
// through DrawIndex, including the fixed multi-draw and single-slot fallbacks when native counting is unavailable.
uint sdfMeshRasterIdentity(uint drawId) {
#ifdef __spirv__
    return sdfMeshIndirectCommands[pushedIndex.index + SdfMeshRasterCommandWords * drawId];
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
// The first triangle of the level of detail a draw draws, among its mesh's levelled triangles.
uint sdfMeshRasterLevelTriangle(uint record, uint drawId) {
    return (sdfMeshLevelIndex(record, sdfMeshRasterLevel(drawId)) / 3u);
}
// The triangle a fragment of a raster draw covers, among its mesh's levelled triangles: its primitive id within the draw
// past the level's first.
uint sdfMeshRasterTriangle(MeshRasterVertex input, uint primitive) {
    return (input.levelTriangle + primitive);
}

#ifdef __spirv__
#define SDF_MESH_DRAW_INPUT , [[vk::builtin("DrawIndex")]] uint drawId : DRAWID
#else
#define SDF_MESH_DRAW_INPUT , uint drawId : SV_InstanceID
#endif

#endif
