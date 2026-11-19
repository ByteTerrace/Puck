// The display encode: a working image, sampled 1:1 by fragment coordinate, written in the color space its target
// displays. Every swapchain compositor draws it into its back buffer, and a float output's preview and capture draw it,
// in SDR, into RGBA8 (SurfaceEncoder). A working image holds sRGB-encoded values, one at SDR white, with float headroom
// above it on the extended curve: a world's view writes them in its own graph's display pass, which converts its scene
// light exactly once, and an image source's conversion writes them (ImageSourceConversion.LinearToWorking). So the SDR
// branch writes the value as it is, and each HDR branch decodes it to linear light once before its own encode.
//
// Its one group is the pass group, set 3, each register equal to its binding (DisplayEncodeLayout): the source image,
// its sampler and the encode block, which DisplayEncodeLayout.WriteBlock writes.
#include "Shared/low-discrepancy.hlsli"
#include "Shared/transfer.hlsli"

[[vk::binding(0, 3)]] Texture2D<float4> sourceTexture : register(t0, space3);
[[vk::binding(1, 3)]] SamplerState sourceSampler : register(s1, space3);

// DisplayEncodeLayout.WriteBlock writes it field for field.
struct DisplayEncode {
    // The target's color space, a Puck.Abstractions.Presentation.DisplayColorSpace value.
    uint colorSpace;
    // The linear value SDR white takes in the target at the paper-white level (DisplayOutput.WhiteScale).
    float whiteScale;
    // One code of the target's format, the step its write quantizes at; zero for a float target, which is not dithered.
    float ditherStep;
    // Nonzero when the target encodes sRGB on write, so the encode writes it linear light.
    uint encodesSrgb;
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

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float3 color = sourceTexture.Sample(sourceSampler, (fragCoord.xy / float2(width, height))).rgb;
    // Half a code of dither either side of the value the target quantizes, which breaks gradient banding into noise the
    // eye barely sees: a code of an 8-bit target, a code of a 10-bit one, nothing on a float target.
    float dither = ((puckR2Dither(uint2(fragCoord.xy)) - 0.5) * encode.ditherStep);

    if (encode.colorSpace == DisplayHdr10) {
        float3 display = (mul(Bt709ToBt2020, puckSrgbToLinearExtended(color)) * encode.whiteScale);

        return float4(saturate(puckPqEncode(display) + dither), 1.0);
    }
    if (encode.colorSpace == DisplayScRgb) {
        return float4(((puckSrgbToLinearExtended(color) * encode.whiteScale) + dither), 1.0);
    }

    float3 sdr = saturate(color + dither);

    // A target that encodes sRGB on write quantizes after its own encode, so it takes the linear light that encodes back
    // to the dithered value.
    return float4(((encode.encodesSrgb != 0u) ? puckSrgbToLinearExtended(sdr) : sdr), 1.0);
}
