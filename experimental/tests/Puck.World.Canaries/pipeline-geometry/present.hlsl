// Samples the layered color image by the fullscreen adapter's top-left UV, so the published image shows the layers the
// right way up on every backend. Both passes that run it name their input "as": "layers".
#include "present.interface.hlsli"

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    return layers.SampleLevel(layersSampler, uv, 0.0);
}
