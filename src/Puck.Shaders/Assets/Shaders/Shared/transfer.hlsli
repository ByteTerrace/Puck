// The transfer curves the image-source conversion passes and the display encode share: sRGB (IEC 61966-2-1) in its clamped
// and extended forms, and the SMPTE ST 2084 perceptual quantizer. ImageSourceConversion (SrgbToLinear, LinearToWorking,
// PqToNits) is the double-precision reference; the display encode's HDR10 branch applies the quantizer's encode.
#ifndef PUCK_TRANSFER_HLSLI
#define PUCK_TRANSFER_HLSLI

// SMPTE ST 2084, as the standard's ratios; every one is exact in single precision.
static const float PuckPqC1 = (3424.0 / 4096.0);
static const float PuckPqC2 = ((2413.0 / 4096.0) * 32.0);
static const float PuckPqC3 = ((2392.0 / 4096.0) * 32.0);
static const float PuckPqM1 = (2610.0 / 16384.0);
static const float PuckPqM2 = ((2523.0 / 4096.0) * 128.0);
// The luminance an encoded value of one decodes to, in cd/m².
static const float PuckPqPeakNits = 10000.0;

// An sRGB-encoded value to linear, clamped to 0 to 1.
float puckSrgbToLinear(float value) {
    float clamped = saturate(value);

    return ((clamped <= 0.04045) ? (clamped / 12.92) : pow(((clamped + 0.055) / 1.055), 2.4));
}
// The sRGB decode extended past one and mirrored below zero, so headroom decodes on the curve.
float3 puckSrgbToLinearExtended(float3 value) {
    float3 magnitude = abs(value);
    float3 curve = pow(((magnitude + 0.055) / 1.055), 2.4);

    return (sign(value) * lerp(curve, (magnitude / 12.92), step(magnitude, 0.04045)));
}
// Linear light to an sRGB-encoded value, extended past one and mirrored below zero.
float puckLinearToSrgbExtended(float value) {
    float magnitude = abs(value);
    float encoded = ((magnitude <= 0.0031308) ? (magnitude * 12.92) : ((1.055 * pow(magnitude, (1.0 / 2.4))) - 0.055));

    return ((value < 0.0) ? -encoded : encoded);
}
// The perceptual quantizer's decode: an encoded value to cd/m².
float puckPqToNits(float value) {
    float power = pow(saturate(value), (1.0 / PuckPqM2));

    return (PuckPqPeakNits * pow((max((power - PuckPqC1), 0.0) / (PuckPqC2 - (PuckPqC3 * power))), (1.0 / PuckPqM1)));
}
// The perceptual quantizer's encode: linear light normalized to the peak luminance to an encoded value.
float3 puckPqEncode(float3 normalized) {
    float3 power = pow(saturate(normalized), PuckPqM1);

    return pow(((PuckPqC1 + (PuckPqC2 * power)) / (1.0 + (PuckPqC3 * power))), PuckPqM2);
}

#endif
