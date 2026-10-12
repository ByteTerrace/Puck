// Copies the grayscale image through the fullscreen path unchanged.
#include "copy.interface.hlsli"

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    return gray.Sample(graySampler, uv);
}
