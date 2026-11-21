// The display pass, the last pass of a view whose graph converts for the display (SdfWorldPackage.WithDisplay): one
// invocation an output pixel turns the view's exposed scene light (bloomFine) into the working image, in the register's
// order. The contrast-adaptive sharpen acts on the direct image alone; the camera's scatter (bloomSource, the pyramid's
// finest level, absent without bloom) is added after it, so sharpening never acts on scattered light; the world's tone
// curve follows; the screen fidelity then fades all three toward the scene light as it is; and the sRGB encode, extended
// past one so headroom survives for an HDR display, comes last. A debug view's picture is already display values, so it
// passes through unencoded (displayEncode zero). bloomMix below zero shows the scatter alone (the bloom debug view).
#include "../isa/sdf-optics.interface.hlsli"
#include "Shared/bloom-filter.hlsli"
#include "Shared/display-transform.hlsli"
#include "Shared/transfer.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent;
    uint texels = 0u;
    bloomOutput.GetDimensions(extent.x, extent.y);
    if (all(id.xy < extent)) {
        uint2 sceneExtent;
        bloomFine.GetDimensions(sceneExtent.x, sceneExtent.y);
        float4 direct = bloomFine.Load(int3(id.xy, 0));
        float3 color = (((passGroup.displaySharpness > 0.0) && all(sceneExtent == extent))
            ? puckSharpen(bloomFine, id.xy, sceneExtent, passGroup.displaySharpness)
            : direct.rgb);

        if ((passGroup.bloomScatter > 0.0) || (passGroup.bloomMix < 0.0)) {
            uint2 bloomExtent;
            bloomSource.GetDimensions(bloomExtent.x, bloomExtent.y);
            float3 veil = bloomTent(bloomSource, bloomSampler,
                bloomLinearAxis(id.x, bloomExtent.x, extent.x), bloomLinearAxis(id.y, bloomExtent.y, extent.y), bloomExtent, 1.0);
            color = ((passGroup.bloomMix < 0.0) ? veil : (color + (passGroup.bloomScatter * (veil - bloomSelected(direct.rgb, passGroup.bloomCutoff)))));
        }
        if (passGroup.displayEncode != 0u) {
            color = puckToneCurve(color, passGroup.toneShape, passGroup.toneToe);
            color = lerp(color, direct.rgb, saturate(passGroup.displayFidelity));
            color = puckLinearToSrgbExtended(color);
        }
        bloomOutput[id.xy] = float4(color, direct.a);
        texels = 1u;
    }
    puckCountWork(0u, texels);
}
