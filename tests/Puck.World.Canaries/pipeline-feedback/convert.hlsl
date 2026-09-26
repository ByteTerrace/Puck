// Converts the history's red channel to opaque grayscale, saturated at full scale.
#include "convert.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    float2 uv = ((float2(id.xy) + 0.5) / float2(passGroup.extent));
    float value = saturate(history.SampleLevel(historySampler, uv, 0.0).r);

    gray[id.xy] = float4(value, value, value, 1.0);
}
