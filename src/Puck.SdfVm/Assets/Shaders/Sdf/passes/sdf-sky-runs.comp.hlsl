// The sky's field runs, one invocation a render-grid pixel: evaluated only where the pixel or one of its eight neighbours
// is not wholly covered by the color views wrote, the one-pixel dilation that keeps every texel the composite reads beside
// an edge evaluated. The lowest run, the gradient, composes over nothing, so it writes its offset alone (skyBaseRW); the
// cloud run above the point run writes its scale and offset (skyScaleRW, skyOffsetRW). The base's alpha says whether the
// texel was evaluated: a pixel views covers, with all its neighbours, evaluates nothing and writes a zero base, and a
// debug view draws its own pixels, so its sky evaluates nothing. Each layer counts its own evaluations
// (gpu.sky.evaluations) in its detail row, and each pixel counts its texel in the plain row.
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
        float3 scale;
        float3 offset;

        sdfSkyCloudRun(direction, scale, offset);
        skyBaseRW[id.xy] = float4(sdfSkyGradient(direction), 1.0);
        skyScaleRW[id.xy] = float4(scale, 1.0);
        skyOffsetRW[id.xy] = float4(offset, 1.0);
    } else {
        skyBaseRW[id.xy] = float4(0.0, 0.0, 0.0, 0.0);
    }
    sdfWorkTexels = 1u;

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
