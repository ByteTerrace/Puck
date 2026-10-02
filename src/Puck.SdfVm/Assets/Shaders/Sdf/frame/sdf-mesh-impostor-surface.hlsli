// What the hit passes read of an impostor card's pixel: the surface the camera's ray meets in the impostor's views
// (frame/sdf-mesh-impostor.hlsli), at the ray parameter the mesh pass wrote, as the albedo, the world normal and the light
// the three nearest views show there, each weighted by its view's weight and by the coverage its texel holds, so a view that
// shows nothing at the point does not dim or bend it. The world kernels' interface declares the impostor atlases
// (sdfImpostorAlbedo, sdfImpostorNormals, sdfImpostorMaterials, sdfImpostorEmission) beside the depth atlas the trace reads; the mesh pass's does
// not, so its stages never include this module. Each atlas is read at the level the pixel's footprint wants, bilinear inside
// the view's tile at that level, as frame/sdf-mesh-textures.hlsli reads a mesh's. The material is an identity, never
// filtered: the texel the highest-weighted view with a covered nearest texel holds at the pixel's level, whose entry the includer adds to the draw's
// material as a mesh's texel entry is. KEEP IN SYNC with SdfBakedImpostor and
// SdfMeshImpostor (the albedo is sRGB-encoded with coverage in alpha, the normal an octahedral pair in the prototype's
// frame, the emission linear).
#ifndef FRAME_SDF_MESH_IMPOSTOR_SURFACE_HLSLI
#define FRAME_SDF_MESH_IMPOSTOR_SURFACE_HLSLI

#include "../field/sdf-octahedral.hlsli"
#include "sdf-mesh-textures.hlsli"
#include "sdf-mesh-impostor.hlsli"

struct SdfImpostorSurface {
    float3 albedo;   // linear
    float3 normal;   // world, facing the camera
    float3 emission; // linear light
    int material;    // the winning view's texel entry, added to the draw's material
};

// One view's atlas value at a point of its plane and a level: bilinear inside the view's tile.
float4 sdfImpostorFiltered(Texture2D<float4> atlas, SdfImpostor impostor, uint2 cell, float2 ab, uint level) {
    uint width;
    uint height;
    uint levels;

    atlas.GetDimensions(level, width, height, levels);

    float2 size = float2((float)width, (float)height);
    SdfImpostorTile tile = sdfImpostorTileOf(impostor, cell, level, size);
    float2 local = clamp((sdfImpostorTileFraction(ab) * tile.edge), 0.5, (tile.edge - 0.5));

    return atlas.SampleLevel(samplers[SdfMeshLinearSampler], ((tile.origin + local) / size), (float)level);
}
// One view's object-space normal at a point of its plane and a level: the four texels around it, each decoded, weighted.
float3 sdfImpostorNormalFiltered(SdfImpostor impostor, uint2 cell, float2 ab, uint level) {
    uint width;
    uint height;
    uint levels;

    sdfImpostorNormals.GetDimensions(level, width, height, levels);

    SdfImpostorTile tile = sdfImpostorTileOf(impostor, cell, level, float2((float)width, (float)height));
    float2 at = ((tile.origin + clamp((sdfImpostorTileFraction(ab) * tile.edge), 0.5, (tile.edge - 0.5))) - 0.5);
    int2 low = int2(max(floor(at), tile.origin));
    int2 high = int2(min((float2(low) + 1.0), ((tile.origin + tile.edge) - 1.0)));
    float2 weight = frac(at);
    float3 d00 = sdfOctDecode(((sdfImpostorNormals.Load(int3(low, (int)level)).rg * 2.0) - 1.0));
    float3 d10 = sdfOctDecode(((sdfImpostorNormals.Load(int3(int2(high.x, low.y), (int)level)).rg * 2.0) - 1.0));
    float3 d01 = sdfOctDecode(((sdfImpostorNormals.Load(int3(int2(low.x, high.y), (int)level)).rg * 2.0) - 1.0));
    float3 d11 = sdfOctDecode(((sdfImpostorNormals.Load(int3(high, (int)level)).rg * 2.0) - 1.0));

    return lerp(lerp(d00, d10, weight.x), lerp(d01, d11, weight.x), weight.y);
}
// The material entry of one view's texel at a point of its plane and a level: the nearest texel, unfiltered.
int sdfImpostorMaterialAt(SdfImpostor impostor, uint2 cell, float2 ab, uint level) {
    uint width;
    uint height;
    uint levels;

    sdfImpostorMaterials.GetDimensions(level, width, height, levels);

    SdfImpostorTile tile = sdfImpostorTileOf(impostor, cell, level, float2((float)width, (float)height));
    float2 local = clamp(floor((sdfImpostorTileFraction(ab) * tile.edge)), 0.0, (tile.edge - 1.0));

    return (int)round((sdfImpostorMaterials.Load(int3(int2((tile.origin + local)), (int)level)).r * 255.0));
}
// The surface of a card's pixel at ray parameter `t` along the unit `direction` from `origin`. `pixelAngle` is a pixel's
// world size per unit of distance.
SdfImpostorSurface sdfImpostorSurfaceAt(uint draw, float3 origin, float3 direction, float t, float pixelAngle) {
    uint record = sdfMeshRecord(draw);
    SdfImpostor impostor = sdfImpostorOf(record);
    float3 start = ((sdfMeshObjectFromWorld(record, (origin - sdfMeshRow(record, 3u))) - impostor.center) / impostor.radius);
    float3 travel = (sdfMeshObjectFromWorld(record, direction) / impostor.radius);
    float3 spot = (start + (travel * t));
    uint width;
    uint height;
    uint levels;

    sdfImpostorDepth.GetDimensions(0u, width, height, levels);

    uint level = sdfImpostorLevel(impostor, record, origin, pixelAngle, max(levels, 1u));
    SdfImpostorViews views = sdfImpostorViewsAt(impostor, normalize(-travel));
    float3 albedo = float3(0.0, 0.0, 0.0);
    float3 normal = float3(0.0, 0.0, 0.0);
    float3 emission = float3(0.0, 0.0, 0.0);
    float total = 0.0;
    float3 plain = float3(0.0, 0.0, 0.0);
    float heaviest = -1.0;
    int material = 0;

    [unroll] for (uint view = 0u; view < 3u; view++) {
        float3 toward = sdfImpostorViewDirection(views.cell[view].x, views.cell[view].y, impostor.views);
        float3 right;
        float3 up;

        sdfImpostorViewBasis(toward, right, up);

        float2 ab = float2(dot(spot, right), dot(spot, up));
        float4 color = sdfImpostorFiltered(sdfImpostorAlbedo, impostor, views.cell[view], ab, level);
        float weight = (views.weight[view] * color.a);
        float3 direction3 = sdfImpostorNormalFiltered(impostor, views.cell[view], ab, level);
        float3 light = max(sdfImpostorFiltered(sdfImpostorEmission, impostor, views.cell[view], ab, level).rgb, 0.0);

        albedo += (weight * color.rgb);
        normal += (weight * direction3);
        emission += (weight * light);
        total += weight;

        // The material is the highest-weighted view's whose nearest texel at this level is covered (the trace's own
        // rule), never the filtered alpha's, which a neighbouring covered texel can lift over an uncovered one.
        if (sdfImpostorCovered(impostor, views.cell[view], spot, level) && (views.weight[view] > heaviest)) {
            heaviest = views.weight[view];
            material = sdfImpostorMaterialAt(impostor, views.cell[view], ab, level);
        }
        plain += (views.weight[view] * direction3);
    }

    SdfImpostorSurface surface;
    float3 objectNormal = ((total > 1.0e-4) ? normal : plain);

    albedo = ((total > 1.0e-4) ? saturate((albedo / total)) : float3(0.0, 0.0, 0.0));
    surface.albedo = lerp(pow(((albedo + 0.055) / 1.055), 2.4), (albedo / 12.92), step(albedo, 0.04045));
    surface.material = material;
    surface.emission = ((total > 1.0e-4) ? (emission / total) : float3(0.0, 0.0, 0.0));

    float3 worldNormal = normalize(sdfMeshNormalToWorld(record, objectNormal));

    surface.normal = ((dot(worldNormal, direction) > 0.0) ? -worldNormal : worldNormal);

    return surface;
}

#endif
