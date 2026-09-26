// The mesh pass's vertex stage. One draw call a mesh draw, non-indexed over the draw's index count, pulls its triangles
// from the mesh region (sdf-mesh.hlsli): the pushed index names the view and the draw, and a vertex id's triangle is
// its three consecutive indices. The projection is ViewProjection's, read from the view's viewport row: reversed-Z
// with an infinite far plane and ConeNear as the near plane, clip = (sx (x - ox w), sy (y - oy w), ConeNear, w) for a
// point at camera-space right x, up y and forward distance w, with the lens row's frustum offset (ox, oy). The render
// pass's area is the view's render extent, so clip space covers exactly the pixels whose centers the SDF march casts
// through.
#include "sdf-mesh.interface.hlsli"
#include "sdf-viewport.hlsli"
#include "sdf-mesh.hlsli"

MeshVertex VSMain(uint vertexId : SV_VertexID) {
    uint record = sdfMeshRecord((pushedIndex.index & SdfMeshDrawMask));
    uint corner = (vertexId % 3u);
    uint first = (vertexId - corner);
    float3 a = sdfMeshWorldPosition(record, first);
    float3 b = sdfMeshWorldPosition(record, (first + 1u));
    float3 c = sdfMeshWorldPosition(record, (first + 2u));
    float3 world = ((corner == 0u) ? a : ((corner == 1u) ? b : c));
    ViewportData view = worldViewport((pushedIndex.index >> SdfMeshViewShift));
    float3 relative = (world - view.position.xyz);
    float w = dot(relative, view.forward.xyz);
    float scaleX = (1.0 / (view.up.w * view.right.w));
    float scaleY = (1.0 / view.right.w);
    MeshVertex output;

    output.position = float4(
        (scaleX * (dot(relative, view.right.xyz) - (view.lens.y * w))),
        (scaleY * (dot(relative, view.up.xyz) - (view.lens.z * w))),
        ConeNear,
        w
    );
    output.world = world;
    output.normal = cross((b - a), (c - a));

    return output;
}
