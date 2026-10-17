// One group per acquired image. Four lanes sum each of the sixteen cells in source-pixel order; a fixed reduction
// tree publishes the pixel-weighted whole-image mean. No CPU color or displayed-frame guess is read.
#include "../isa/sdf-sky-environment.interface.hlsli"
#include "../isa/sdf-isa.hlsli"
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
    uint width = last.x - first.x;
    uint count = width * (last.y - first.y);
    float3 sum = 0.0;
    uint samples = 0u;
    [loop] for (uint pixel = lane % 4u; pixel < count; pixel += 4u) {
        uint2 at = first + uint2(pixel % width, pixel / width);
        sum += max(screenSources[screen].Load(int3(at, 0)).rgb, 0.0);
        samples++;
    }
    sdfScreenSums[lane] = float4(sum, (float)samples);
    GroupMemoryBarrierWithGroupSync();
    uint writes = 0u;
    if (lane == 0u) {
        float4 total = 0.0;
        [loop] for (uint part = 0u; part < 64u; part++) { total += sdfScreenSums[part]; }
        sdfScreenEmissionRW[screen] = total.w > 0.0
            ? float4(total.rgb / total.w, SDF_SCREEN_EMISSION_DIRECT_GAIN) : 0.0;
        writes++;
    }
    puckCountDetail(0u, 0u, writes, 0u, 0u, samples);
}
