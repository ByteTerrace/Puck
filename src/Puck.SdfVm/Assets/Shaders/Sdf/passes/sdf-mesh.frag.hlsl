// The mesh pass's fragment stage: the nearest mesh surface of each covered pixel, which the reversed-Z depth test kept,
// as the ray parameter the SDF march records (the Euclidean distance from the camera, so the hit passes compare the two
// directly), the draw index plus one, and the triangle, whose surface the hit passes resolve (sdfMeshSurfaceNormal,
// sdfMeshMaterial). Each is a whole number a float holds exactly.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"

// The depth test runs before the stage, so a fragment it rejects never runs and never counts; each one that runs writes
// its texel and counts it.
[earlydepthstencil]
float4 PSMain(MeshVertex input) : SV_Target0 {
    ViewportData view = worldView();

    puckCountFragmentWork(0u, 1u);

    float3 offset = input.world - view.position.xyz;
    float depth = passGroup.lightMap != 0u ? dot(offset, view.forward.xyz) : length(offset);
    return float4(depth, float(((pushedIndex.index & SdfMeshDrawMask) + 1u)), float(input.triangleIndex), 0.0);
}
