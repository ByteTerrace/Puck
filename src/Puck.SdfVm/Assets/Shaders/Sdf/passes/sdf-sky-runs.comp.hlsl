// The sky's field runs, one invocation a render-grid pixel: evaluated only where the pixel or one of its eight neighbours
// is not wholly covered by the color views wrote, the one-pixel dilation that keeps every texel the composite reads beside
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
    if (any(id.xy >= passGroup.extent)) {
        return;
    }

    bool seen = false;

    [unroll] for (int dy = -1; dy <= 1; dy++) {
        [unroll] for (int dx = -1; dx <= 1; dx++) {
            seen = (seen || (sdfSkyPassLit((int2(id.xy) + int2(dx, dy))).a < 1.0));
        }
    }

    if (seen && (passGroup.debugMode == 0u)) {
        float3 direction = sdfSkyPassDirection(sdfSkyPassView(), id.xy);
        uint upper = min(sdfSky[0].UpperRuns, SDF_SKY_MAX_UPPER_FIELD_RUNS);
        float3 base;
        float3 scales[SDF_SKY_MAX_UPPER_FIELD_RUNS];
        float3 offsets[SDF_SKY_MAX_UPPER_FIELD_RUNS];

        sdfSkyFieldRuns(direction, base, scales, offsets);
        skyBaseRW[id.xy] = float4(base, 1.0);
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
