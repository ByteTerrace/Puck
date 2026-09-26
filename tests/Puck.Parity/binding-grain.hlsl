// The binding station's fragment pass over both groups: it samples its input through the pass group's image and
// sampler at each texel's centre, and adds an integer grain hashed from the pixel, the pass group's seed and the frame
// group's tick, quantized to the flicker rate. Every step is in integer codes, so the stored value is a whole number
// of 255ths and both backends draw the same bytes at the same scheduled tick; ParityBindingReference computes them.
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
    int3 code = (int3)round(saturate(cells.SampleLevel(cellsSampler, uv, 0.0).rgb) * 255.0);
    uint2 pixel = uint2(position.xy);
    uint grainFrame = (frameGroup.tick.x / max(1u, (frameGroup.tickRate / max(1u, passGroup.flickerHz))));
    uint noise = hash((pixel.x + (pixel.y * 4099u)) ^ hash(passGroup.seed ^ (grainFrame * 0x9E3779B9u)));
    int offset = ((int)(noise % ((2u * passGroup.amplitude) + 1u)) - (int)passGroup.amplitude);

    return float4((float3(clamp((code + offset), 0, 255)) / 255.0), 1.0);
}
