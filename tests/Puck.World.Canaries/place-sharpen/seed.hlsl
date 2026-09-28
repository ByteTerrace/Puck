// Rows 0..31 carry a softened step in exact eight-bit codes. Rows 32..47 carry a full-contrast step.
#include "seed.interface.hlsli"
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) { return; }
    float value = (id.y < 32u)
        ? ((id.x < 7u) ? 64.0 : (id.x == 7u) ? 96.0 : (id.x == 8u) ? 160.0 : 192.0) / 255.0
        : ((id.x < 8u) ? 0.0 : 1.0);
    step[id.xy] = float4(value, value, value, 1.0);
}
