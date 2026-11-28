// All glass crossings integrate optical depth up to the opaque hit. Front crossings visible to the camera
// also write the interface target through a reversed-Z depth test. Use the triangle's authored outward winding
// for entry and exit, since rasterizer facing follows backend viewport conventions.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-raster.hlsli"
#include "../frame/sdf-stamp-fade.hlsli"
#include "../frame/sdf-form-band.hlsli"
#include "../frame/sdf-visibility.hlsli"
#define SDF_GLASS_ACCUMULATES
#include "../frame/sdf-glass.hlsli"
#include "../field/sdf-program.hlsli"
#include "../shade/sdf-material.hlsli"
#include "../frame/sdf-mesh-box.hlsli"

// Whether no ray of the view can start inside a draw: its world box lies wholly beyond the near plane, by a margin over
// the box's rounding, so every crossing of its closed surface lies past every ray's start and the draw accumulates from the
// opaque hit (frame/sdf-glass.hlsli). False for a draw no box bounds.
bool sdfGlassBeyondNear(uint record, ViewportData view) {
    uint4 a = sdfSelectionRow(record, 0u);

    if ((a.w & 1u) != 0u) {
        return false;
    }

    float3 center;
    float3 x;
    float3 y;
    float3 z;

    sdfSelectionBox(record, a, sdfSelectionRow(record, 1u), sdfSelectionRow(record, 2u), sdfSelectionRow(record, 3u), center, x, y, z);

    float3 forward = view.forward.xyz;
    float nearest = (dot((center - view.position.xyz), forward) - sdfSelectionRadius(forward, x, y, z));
    float nearPlane = worldSurfaceNearDistance(view);

    return (nearest > ((nearPlane * 1.01) + 1.0e-3));
}

struct GlassFragment {
    float4 target : SV_Target0;
    float depth : SV_Depth;
};

GlassFragment PSMain(MeshRasterVertex input, uint primitive : SV_PrimitiveID) {
    sdfStampFade(input.draw, uint2(input.position.xy));
    sdfMeshFormBandClip(input.draw, input.world);
    ViewportData view = worldView();
    uint2 pixel = uint2(input.position.xy);
    float t = length(input.world - view.position.xyz);
    uint record = sdfMeshRecord(input.draw);
    uint triangleIndex = sdfMeshRasterTriangle(input, primitive);
    uint first = (3u * triangleIndex);
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
        int materialId = sdfMeshRasterMaterial(input.draw, triangleIndex);
        SdfMaterialData material = sdfMaterialLoad(materialId);
        // A draw a portal plane cuts, and one no ray starts inside, accumulate from the opaque hit, so a face of either
        // behind the opaque hit writes nothing and marks no tile.
        bool relative = (any(sdfMeshClipPlane(record) != 0.0) || sdfGlassBeyondNear(record, view));

        sdfGlassAccumulate(pixel, passGroup.imageExtent, t, start, opaque, front, material.absorption,
            materialId, input.draw, triangleIndex, relative);
    }
    puckCountFragmentWork(0u, 1u);

    GlassFragment fragment;

    fragment.target = float4(t, float(input.draw + 1u), float(triangleIndex), 0.0);
    fragment.depth = ((front && counts) ? input.position.z : 0.0);

    return fragment;
}
