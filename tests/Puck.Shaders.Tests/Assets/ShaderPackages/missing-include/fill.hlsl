// Names an include that no file holds, below one that exists.
#include "present.hlsli"
#include "absent.hlsli"
#include "fill.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    image[id.xy] = Fill();
}
