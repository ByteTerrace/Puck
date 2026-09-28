// The first compute stage: every texel of the half-float field holds (3/8, 0, 0, 1), and the raw word table holds 96
// at byte 0 and 32 at byte 4.
#include "seed.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    field[id.xy] = float4(0.375, 0.0, 0.0, 1.0);
    if ((id.x == 0) && (id.y == 0)) {
        words.Store2(0, uint2(96, 32));
    }
}
