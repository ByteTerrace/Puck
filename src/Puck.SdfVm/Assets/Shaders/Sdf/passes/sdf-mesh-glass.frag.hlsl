// All glass crossings integrate optical depth up to the opaque hit. Front crossings visible to the camera
// also write the interface target through a reversed-Z depth test. Use the triangle's authored outward winding
// for entry and exit, since rasterizer facing follows backend viewport conventions.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-raster.hlsli"
#include "../frame/sdf-stamp-fade.hlsli"
#include "../frame/sdf-visibility.hlsli"
#define SDF_GLASS_ACCUMULATES
#include "../frame/sdf-glass.hlsli"
#include "../field/sdf-program.hlsli"
#include "../shade/sdf-material.hlsli"

struct GlassFragment {
    float4 target : SV_Target0;
    float depth : SV_Depth;
};

GlassFragment PSMain(MeshRasterVertex input) {
    sdfStampFade(input.draw, uint2(input.position.xy));
    ViewportData view = worldView();
    uint2 pixel = uint2(input.position.xy);
    float t = length(input.world - view.position.xyz);
    uint record = sdfMeshRecord(input.draw);
    uint first = (3u * input.triangleIndex);
    float3 a = sdfMeshWorldPosition(record, first);
    bool front = (dot(cross((sdfMeshWorldPosition(record, (first + 1u)) - a), (sdfMeshWorldPosition(record, (first + 2u)) - a)), (input.world - view.position.xyz)) < 0.0);
    bool counts = false;

    // Only a covered pixel addresses the per-pixel records and sums: a helper lane of a quad that straddles the grid's
    // last row or column lies outside the grid, where its record is past the end of the buffers. A helper's outputs
    // are discarded, so it needs neither.
    if (!IsHelperLane()) {
        SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, passGroup.imageExtent));
        float opaque = (sdfVisibilityHit(visibility) ? visibility.t : worldFarDistance(view));

        counts = (t < opaque);
        float3 ray = cameraRayDirection(view, (float2(pixel) + 0.5) / float2(passGroup.imageExtent));
        float start = worldSurfaceNearDistance(view) / dot(ray, view.forward.xyz);
        int materialId = sdfMeshRasterMaterial(input.draw, input.triangleIndex);
        SdfMaterialData material = sdfMaterialLoad(materialId);
        bool clippedDraw = any(sdfMeshClipPlane(record) != 0.0);

        sdfGlassAccumulate(sdfGlassSumWord(pixel, passGroup.imageExtent), t, start, opaque, front, material.absorption,
            materialId, input.draw, input.triangleIndex, clippedDraw);
    }
    puckCountFragmentWork(0u, 1u);

    GlassFragment fragment;

    fragment.target = float4(t, float(input.draw + 1u), float(input.triangleIndex), 0.0);
    fragment.depth = ((front && counts) ? input.position.z : 0.0);

    return fragment;
}
