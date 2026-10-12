#include "crop-seed.interface.hlsli"

// The four-by-four crop starts at (2,2). Its black guard differs from its edge texels.
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) { return; }
    bool inside = all(id.xy >= uint2(2, 2)) && all(id.xy < uint2(6, 6));
    float2 color = inside ? float2((id.x == 2u) ? 0.25 : 0.75, (id.y == 2u) ? 0.25 : 0.75) : 0.0;
    image[id.xy] = float4(color, 0.0, 1.0);
}
