// A non-negative RGB colour in one word: three nine-bit mantissas under one five-bit exponent (the layout of
// DXGI_FORMAT_R9G9B9E5_SHAREDEXP and VK_FORMAT_E5B9G9R9_UFLOAT_PACK32: red in bits 0 to 8, green in 9 to 17, blue in 18
// to 26, the exponent in 27 to 31, biased by fifteen). The largest channel keeps nine bits and the others are held to
// its step, so the error of any channel is at most 1/1022 of the largest: a store for light that is read back and
// blended (a blended irradiance cube, a history sample), where a half float a channel spends twice the words. A channel
// below zero or not a number packs as zero, and one above PuckSharedExponentMax as that.
#ifndef PUCK_SHARED_SHARED_EXPONENT_HLSLI
#define PUCK_SHARED_SHARED_EXPONENT_HLSLI

// The largest value a channel holds: 511/512 of 2^16.
static const float PuckSharedExponentMax = 65408.0;

uint puckPackSharedExponent(float3 color) {
    // max(x, 0) answers zero for a NaN operand, and the min holds an infinity.
    float3 held = min(max(color, 0.0), PuckSharedExponentMax);
    float largest = max(max(held.r, held.g), held.b);
    // The exponent whose step gives the largest channel nine bits, never below the format's least.
    int exponent = (max(-16, (int)floor(log2(max(largest, 1.0e-30)))) + 16);
    float scale = exp2((float)(9 - exponent + 15));

    // Rounding the largest channel can carry it to 512, which the next exponent holds.
    if ((uint)floor((largest * scale) + 0.5) == 512u) {
        exponent += 1;
        scale *= 0.5;
    }

    uint3 mantissa = (uint3)floor((held * scale) + 0.5);

    return (mantissa.r | (mantissa.g << 9u) | (mantissa.b << 18u) | ((uint)exponent << 27u));
}
float3 puckUnpackSharedExponent(uint packed) {
    float scale = exp2((float)((int)(packed >> 27u) - 24));

    return (float3((float)(packed & 0x1FFu), (float)((packed >> 9u) & 0x1FFu), (float)((packed >> 18u) & 0x1FFu)) * scale);
}

#endif
