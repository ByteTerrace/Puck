// One group per acquired image. Four lanes sum each of the sixteen cells over at most a 32 x 32 grid of evenly
// spaced pixel centres, so the cost is bounded however large the image grows; a fixed reduction tree publishes the
// whole-image mean, in nits. No CPU color or displayed-frame guess is read.
#include "../isa/sdf-sky-environment.interface.hlsli"
#include "../isa/sdf-render.hlsli"
#include "../frame/sdf-screen-mapping.hlsli"

groupshared float4 sdfScreenSums[64];

[numthreads(64, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    uint screen = group.x;
    uint cell = lane / 4u;
    ScreenMappingData mapping = worldScreenMapping(screen);
    uint2 extent = uint2(0u, 0u);
    bool bound = (passGroup.screenEmissionMask & (1u << screen)) != 0u &&
        mapping.state.x != 0.0 && mapping.state.z != 0.0 && mapping.imageU.w != 0.0;
    if (bound) { screenSources[screen].GetDimensions(extent.x, extent.y); }
    uint2 cellXY = uint2(cell % SDF_SCREEN_EMISSION_EDGE, cell / SDF_SCREEN_EMISSION_EDGE);
    uint2 first = cellXY * extent / SDF_SCREEN_EMISSION_EDGE;
    uint2 last = (cellXY + 1u) * extent / SDF_SCREEN_EMISSION_EDGE;
    uint2 size = last - first;
    uint2 grid = min(size, uint2(32u, 32u));
    uint count = grid.x * grid.y;
    float3 sum = 0.0;
    uint samples = 0u;
    [loop] for (uint pixel = lane % 4u; pixel < count; pixel += 4u) {
        uint2 index = uint2(pixel % grid.x, pixel / grid.x);
        uint2 at = first + ((((index * 2u) + 1u) * size) / (grid * 2u));
        sum += max(screenSources[screen].Load(int3(at, 0)).rgb, 0.0);
        samples++;
    }
    sdfScreenSums[lane] = float4(sum, (float)samples);
    GroupMemoryBarrierWithGroupSync();
    uint writes = 0u;
    if (lane == 0u) {
        float4 total = 0.0;
        [loop] for (uint part = 0u; part < 64u; part++) { total += sdfScreenSums[part]; }
        // The mean picture value times the luminance a value of one shows at (the mapping's z): the face's mean
        // luminance, in nits.
        sdfScreenEmissionRW[screen] = total.w > 0.0
            ? float4(((total.rgb / total.w) * mapping.state.z), 1.0) : 0.0;
        writes++;
    }
    puckCountDetail(0u, 0u, writes, 0u, 0u, samples);
}
