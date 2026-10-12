// Writes an image whose rows, not any clip space, fix its orientation: row 0 is the image's top, so the top half is
// red and the bottom half blue.
#include "stripes.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    stripes[id.xy] = ((id.y < (passGroup.extent.y / 2))
        ? float4(1.0, 0.0, 0.0, 1.0)
        : float4(0.0, 0.0, 1.0, 1.0));
}
