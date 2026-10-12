// Exact UNORM codes keep the sharpen oracle independent of a captured baseline.
#include "seed.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) { return; }
    float code = (id.y < 32u)
        ? ((id.x < 7u) ? 64.0 : (id.x == 7u) ? 96.0 : (id.x == 8u) ? 160.0 : 192.0)
        : ((id.x < 8u) ? 0.0 : 255.0);
    step[id.xy] = float4((code / 255.0).xxx, 1.0);
}
