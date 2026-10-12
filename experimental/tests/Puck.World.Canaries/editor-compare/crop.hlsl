#include "crop.interface.hlsli"
#include "../../../src/Puck.Shaders/Assets/Shaders/Shared/reconstruction.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) { return; }
    expanded[id.xy] = puckReconstructRegion(image, id.xy, uint2(16, 16), uint2(4, 4), uint2(2, 2), passGroup.sharpness);
    if (all(id.xy < uint2(4, 4))) {
        exact[id.xy] = puckReconstructRegion(image, id.xy, uint2(4, 4), uint2(4, 4), uint2(2, 2), passGroup.sharpness);
    }
}
