// puck.probe.v1 kernel ABI: Source is the camera's shared IR target (r = luminance); ProbeConfig is the manifest's
// bound config, in declaration order; Accumulate is cleared before accumulate runs and is never read by the
// caller; Channels is ChannelCount + 1 floats (this kind's four channels, then confidence), written once by
// finalize. accumulate runs one thread per pixel; finalize runs a single thread after accumulate completes.
//
// Every InterlockedAdd sums a value scaled by AccumulateScale(width, height) into a uint slot
// (RWStructuredBuffer<uint> has no float atomic). Pixel coordinates are normalized to [0, 1] before weighting and
// the scale is derived from the frame's pixel count, so over the worst case (every pixel above threshold) each of
// Accumulate[0..3] stays at or under width * height * scale <= uint.MaxValue at any resolution.
//
// Channel sign conventions: x is -1 at the left edge and 1 at the right; y is -1 at the bottom edge and 1 at the
// top (the same y-up convention a stick axis carries).

Texture2D<float4> Source : register(t0);

cbuffer ProbeConfig : register(b0) {
    float threshold;
    float minCoverage;
};

RWStructuredBuffer<uint> Accumulate : register(u0);
RWStructuredBuffer<float> Channels : register(u1);

static const uint MaxAccumulateScale = 1024;

groupshared uint GroupWeight;
groupshared uint GroupWeightX;
groupshared uint GroupWeightY;
groupshared uint GroupLuminance;
groupshared uint GroupCount;

uint AccumulateScale(uint width, uint height) {
    return min(MaxAccumulateScale, 0xFFFFFFFFu / max(width * height, 1u));
}

[numthreads(8, 8, 1)]
void accumulate(uint3 dispatchId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex) {
    uint width;
    uint height;

    Source.GetDimensions(width, height);

    if (groupIndex == 0) {
        GroupWeight = 0;
        GroupWeightX = 0;
        GroupWeightY = 0;
        GroupLuminance = 0;
        GroupCount = 0;
    }

    GroupMemoryBarrierWithGroupSync();

    if ((dispatchId.x < width) && (dispatchId.y < height)) {
        float scale = float(AccumulateScale(width, height));
        float lum = Source.Load(int3(int(dispatchId.x), int(dispatchId.y), 0)).r;
        float weight = saturate((lum - threshold) / max(1.0 - threshold, 0.0001));

        if (weight > 0.0) {
            float2 uv = (float2(dispatchId.xy) + 0.5) / float2(width, height);

            InterlockedAdd(GroupWeight, uint(round(weight * scale)));
            InterlockedAdd(GroupWeightX, uint(round(weight * uv.x * scale)));
            InterlockedAdd(GroupWeightY, uint(round(weight * uv.y * scale)));
            InterlockedAdd(GroupLuminance, uint(round(lum * scale)));
            InterlockedAdd(GroupCount, 1);
        }
    }

    GroupMemoryBarrierWithGroupSync();

    if (groupIndex == 0) {
        InterlockedAdd(Accumulate[0], GroupWeight);
        InterlockedAdd(Accumulate[1], GroupWeightX);
        InterlockedAdd(Accumulate[2], GroupWeightY);
        InterlockedAdd(Accumulate[3], GroupLuminance);
        InterlockedAdd(Accumulate[4], GroupCount);
    }
}

[numthreads(1, 1, 1)]
void finalize(uint3 dispatchId : SV_DispatchThreadID) {
    uint width;
    uint height;

    Source.GetDimensions(width, height);

    float scale = float(AccumulateScale(width, height));
    float totalWeight = (Accumulate[0] / scale);
    float pixelCount = (float(width) * float(height));
    float countAbove = float(Accumulate[4]);

    if ((totalWeight <= 0.0) || (countAbove <= 0.0)) {
        Channels[0] = 0.0;
        Channels[1] = 0.0;
        Channels[2] = 0.0;
        Channels[3] = 0.0;
        Channels[4] = 0.0;

        return;
    }

    float meanU = ((Accumulate[1] / scale) / totalWeight);
    float meanV = ((Accumulate[2] / scale) / totalWeight);
    float coverage = saturate(countAbove / pixelCount);

    Channels[0] = clamp(((meanU * 2.0) - 1.0), -1.0, 1.0);
    Channels[1] = clamp((1.0 - (meanV * 2.0)), -1.0, 1.0);
    Channels[2] = coverage;
    Channels[3] = saturate((Accumulate[3] / scale) / countAbove);
    Channels[4] = saturate(coverage / max(minCoverage, 0.0001));
}
