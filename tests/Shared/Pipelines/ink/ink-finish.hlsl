// Darkens the colored ink toward the corners.
// The generated interface declares the frame group, the pass block and the input, 'color'.
#include "ink-finish.interface.hlsli"

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    float3 source = color.Sample(colorSampler, uv).rgb;
    float2 offset = uv - 0.5;
    float vignette = 1.0 - 0.55 * dot(offset, offset);
    return float4(source * vignette, 1);
}
