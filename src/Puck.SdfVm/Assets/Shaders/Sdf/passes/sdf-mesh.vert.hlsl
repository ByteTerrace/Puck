// The mesh pass's vertex stage. One draw call a mesh draw, non-indexed over the draw's index count, pulls its triangles
// from the mesh region (sdf-mesh.hlsli): the pushed index names the view and the draw, and a vertex id's triangle is
// its three consecutive indices, which each vertex hands on flat so the hit passes resolve that triangle's surface. The
// projection is ViewProjection's, read from the view in the pass block: reversed-Z with an infinite far plane and the
// view's near distance n as the near plane, clip = (sx (x - ox w), sy (y - oy w), n, w) for a point at camera-space
// right x, up y and forward distance w, with the lens row's frustum offset (ox, oy). The render
// pass's area is the view's render extent, so clip space covers exactly the pixels whose centers the SDF march casts
// through.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"

MeshVertex VSMain(uint vertexId : SV_VertexID) {
    uint record = sdfMeshRecord((pushedIndex.index & SdfMeshDrawMask));
    float3 world = sdfMeshWorldPosition(record, vertexId);
    ViewportData view = worldView();
    float3 relative = (world - view.position.xyz);
    float w = dot(relative, view.forward.xyz);
    float scaleX = (1.0 / (view.up.w * view.right.w));
    float scaleY = (1.0 / view.right.w);
    MeshVertex output;

    output.position = float4(
        (scaleX * (dot(relative, view.right.xyz) - (view.lens.y * w))),
        (scaleY * (dot(relative, view.up.xyz) - (view.lens.z * w))),
        worldNearDistance(view),
        w
    );
    output.world = world;
    output.triangleIndex = (vertexId / 3u);

    return output;
}
