// The broken edit of the middle pass: it reads a brightness nothing declares, so the pass does not compile.
#include "convert-broken.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    float2 uv = ((float2(id.xy) + 0.5) / float2(passGroup.extent));
    float value = saturate(history.SampleLevel(historySampler, uv, 0.0).r * brightness);

    gray[id.xy] = float4(value, value, value, 1.0);
}
