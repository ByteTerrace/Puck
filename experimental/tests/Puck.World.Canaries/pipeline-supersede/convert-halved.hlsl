// The second edit of the middle pass: opaque grayscale of half the history's red channel, so submission n of the
// unchanged accumulator reads min(n/16, 1) / 2.
#include "convert-halved.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    float2 uv = ((float2(id.xy) + 0.5) / float2(passGroup.extent));
    float value = (0.5 * saturate(history.SampleLevel(historySampler, uv, 0.0).r));

    gray[id.xy] = float4(value, value, value, 1.0);
}
