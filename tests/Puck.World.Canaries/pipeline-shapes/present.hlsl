// The fullscreen stage, drawn through the POSITION vertex adapter: it copies mixed and adds 1/2 to blue on the right
// half of the image, so its own contribution depends on the adapter's UV.
#include "present.interface.hlsli"

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    float4 value = mixed.Sample(mixedSampler, uv);

    return float4(value.r, value.g, (value.b + ((uv.x >= 0.5) ? 0.5 : 0.0)), 1.0);
}
