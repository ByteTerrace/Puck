// Fills the pane with opaque magenta, a colour the world beside it never shows.
#include "solid.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }

    output[id.xy] = float4(1.0, 0.0, 1.0, 1.0);
}
