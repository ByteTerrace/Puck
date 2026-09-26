// Colors the simulated ink over a dark background.
// The generated interface declares the frame group, the pass block (extent and config) and the ports.
#include "ink-visualize.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    float2 uv = ((float2(id.xy) + 0.5) / float2(passGroup.extent));
    float ink = simulation.SampleLevel(simulationSampler, uv, 0.0).r;
    float3 background = float3(0.018, 0.027, 0.060);
    // The hue term rises toward the top of the image, where uv.y is zero.
    float3 pigment = (0.5 + (0.5 * cos(float3(0.0, 1.8, 3.6) + (ink * 4.0) + ((1.0 - uv.y) * 1.4))));

    color[id.xy] = float4(lerp(background, (pigment * passGroup.exposure), smoothstep(0.015, 0.8, ink)), 1.0);
}
