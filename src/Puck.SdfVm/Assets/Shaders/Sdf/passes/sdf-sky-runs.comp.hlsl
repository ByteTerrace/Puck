// The sky's field runs, one invocation an output pixel: evaluated only where the pixel or one of its eight neighbours is
// not wholly covered by the lit image, the one-pixel dilation that keeps every texel the composite reads beside an edge
// written. The lowest run, the gradient, composes over nothing, so it writes its offset alone (skyBaseRW); the cloud run
// above the point run writes its scale and offset (skyScaleRW, skyOffsetRW). A pixel the lit image covers, with all its
// neighbours, evaluates and writes nothing, and a debug view draws its own pixels, so its sky evaluates nothing. Each
// pixel evaluated counts one sky evaluation (gpu.sky.evaluations) and its texel.
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

    uint evaluations = 0u;

    if (seen && (passGroup.debugMode == 0u)) {
        float3 direction = sdfSkyPassDirection(sdfSkyPassView(), id.xy);
        float3 scale;
        float3 offset;

        sdfSkyCloudRun(direction, scale, offset);
        skyBaseRW[id.xy] = float4(sdfSkyGradient(direction), 1.0);
        skyScaleRW[id.xy] = float4(scale, 1.0);
        skyOffsetRW[id.xy] = float4(offset, 1.0);
        sdfWorkTexels = 1u;
        evaluations = 1u;
    }

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountSky(evaluations);
}
