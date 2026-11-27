// The cascade's glass draws run after its opaque depths. Integrate extinction to the first opaque receiver using the
// same signed optical-depth arithmetic as the camera glass pass, one sum a channel where the buffer holds the colour
// plane (frame/sdf-shadow-optical.hlsli) and the red sum alone otherwise. Returning zero depth forces the late depth
// test to fail under the Greater compare (the exit depth is non-negative), preserving the cascade's opaque exit layer
// while every glass face contributes, irrespective of draw order.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-raster.hlsli"
#include "../frame/sdf-optical-depth.hlsli"
#include "../frame/sdf-shadow-optical.hlsli"
#include "../field/sdf-program.hlsli"
#include "../shade/sdf-material.hlsli"

float PSMain(MeshRasterVertex input, uint primitive : SV_PrimitiveID) : SV_Depth {
    uint triangleIndex = sdfMeshRasterTriangle(input, primitive);
    float3 extinction = sdfMaterialShadowExtinction(sdfMeshRasterMaterial(input.draw, triangleIndex));
    if (!any(extinction > 0.0)) { return 0.0; }
    ViewportData view = worldView();
    uint2 pixel = uint2(input.position.xy);
    float near = worldNearDistance(view);
    float range = worldFarDistance(view) - near;
    float t = dot(input.world - view.position.xyz, view.forward.xyz) - near;
    float receiver = (1.0 - meshVisibility.Load(int3(pixel, 0)).x) * range;
    if (t < receiver && !IsHelperLane()) {
        uint record = sdfMeshRecord(input.draw);
        uint first = 3u * triangleIndex;
        float3 a = sdfMeshWorldPosition(record, first);
        bool front = dot(cross(sdfMeshWorldPosition(record, first + 1u) - a, sdfMeshWorldPosition(record, first + 2u) - a), view.forward.xyz) < 0.0;
        uint width;
        uint height;
        meshVisibility.GetDimensions(width, height);
        uint2 tile = pixel / SDF_SHADOW_OPTICAL_TILE_EDGE;
        uint index = tile.y * sdfShadowOpticalTilesAcross(width) + tile.x;
        uint firstTile = WaveReadLaneFirst(index);
        if (index != firstTile || WaveIsFirstLane()) {
            InterlockedOr(sdfGlassSumsRW[sdfShadowOpticalBitmap(width) + index / 32u], 1u << (index % 32u));
        }
        uint word = sdfShadowOpticalWord(width, pixel);
        uint words;
        uint stride;
        sdfGlassSumsRW.GetDimensions(words, stride);
        sdfOpticalDepthAccumulate(sdfGlassSumsRW, word, t, receiver, front, extinction.x);
        if (sdfShadowOpticalHoldsColor(words, width)) {
            uint color = sdfShadowOpticalColorWord(width, pixel);
            sdfOpticalDepthAccumulate(sdfGlassSumsRW, color, t, receiver, front, extinction.y);
            sdfOpticalDepthAccumulate(sdfGlassSumsRW, color + 2u, t, receiver, front, extinction.z);
        }
        if (front) {
            uint ignored;
            InterlockedMax(sdfGlassSumsRW[word + 2u], ~asuint(max(t, 0.0)), ignored);
        }
    }
    puckCountFragmentWork(0u, 1u);
    return 0.0;
}
