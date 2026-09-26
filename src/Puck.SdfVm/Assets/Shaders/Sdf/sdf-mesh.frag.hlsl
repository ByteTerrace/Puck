// The mesh pass's fragment stage: the nearest mesh surface of each covered pixel, which the reversed-Z depth test kept,
// as the ray parameter the SDF march records (the Euclidean distance from the camera, so the hit passes compare the two
// directly), the draw index plus one, and the octahedral normal. The pipeline culls nothing, so the face normal is turned
// toward the camera: a mirrored copy, whose matrix reverses its winding, and an open mesh seen from behind both shade the
// side the camera sees.
#include "sdf-mesh.interface.hlsli"
#include "sdf-viewport.hlsli"
#include "sdf-mesh.hlsli"
#include "sdf-octahedral.hlsli"

float4 PSMain(MeshVertex input) : SV_Target0 {
    ViewportData view = worldViewport((pushedIndex.index >> SdfMeshViewShift));
    float3 toSurface = (input.world - view.position.xyz);
    float3 normal = normalize(input.normal);

    if (dot(normal, toSurface) > 0.0) {
        normal = -normal;
    }

    return float4(length(toSurface), float(((pushedIndex.index & SdfMeshDrawMask) + 1u)), sdfOctEncode(normal));
}
