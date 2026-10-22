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

// The draw a vertex belongs to: the pushed index's draw plus the instance index. A draw the host records pushes its
// draw and starts its one instance at zero; a counted indirect draw's record carries the draw as its identity, which
// Direct3D 12 writes into the pushed index and Vulkan starts the instance at, the host having pushed zero
// (GpuDrawIndirectCommand), so either way the sum is the draw.
uint sdfMeshRasterDraw(uint instanceId) {
    return ((pushedIndex.index + instanceId) & SdfMeshDrawMask);
}

#endif
