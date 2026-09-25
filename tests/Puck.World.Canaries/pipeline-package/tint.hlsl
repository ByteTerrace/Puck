// Every pixel is (gain, 1 - gain, 0.5), so a captured region's value is the gain's own arithmetic.
// The generated interface declares the frame group, the pass block (extent and gain) and the output, 'image'.
#include "tint.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    image[id.xy] = float4(passGroup.gain, (1.0 - passGroup.gain), 0.5, 1.0);
}
