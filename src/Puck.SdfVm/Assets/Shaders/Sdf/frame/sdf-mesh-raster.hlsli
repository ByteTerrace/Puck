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

// Vulkan pushes a word base and reads the record's identity through DrawIndex, including the fixed multi-draw and
// single-slot fallbacks when native counting is unavailable.
// Direct3D 12's counted draw writes one word into the pushed index per command. Below execute-indirect tier 1.1 that
// word is the record's identity, whose level of detail sits above SDF_MESH_LEVEL_SHIFT. At tier 1.1 it is the command's
// position, which the occlusion kernel makes the candidate's index in every phase, and the candidate's identity is the one
// every phase writes at that index: the first phase's record. A word with no level bits names the same record either way, so a
// level-zero identity read through the table is its own value.
uint sdfMeshRasterIdentity(uint drawId) {
#ifdef __spirv__
    return sdfMeshIndirectCommands[pushedIndex.index + 5u * drawId];
#else
    uint pushed = pushedIndex.index;

    return (((pushed >> SDF_MESH_LEVEL_SHIFT) != 0u) ? pushed : sdfMeshIndirectCommands[5u * pushed]);
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
