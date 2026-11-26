// The mesh pass's vertex stage. One indexed draw a mesh draw reads its level's indices from the mesh region
// (sdf-mesh-raster.hlsli), so its vertex id is the mesh's vertex (sdfMeshWorldPositionOf): the pushed index and the
// instance index name the draw and its level of detail (sdfMeshRasterDraw, sdfMeshRasterLevel), and each vertex hands the
// level's first triangle on flat with the draw, from which the fragment stage names the triangle it covers, whose surface
// the surface pass resolves. The projection is ViewProjection's, read from the view in the pass block:
// reversed-Z with an infinite far plane and the view's surface near distance n as the near plane,
// clip = (sx (x - ox w), sy (y - oy w), n, w) for a point at camera-space right x, up y and forward distance w, with the
// lens row's frustum offset (ox, oy). The render pass's area is the view's render extent, so clip space covers exactly
// the pixels whose centers the view's rays pass through. The occlusion kernel (sdf-mesh-occlusion.comp.hlsl) projects
// a draw's box the same way.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-raster.hlsli"

MeshRasterVertex VSMain(uint vertexId : SV_VertexID SDF_MESH_DRAW_INPUT) {
    uint draw = sdfMeshRasterDraw(drawId);
    uint record = sdfMeshRecord(draw);
    ViewportData view = worldView();
    float3 world = sdfMeshWorldPositionOf(record, vertexId);
    float3 relative = (world - view.position.xyz);
    float w = dot(relative, view.forward.xyz);
    float scaleX = (1.0 / (view.up.w * view.right.w));
    float scaleY = (1.0 / view.right.w);
    MeshRasterVertex output;

    output.position = float4(
        (scaleX * (dot(relative, view.right.xyz) - (view.lens.y * w))),
        (scaleY * (dot(relative, view.up.xyz) - (view.lens.z * w))),
        worldSurfaceNearDistance(view),
        w
    );
    output.clip = sdfMeshClipDistance(record, world);
    output.world = world;
    output.levelTriangle = sdfMeshRasterLevelTriangle(record, drawId);
    output.draw = draw;

    return output;
}
