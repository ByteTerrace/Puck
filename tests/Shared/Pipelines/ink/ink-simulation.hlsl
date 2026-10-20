// Ink is advected through a slowly turning flow and retained in a floating-point history image.
// The generated interface declares the frame group, the pass block (extent and config) and the ports.
#include "ink-simulation.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    // Image coordinates: the origin is the top-left corner and y grows downward, as the history is stored.
    float2 extent = float2(passGroup.extent);
    float2 uv = ((float2(id.xy) + 0.5) / extent);
    float2 center = (uv - 0.5);
    float2 flow = (float2(-center.y, center.x) * 0.004);
    float4 previous = previousSimulation.SampleLevel(previousSimulationSampler, saturate(uv - flow), 0.0);
    float2 pen = (0.5 + (0.27 * float2(cos(frameGroup.time * 0.91), sin(frameGroup.time * 1.17))));

    if (frameGroup.pointerDown != 0) {
        pen = (frameGroup.pointer / extent);
    }
    float2 offset = (uv - pen);
    float ink = exp(-dot(offset, offset) * 1700.0);
    float value = max((previous.r * passGroup.decay), ink);

    simulation[id.xy] = float4(value, previous.r, 0.0, 1.0);
}
