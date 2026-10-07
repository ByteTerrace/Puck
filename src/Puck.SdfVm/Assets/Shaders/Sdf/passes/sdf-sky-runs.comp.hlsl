// The sky's field runs, one invocation a field-grid texel: evaluated only where its complete lit footprint or its one-pixel
// border is not wholly covered by the color views wrote, the dilation that keeps every texel the composite reads beside
// an edge evaluated. The stack's lowest field run, when it opens with one, composes over nothing, so it writes its offset
// alone (skyBaseRW, black when the stack opens with a point layer); each upper field run writes its scale and offset as
// six half floats across the upper images (skyUpper0RW to skyUpper2RW), only as many as the stack has upper runs
// (sdfSky[0].UpperRuns). The base's alpha says whether the texel was evaluated: a pixel views covers, with all its
// neighbours, evaluates nothing and writes a zero base, and a debug view draws its own pixels, so its sky evaluates
// nothing. Each layer counts its own evaluations in its detail row, each run its texels written in its run's row, and
// an unevaluated pixel its zero base in the plain row.
#include "sdf-sky-pass.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.skyFieldExtent)) {
        return;
    }

    bool seen = false;

    // Include the complete lit footprint and its one-pixel border. A reduced field texel cannot hide an
    // uncovered lit sample at an edge, including an odd-sized final footprint.
    int2 first = int2((id.xy * passGroup.imageExtent) / passGroup.skyFieldExtent) - 1;
    int2 last = int2(((id.xy + 1u) * passGroup.imageExtent + passGroup.skyFieldExtent - 1u) / passGroup.skyFieldExtent);
    [loop] for (int y = first.y; y <= last.y; y++) {
        [loop] for (int x = first.x; x <= last.x; x++) {
            uint2 pixel = uint2(clamp(int2(x, y), int2(0, 0), int2(passGroup.imageExtent) - 1));
            seen = seen || (sdfSkyPassCurrent(pixel) ? lit.Load(int3(pixel, 0)).a < 1.0 : true);
        }
    }

    if (seen && ((passGroup.debugMode == 0u) || (passGroup.debugMode == DebugViewModeSkyCost))) {
        float3 direction = cameraRayDirection(sdfSkyPassView(), (float2(id.xy) + 0.5) / float2(passGroup.skyFieldExtent));
        uint upper = min(sdfSky[0].UpperRuns, SDF_SKY_MAX_UPPER_FIELD_RUNS);
        float3 base;
        float3 scales[SDF_SKY_MAX_UPPER_FIELD_RUNS];
        float3 offsets[SDF_SKY_MAX_UPPER_FIELD_RUNS];

        sdfSkyFieldRuns(direction, base, scales, offsets);
        skyBaseRW[id.xy] = float4(((passGroup.debugMode == DebugViewModeSkyCost) ? sdfSkyCost : base), 1.0);
        puckCountDetail(0u, 0u, 1u, 0u, 0u, 0u);
        if (upper > 0u) {
            skyUpper0RW[id.xy] = float4(scales[0], offsets[0].x);
            skyUpper1RW[id.xy] = float4(offsets[0].yz, scales[1].xy);
            puckCountDetail(1u, 0u, 2u, 0u, 0u, 0u);
            if (upper > 1u) {
                skyUpper2RW[id.xy] = float4(scales[1].z, offsets[1]);
                puckCountDetail(2u, 0u, 1u, 0u, 0u, 0u);
            }
        }
    } else {
        skyBaseRW[id.xy] = float4(0.0, 0.0, 0.0, 0.0);
        sdfWorkTexels = 1u;
    }

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
