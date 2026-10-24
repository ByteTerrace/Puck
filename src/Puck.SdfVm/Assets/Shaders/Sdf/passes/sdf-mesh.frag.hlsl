// The mesh pass's fragment stage: the nearest mesh surface of each covered pixel, which the reversed-Z depth test kept,
// as the ray parameter (the Euclidean distance from the camera along the normalized ray), the draw index plus one, the
// triangle, whose surface the surface pass resolves (sdfMeshSurfaceNormal, sdfMeshMaterial), and the forward depth
// (the distance along the camera's forward axis), from which the occlusion kernel builds its depth pyramid
// (sdf-mesh-occlusion.comp.hlsl). The first three are whole numbers or a distance a float holds exactly.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-raster.hlsli"
#include "../frame/sdf-stamp-fade.hlsli"

// Discard must precede depth writes: an early-depth annotation would leave invisible avatar depth in the holes.
float4 PSMain(MeshRasterVertex input) : SV_Target0 {
    sdfStampFade(input.draw, uint2(input.position.xy));
    ViewportData view = worldView();
    float3 relative = (input.world - view.position.xyz);

    puckCountFragmentWork(0u, 1u);

    return float4(length(relative), float(input.draw + 1u), float(input.triangleIndex), dot(relative, view.forward.xyz));
}
