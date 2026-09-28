// Every pixel is (gain, 1 - gain, blue), so a captured region's value is the gain's own arithmetic and the blue names
// the variant that drew it: 0.5 for the default variant, and 0.25 (tier + 1) for a tier's variant, which compiles with
// PUCK_QUALITY_TIER defined. The graph declares the high tier alone, whose variant writes 0.75.
// The generated interface declares the frame group, the pass block (extent and gain) and the output, 'image'.
#include "tint.interface.hlsli"

#if defined(PUCK_QUALITY_TIER)
static const float TierBlue = (0.25 * (PUCK_QUALITY_TIER + 1));
#else
static const float TierBlue = 0.5;
#endif

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    image[id.xy] = float4(passGroup.gain, (1.0 - passGroup.gain), TierBlue, 1.0);
}
