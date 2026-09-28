// The binding station's seed: an integer pattern of the pixel coordinates, each channel a whole number of 255ths, so
// both backends write the same bytes.
#include "binding-seed.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint2 extent = passGroup.extent;

    if (any(id.xy >= extent)) {
        return;
    }

    uint r = (((id.x * 37u) + (id.y * 11u)) & 255u);
    uint g = (((id.x ^ id.y) * 5u) & 255u);
    uint b = ((id.y * 255u) / max(1u, (extent.y - 1u)));

    pattern[id.xy] = float4((float3(r, g, b) / 255.0), 1.0);
}
