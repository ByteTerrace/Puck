// One group per screen slot below the highest acquired image or emitter face (the mask's span, which the host
// dispatches). Four lanes sum each of the sixteen cells over at most a 32 x 32 grid of evenly spaced pixel centres, so
// the cost is bounded however large the image grows; a fixed reduction tree publishes the whole-image mean, in nits. An
// emitter face (SdfScreenFace.Emitter) has no image: its lanes sample a 16 x 16 grid of points on its face through its
// material's paints, as the views pass draws them, and publish the mean painted albedo times its emissive. No CPU
// color or displayed-frame guess is read.
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_GLYPH_ATLAS
#include "../isa/sdf-sky-environment.interface.hlsli"
#include "../isa/sdf-render.hlsli"
#include "../frame/sdf-screen-mapping.hlsli"
#include "../frame/sdf-screen-surface.hlsli"
#include "../field/sdf-component.hlsli"
#include "../field/sdf-hash.hlsli"
#include "../field/sdf-program.hlsli"
#include "../field/sdf-point.hlsli"
#include "../field/sdf-shapes.hlsli"
#include "../shade/sdf-material.hlsli"
#include "../shade/shade-paints.hlsli"
#include "Shared/transfer.hlsli"

// The edge of the grid of face points an emitter face's lanes sample.
static const uint SdfEmitterGridEdge = 16u;

// The sum of an emitter face's painted albedo over this lane's share of its grid, and how many points it took: the face
// read as the views pass reads a hit on it (sdfMaterialPaints, sdfApplyPaints), each point's footprint a grid cell, so
// a paint finer than the grid averages over the cell. A static face's paints are in world space, the frame its hits
// shade in.
float4 sdfEmitterFaceSum(uint screen, int material, uint lane) {
    ScreenSurfaceData surface = worldScreenSurface(screen);
    uint paintFirst;
    uint paintCount;
    float3 base = sdfMaterialPaints(material, paintFirst, paintCount);
    float footprint = ((2.0 * max(surface.right.w, surface.up.w)) / (float)SdfEmitterGridEdge);
    float3 sum = 0.0;
    uint samples = 0u;

    [loop] for (uint index = lane; index < (SdfEmitterGridEdge * SdfEmitterGridEdge); index += 64u) {
        float2 uv = ((((float2(index % SdfEmitterGridEdge, index / SdfEmitterGridEdge) + 0.5) / (float)SdfEmitterGridEdge) * 2.0) - 1.0);
        float3 at = ((surface.origin.xyz + (surface.right.xyz * (uv.x * surface.right.w))) + (surface.up.xyz * (uv.y * surface.up.w)));
        float3 albedo = base;
        float coverage;

        sdfApplyPaints(at, at, footprint, paintFirst, paintCount, false, albedo, coverage);
        sum += albedo;
        samples++;
    }
    return float4(sum, (float)samples);
}

groupshared float4 sdfScreenSums[64];

[numthreads(64, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    uint screen = group.x;
    uint cell = lane / 4u;
    ScreenMappingData mapping = worldScreenMapping(screen);
    uint2 extent = uint2(0u, 0u);
    bool emits = (sdfComponent(passGroup.screenEmissionMask, (screen / 32u)) & (1u << (screen % 32u))) != 0u;
    bool emitter = emits && sdfScreenIsEmitter(mapping);
    bool bound = emits && !emitter && mapping.state.x != 0.0 && mapping.state.z != 0.0 && mapping.imageU.w != 0.0;
    if (bound) { screenSources[screen].GetDimensions(extent.x, extent.y); }
    uint2 cellXY = uint2(cell % SDF_SCREEN_EMISSION_EDGE, cell / SDF_SCREEN_EMISSION_EDGE);
    uint2 first = cellXY * extent / SDF_SCREEN_EMISSION_EDGE;
    uint2 last = (cellXY + 1u) * extent / SDF_SCREEN_EMISSION_EDGE;
    uint2 size = last - first;
    uint2 grid = min(size, uint2(32u, 32u));
    uint count = grid.x * grid.y;
    float3 sum = 0.0;
    uint samples = 0u;
    // A display's picture is a working image, which it emits as the linear light it decodes to; an opening's is linear
    // light already (sampleScreenSurface reads the same split).
    bool display = (mapping.state.w != 0.0);
    [loop] for (uint pixel = lane % 4u; pixel < count; pixel += 4u) {
        uint2 index = uint2(pixel % grid.x, pixel / grid.x);
        uint2 at = first + ((((index * 2u) + 1u) * size) / (grid * 2u));
        float3 value = max(screenSources[screen].Load(int3(at, 0)).rgb, 0.0);
        sum += (display ? puckSrgbToLinearExtended(value) : value);
        samples++;
    }
    if (emitter) {
        float4 face = sdfEmitterFaceSum(screen, (int)mapping.state.y, lane);

        sum = face.rgb;
        samples = (uint)face.w;
    }
    sdfScreenSums[lane] = float4(sum, (float)samples);
    GroupMemoryBarrierWithGroupSync();
    uint writes = 0u;
    if (lane == 0u) {
        float4 total = 0.0;
        [loop] for (uint part = 0u; part < 64u; part++) { total += sdfScreenSums[part]; }
        // The mean picture value times the luminance a value of one shows at (the mapping's z): the face's mean
        // luminance, in nits. A display emits no more than its peak, which its light's range is sized from
        // (SdfWorldTables' screen reaches), so an extended-range picture's mean is held at one.
        float3 mean = (total.w > 0.0) ? (total.rgb / total.w) : 0.0;
        if (display) { mean = min(mean, 1.0); }
        // An emitter face's mean painted albedo shows at its material's emissive, a lane-driven one at its world lane
        // (a static face rides no slot, so its slot lanes are zero).
        float luminance = mapping.state.z;
        if (emitter) {
            SdfMaterialData material = sdfMaterialLoad((int)mapping.state.y);
            sdfApplyEmissiveLane(float4(0.0, 0.0, 0.0, 0.0), material);
            luminance = material.emissive;
        }
        sdfScreenEmissionRW[screen] = total.w > 0.0 ? float4((mean * luminance), 1.0) : 0.0;
        writes++;
    }
    puckCountDetail(0u, 0u, writes, 0u, 0u, samples);
}
