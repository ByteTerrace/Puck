// The mesh pass's vertex stage. One draw call a mesh draw, non-indexed over the draw's index count, pulls its triangles
// from the mesh region (sdf-mesh.hlsli): the pushed index names the view and the draw, and a vertex id's triangle is
// its three consecutive indices, which each vertex hands on flat so the hit passes resolve that triangle's surface. The
// projection is ViewProjection's, read from the view in the pass block: reversed-Z with an infinite far plane and the
// view's surface near distance n as the near plane, clip = (sx (x - ox w), sy (y - oy w), n, w) for a point at
// camera-space right x, up y and forward distance w, with the lens row's frustum offset (ox, oy). The render
// pass's area is the view's render extent, so clip space covers exactly the pixels whose centers the SDF march casts
// through.
// A draw that is an impostor card (SdfMeshImpostorFlag) pulls no triangle of a mesh: its four corners (the signs of its
// card mesh's positions) are placed on the plane perpendicular to the view's forward axis that touches the near side of
// the impostor's bounding sphere, centered where the ray through the sphere's center crosses it, and as wide as the sphere's
// silhouette on the plane can be: the radius, plus the sphere's tangent of its offset from the axis, per axis. Every pixel
// the sphere covers is then a pixel of the card, and sdf-mesh-impostor.frag.hlsl keeps the ones the impostor covers.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-impostor.hlsli"

// The world position of a card's corner for the view: the corner (x, y) in {-1, 1} of the card in front of the draw's sphere.
float3 sdfMeshCardCorner(uint record, float2 corner, ViewportData view) {
    SdfImpostor impostor = sdfImpostorOf(record);
    float3 center = sdfImpostorWorldCenter(record, impostor);
    float radius = (impostor.radius * sdfImpostorScale(record));
    float3 relative = (center - view.position.xyz);
    float depth = max(dot(relative, view.forward.xyz), radius);
    float onPlane = ((depth - radius) / depth);
    float2 across = (float2(dot(relative, view.right.xyz), dot(relative, view.up.xyz)) / depth);
    float2 reach = (radius * (1.0 + abs(across)));

    return (view.position.xyz + (relative * onPlane) + (view.right.xyz * (corner.x * reach.x)) + (view.up.xyz * (corner.y * reach.y)));
}

MeshVertex VSMain(uint vertexId : SV_VertexID) {
    uint record = sdfMeshRecord((pushedIndex.index & SdfMeshDrawMask));
    ViewportData view = worldView();
    float3 world = (((sdfMeshRegion[(record + SdfMeshFlagsWord)] & SdfMeshImpostorFlag) != 0u)
        ? sdfMeshCardCorner(record, sdfMeshObjectPosition(record, vertexId).xy, view)
        : sdfMeshWorldPosition(record, vertexId));
    float3 relative = (world - view.position.xyz);
    float w = dot(relative, view.forward.xyz);
    float scaleX = (1.0 / (view.up.w * view.right.w));
    float scaleY = (1.0 / view.right.w);
    MeshVertex output;

    output.position = float4(
        (scaleX * (dot(relative, view.right.xyz) - (view.lens.y * w))),
        (scaleY * (dot(relative, view.up.xyz) - (view.lens.z * w))),
        worldSurfaceNearDistance(view),
        w
    );
    if (passGroup.lightMap != 0u) {
        // Finite reversed-Z orthographic projection, matching the parallel primary rays and their local depth.
        output.position = float4(scaleX * dot(relative, view.right.xyz), scaleY * dot(relative, view.up.xyz),
            (worldFarDistance(view) - w) / (worldFarDistance(view) - worldSurfaceNearDistance(view)), 1.0);
    }
    output.world = world;
    output.triangleIndex = (vertexId / 3u);

    return output;
}
