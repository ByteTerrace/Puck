// The cascade's glass draws run after its opaque depths. Integrate extinction to the first opaque receiver using the
// same signed optical-depth arithmetic as the camera glass pass. Returning zero depth forces the late depth test to
// fail under the Greater compare (the exit depth is non-negative), preserving the cascade's opaque exit layer while every glass face contributes, irrespective of draw order.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-raster.hlsli"
#include "../frame/sdf-optical-depth.hlsli"
#include "../field/sdf-program.hlsli"
#include "../shade/sdf-material.hlsli"

float PSMain(MeshRasterVertex input) : SV_Depth {
    float density = sdfMaterialShadowDensity(sdfMeshMaterial(input.draw, input.triangleIndex));
    if (!(density > 0.0)) { return 0.0; }
    ViewportData view = worldView();
    uint2 pixel = uint2(input.position.xy);
    float near = worldNearDistance(view);
    float range = worldFarDistance(view) - near;
    float t = dot(input.world - view.position.xyz, view.forward.xyz) - near;
    float receiver = (1.0 - meshVisibility.Load(int3(pixel, 0)).x) * range;
    if (density > 0.0 && t < receiver && !IsHelperLane()) {
        uint record = sdfMeshRecord(input.draw);
        uint first = 3u * input.triangleIndex;
        float3 a = sdfMeshWorldPosition(record, first);
        bool front = dot(cross(sdfMeshWorldPosition(record, first + 1u) - a, sdfMeshWorldPosition(record, first + 2u) - a), view.forward.xyz) < 0.0;
        uint width;
        uint height;
        meshVisibility.GetDimensions(width, height);
        uint word = 3u * (pixel.y * width + pixel.x);
        sdfOpticalDepthAccumulate(sdfGlassSumsRW, word, t, receiver, front, density);
        if (front) {
            uint ignored;
            InterlockedMax(sdfGlassSumsRW[word + 2u], ~asuint(max(t, 0.0)), ignored);
        }
    }
    puckCountFragmentWork(0u, 1u);
    return 0.0;
}
