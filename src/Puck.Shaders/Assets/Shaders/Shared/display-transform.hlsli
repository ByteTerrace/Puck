// The display transform's curve: Uchimura's toe, linear section and shoulder (the Gran Turismo fit), over exposed scene
// light, every constant an authored parameter (SdfToneCurve, render.tone). The toe is a power curve below linearStart,
// the linear section has slope contrast from linearStart for linearLength of the input, and the shoulder rolls into peak
// above it. A peak of zero is no curve: the light passes through as it is.
#ifndef PUCK_DISPLAY_TRANSFORM_HLSLI
#define PUCK_DISPLAY_TRANSFORM_HLSLI

// shape: peak, contrast, linear start, linear length; toe: the toe's power.
float3 puckToneCurve(float3 color, float4 shape, float toe) {
    float peak = shape.x;

    if (!(peak > 0.0)) {
        return color;
    }

    float contrast = shape.y;
    float linearStart = shape.z;
    float linearLength = shape.w;
    float l0 = (((peak - linearStart) * linearLength) / contrast);
    float s0 = (linearStart + l0);
    float s1 = (linearStart + (contrast * l0));
    float c2 = ((contrast * peak) / (peak - s1));
    float3 x = max(color, 0.0);
    float3 toeWeight = (1.0 - smoothstep(0.0, linearStart, x));
    float3 shoulderWeight = step(s0, x);
    float3 linearWeight = ((1.0 - toeWeight) - shoulderWeight);
    float3 toeCurve = (linearStart * pow((x / linearStart), toe));
    float3 shoulder = (peak - ((peak - s1) * exp((-c2 * (x - s0)) / peak)));
    float3 middle = (linearStart + (contrast * (x - linearStart)));

    return (((toeCurve * toeWeight) + (middle * linearWeight)) + (shoulder * shoulderWeight));
}

// The contrast-adaptive sharpen (AMD FidelityFX CAS over the pixel's four edge neighbours) of an image at its own extent:
// each neighbour weighs -0.2 * sharpness times the pixel's headroom below its neighbourhood's peak, so flat and saturated
// regions are left as they are and an edge is sharpened without overshooting its neighbourhood. Sharpness 0 returns the
// pixel exactly. It reads linear exposed light, whose headroom it measures against 2.
float3 puckSharpen(Texture2D<float4> image, uint2 pixel, uint2 dims, float sharpness) {
    int2 last = (int2(dims) - 1);
    float3 up = image.Load(int3(clamp((int2(pixel) + int2(0, -1)), int2(0, 0), last), 0)).rgb;
    float3 left = image.Load(int3(clamp((int2(pixel) + int2(-1, 0)), int2(0, 0), last), 0)).rgb;
    float3 center = image.Load(int3(pixel, 0)).rgb;
    float3 right = image.Load(int3(clamp((int2(pixel) + int2(1, 0)), int2(0, 0), last), 0)).rgb;
    float3 down = image.Load(int3(clamp((int2(pixel) + int2(0, 1)), int2(0, 0), last), 0)).rgb;
    float3 lowest = min(min(min(up, left), min(center, right)), down);
    float3 highest = max(max(max(up, left), max(center, right)), down);
    float3 amplitude = sqrt(saturate(min(lowest, (2.0 - highest)) / max(highest, 1.0e-5)));
    float3 weight = (amplitude * (-0.2 * saturate(sharpness)));

    return max(((((up + left) + (right + down)) * weight) + center) / (1.0 + (4.0 * weight)), 0.0);
}

#endif
