// The overlap pass's fragment stage (SdfWorldPackage.WithOverlaps), drawn with the mesh pass's vertex stage over every
// opaque draw the view's frustum keeps, no face culled: each face behind its pixel's visible surface keeps its ray distance
// in the pixel's overlap spans when it is the nearest of its kind (frame/sdf-overlaps.hlsli). No face writes the target:
// the stage returns a zero depth, which fails the pipeline's Greater test against the cleared depth, while its writes to
// the spans stand, and writing a depth keeps every face from an early depth test that could cull it.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-raster.hlsli"
#include "../frame/sdf-stamp-fade.hlsli"
#include "../frame/sdf-visibility.hlsli"
#define SDF_OVERLAPS_ACCUMULATE
#include "../frame/sdf-overlaps.hlsli"

struct OverlapFragment {
    float4 target : SV_Target0;
    float depth : SV_Depth;
};

OverlapFragment PSMain(MeshRasterVertex input, uint primitive : SV_PrimitiveID) {
    sdfStampFade(input.draw, uint2(input.position.xy));

    // Only a covered pixel addresses the spans: a helper lane of a quad that straddles the grid's last row or column lies
    // outside the grid, where its words are past the end of the buffer.
    if (!IsHelperLane()) {
        sdfOverlapAccumulate(uint2(input.position.xy), input.draw, sdfMeshRasterTriangle(input, primitive), input.world);
    }
    puckCountFragmentWork(0u, 1u);

    OverlapFragment fragment;

    fragment.target = float4(0.0, 0.0, 0.0, 0.0);
    fragment.depth = 0.0;

    return fragment;
}
