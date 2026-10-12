// Adds 1/16 to the previous submission's history, so submission n holds n/16 in every texel.
#include "accumulate.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    float2 uv = ((float2(id.xy) + 0.5) / float2(passGroup.extent));
    float previous = previousHistory.SampleLevel(previousHistorySampler, uv, 0.0).r;

    history[id.xy] = float4((previous + (1.0 / 16.0)), 0.0, 0.0, 1.0);
}
