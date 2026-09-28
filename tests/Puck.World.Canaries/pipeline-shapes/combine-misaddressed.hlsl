// The discriminating second stage: blue reads the word at byte 0 instead of byte 4, so mixed = (3/4, 3/8, 3/8, 1).
#include "combine-misaddressed.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }
    float2 uv = ((float2(id.xy) + 0.5) / float2(passGroup.extent));
    float value = field.SampleLevel(fieldSampler, uv, 0.0).r;

    mixed[id.xy] = float4((2.0 * value), (float(words.Load(0)) / 256.0), (float(words.Load(0)) / 256.0), 1.0);
}
