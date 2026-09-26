// The binding station's fragment pass over both groups: it samples its input through the pass group's image and
// sampler, and adds an integer-hashed grain keyed by the pixel, the pass group's seed and the frame group's tick,
// quantized to the flicker rate, so both backends draw the same grain at the same scheduled tick.
#include "binding-grain.interface.hlsli"

uint hash(uint value) {
    value ^= (value >> 16u);
    value *= 0x7FEB352Du;
    value ^= (value >> 15u);
    value *= 0x846CA68Bu;
    value ^= (value >> 16u);

    return value;
}

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    float3 color = cells.SampleLevel(cellsSampler, uv, 0.0).rgb;
    uint2 pixel = uint2(position.xy);
    uint grainFrame = (frameGroup.tick.x / max(1u, (frameGroup.tickRate / max(1u, passGroup.flickerHz))));
    uint noise = hash((pixel.x + (pixel.y * 4099u)) ^ hash(passGroup.seed ^ (grainFrame * 0x9E3779B9u)));
    float offset = ((((float)(noise & 255u) / 255.0) * 2.0) - 1.0);

    return float4(saturate(color + (offset * passGroup.intensity)), 1.0);
}
