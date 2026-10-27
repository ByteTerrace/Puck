// Conservative bloom resampling. Reflection is about pixel-cell edges, not their centres. A symmetric
// convolution on that extension preserves both a constant and the sum of the finite image. Resampling
// integrates each destination cell, rather than point-sampling an odd-sized grid, preserving the mean too.
#ifndef PUCK_BLOOM_FILTER_HLSLI
#define PUCK_BLOOM_FILTER_HLSLI

struct BloomAxis {
    float3 position;
    float3 weight;
    uint count;
};

float2 bloomReflect(float2 uv) {
    return 1.0 - abs(1.0 - 2.0 * frac(uv * 0.5));
}

// Pair adjacent cell contributions into one hardware-linear sample. Integer-offset convolutions commute
// with these pairs, including reflected pairs at a boundary. Zero pairs are never sampled.
BloomAxis bloomBoxAxis(uint pixel, uint source, uint destination) {
    BloomAxis axis = (BloomAxis)0;
    uint numerator = pixel * source;
    int first = int(numerator / destination);
    float left = float(numerator % destination) / float(destination);
    float length = float(source) / float(destination);
    float right = left + length;
    [unroll] for (uint pair = 0; pair < 3u; pair++) {
        float cell = float(pair * 2u);
        float a = max(0.0, min(right, cell + 1.0) - max(left, cell));
        float b = max(0.0, min(right, cell + 2.0) - max(left, cell + 1.0));
        float weight = a + b;
        if (weight > 0.0) {
            axis.position[pair] = (float(first) + cell + 0.5 + b / weight) / float(source);
            axis.weight[pair] = weight / length;
            axis.count = pair + 1u;
        }
    }
    return axis;
}

// Integral of the unit triangular reconstruction basis, in local coordinates to avoid cancellation on
// large images. Its reflected finite-domain integral is exactly one source cell per source value.
float bloomHatIntegral(float x) {
    float a = saturate(x + 1.0);
    float b = saturate(1.0 - x);
    return x < 0.0 ? 0.5 * a * a : 1.0 - 0.5 * b * b;
}

BloomAxis bloomLinearAxisLocal(int first, float left, float length, uint source) {
    BloomAxis axis = (BloomAxis)0;
    float right = left + length;
    [unroll] for (uint pair = 0; pair < 2u; pair++) {
        float cell = float(pair * 2u);
        float a = max(0.0, bloomHatIntegral(right - cell) - bloomHatIntegral(left - cell));
        float b = max(0.0, bloomHatIntegral(right - cell - 1.0) - bloomHatIntegral(left - cell - 1.0));
        float weight = a + b;
        if (weight > 0.0) {
            axis.position[pair] = (float(first) + cell + 0.5 + b / weight) / float(source);
            axis.weight[pair] = weight / length;
            axis.count = pair + 1u;
        }
    }
    return axis;
}

BloomAxis bloomLinearAxis(uint pixel, uint source, uint destination) {
    uint numerator = pixel * source;
    int first = int(numerator / destination);
    float fraction = float(numerator % destination) / float(destination);
    if (fraction < 0.5) { first--; fraction += 1.0; }
    return bloomLinearAxisLocal(first, fraction - 0.5, float(source) / float(destination), source);
}

BloomAxis bloomLinearAxisRange(float left, float length, uint source) {
    int first = int(floor(left - 0.5));
    return bloomLinearAxisLocal(first, left - 0.5 - float(first), length, source);
}

float3 bloomAreaSample(Texture2D<float4> image, SamplerState filter, BloomAxis x, BloomAxis y, float2 offset) {
    float3 color = 0.0;
    [loop] for (uint j = 0u; j < y.count; j++) {
        [loop] for (uint i = 0u; i < x.count; i++) {
            color += x.weight[i] * y.weight[j] * image.SampleLevel(filter,
                bloomReflect(float2(x.position[i], y.position[j]) + offset), 0).rgb;
        }
    }
    return color;
}

float3 bloomIntegerTent(Texture2D<float4> image, SamplerState filter, BloomAxis x, BloomAxis y, float2 texel, float spacing) {
    float3 color = 0.0;
    [unroll] for (int j = -1; j <= 1; j++) {
        [unroll] for (int i = -1; i <= 1; i++) {
            color += float((2 - abs(i)) * (2 - abs(j))) / 16.0 *
                bloomAreaSample(image, filter, x, y, float2(i, j) * texel * spacing);
        }
    }
    return color;
}

float3 bloomTent(Texture2D<float4> image, SamplerState filter, BloomAxis x, BloomAxis y, uint2 source, float spacing) {
    float lower = floor(spacing);
    float fraction = spacing - lower;
    float2 texel = 1.0 / float2(source);
    float3 color = bloomIntegerTent(image, filter, x, y, texel, lower);
    // Interpolate whole symmetric kernels. A fractional shift of one already-resampled point would
    // combine the two interpolations, breaking the conservative resampling at odd grid boundaries.
    if (fraction > 0.0) {
        color = lerp(color, bloomIntegerTent(image, filter, x, y, texel, lower + 1.0), fraction);
    }
    return color;
}
#endif
