// The display encode: a working image, sampled 1:1 by fragment coordinate, written in the color space its target
// displays. Every swapchain compositor draws it into its back buffer, and a float output's preview and capture draw it,
// in SDR, into RGBA8 (SurfaceEncoder). The working values are the shading's display-referred values, one at SDR white,
// with float headroom above it.
//
// Its one group is the pass group, set 3, each register equal to its binding (DisplayEncodeLayout): the source image,
// its sampler and the encode block, which SurfaceEncoder.WriteBlock writes.
[[vk::binding(0, 3)]] Texture2D<float4> sourceTexture : register(t0, space3);
[[vk::binding(1, 3)]] SamplerState sourceSampler : register(s1, space3);

struct DisplayEncode {
    // The target's color space, a Puck.Abstractions.Presentation.DisplayColorSpace value.
    uint colorSpace;
    // The linear value SDR white takes in the target at the paper-white level (DisplayOutput.WhiteScale).
    float whiteScale;
    uint2 padding;
};

[[vk::binding(2, 3)]] ConstantBuffer<DisplayEncode> encode : register(b2, space3);

// KEEP IN SYNC with Puck.Abstractions.Presentation.DisplayColorSpace.
static const uint DisplayHdr10 = 1u;
static const uint DisplayScRgb = 2u;

// BT.709 primaries to BT.2020, for linear light (ITU-R BT.2087).
static const float3x3 Bt709ToBt2020 = {
    0.6274040, 0.3292820, 0.0433136,
    0.0690970, 0.9195400, 0.0113612,
    0.0163916, 0.0880132, 0.8955950,
};

// One R2 dither sample in [0, 1] from integer pixel coordinates, in fixed point so both backends add the identical
// pattern (the SDF kernels' sdfR2Dither, which dithers their volumes, is the same sequence).
float displayDither(uint2 pixel) {
    uint h = ((pixel.x * 3242174889u) + (pixel.y * 2447445414u));

    return ((float)h * (1.0 / 4294967296.0));
}
// The sRGB transfer function's inverse, extended past one and mirrored below zero, so headroom decodes on the curve.
float3 displayToLinear(float3 value) {
    float3 magnitude = abs(value);
    float3 curve = pow(((magnitude + 0.055) / 1.055), 2.4);

    return (sign(value) * lerp(curve, (magnitude / 12.92), step(magnitude, 0.04045)));
}
// The SMPTE ST 2084 perceptual quantizer of linear light normalized to 10,000 nits.
float3 displayPerceptualQuantizer(float3 normalized) {
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;
    float3 power = pow(saturate(normalized), m1);

    return pow(((c1 + (c2 * power)) / (1.0 + (c3 * power))), m2);
}

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float3 color = sourceTexture.Sample(sourceSampler, (fragCoord.xy / float2(width, height))).rgb;
    // Half a code of dither either side before a unorm target quantizes, which breaks gradient banding into noise the
    // eye barely sees; a float target is not quantized.
    float dither = (displayDither(uint2(fragCoord.xy)) - 0.5);

    if (encode.colorSpace == DisplayHdr10) {
        float3 display = (mul(Bt709ToBt2020, displayToLinear(color)) * encode.whiteScale);

        return float4(saturate(displayPerceptualQuantizer(display) + (dither * (1.0 / 1023.0))), 1.0);
    }
    if (encode.colorSpace == DisplayScRgb) {
        return float4((displayToLinear(color) * encode.whiteScale), 1.0);
    }

    return float4(saturate(color + (dither * (1.0 / 255.0))), 1.0);
}
