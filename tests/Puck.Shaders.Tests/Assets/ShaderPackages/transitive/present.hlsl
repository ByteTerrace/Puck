// The fullscreen finish. Its includes sit below the first line, so only an include walk that reads every line
// reaches tone.hlsli.
#include "lib/common.hlsli"
#include "lib/tone.hlsli"

[[vk::binding(0, 0)]] Texture2D<float4> fieldImage : register(t0);
[[vk::binding(0, 0)]] SamplerState fieldSampler : register(s0);

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    return Tone(fieldImage.Sample(fieldSampler, uv) * Brighten(1.0));
}
