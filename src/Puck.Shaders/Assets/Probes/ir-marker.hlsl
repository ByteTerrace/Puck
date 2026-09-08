// puck.probe.v1 kernel: Lit and Unlit are the infrared strobe pair (t0 the lit frame, t1 the unlit frame kept
// before it — the strobe socket's two consecutive registers). Lit-minus-unlit, scaled by gain, is the per-pixel
// strobe response; pixels above threshold are the retroreflective mass. accumulate sums that mass's zeroth, first,
// and second moments (u, v normalized to [0, 1], D3D texture-load convention: v = 0 at the top row); finalize
// turns the resulting 2x2 covariance into principal axes and, assuming a uniform rectangular reflector (a real
// rectangle of tape, not a Gaussian blob — variance along an axis of half-width a is a^2 / 3 for a uniform
// distribution, so half extent = sqrt(3 * eigenvalue)), four corners.
//
// Every InterlockedAdd sums a value scaled by AccumulateScale(width, height) into a uint slot
// (RWStructuredBuffer<uint> has no float atomic). u, v, u*u, v*v, and u*v are all in [0, 1] per pixel, so each
// scaled sum stays at or under width * height * scale <= uint.MaxValue at any resolution — the same bound
// ir-blob.hlsl's moments rely on. The zeroth moment (the mass, sum of the binary weight) coincides with the raw
// pixel count above threshold, so it is accumulated once as an exact unscaled integer rather than duplicated as a
// scaled sum.
//
// Channel sign conventions: x is -1 at the left edge and 1 at the right; y is -1 at the bottom edge and 1 at the
// top (the same y-up convention a stick axis carries). Corner order is top-left, top-right, bottom-right,
// bottom-left in image terms: the major axis is chosen to have a non-negative horizontal (u) component ("points
// right"), and the minor axis is its +90-degree rotation in uv space ("points down", since v increases downward);
// the four corners are then centre +/- (major half-extent along the major axis) +/- (minor half-extent along the
// minor axis), which reduces to the natural top-left/top-right/bottom-right/bottom-left assignment for a marker
// photographed close to upright. Degenerate mass (no pixel above threshold) writes every channel and confidence 0.

Texture2D<float4> Lit : register(t0);
Texture2D<float4> Unlit : register(t1);

cbuffer ProbeConfig : register(b0) {
    float threshold;
    float gain;
    float minCoverage;
};

RWStructuredBuffer<uint> Accumulate : register(u0);
RWStructuredBuffer<float> Channels : register(u1);

static const uint MaxAccumulateScale = 1024;

groupshared uint GroupSumU;
groupshared uint GroupSumV;
groupshared uint GroupSumUU;
groupshared uint GroupSumVV;
groupshared uint GroupSumUV;
groupshared uint GroupCount;

uint AccumulateScale(uint width, uint height) {
    return min(MaxAccumulateScale, 0xFFFFFFFFu / max(width * height, 1u));
}

[numthreads(8, 8, 1)]
void accumulate(uint3 dispatchId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex) {
    uint width;
    uint height;

    Lit.GetDimensions(width, height);

    if (groupIndex == 0) {
        GroupSumU = 0;
        GroupSumV = 0;
        GroupSumUU = 0;
        GroupSumVV = 0;
        GroupSumUV = 0;
        GroupCount = 0;
    }

    GroupMemoryBarrierWithGroupSync();

    if ((dispatchId.x < width) && (dispatchId.y < height)) {
        int3 texel = int3(int(dispatchId.x), int(dispatchId.y), 0);
        float lit = Lit.Load(texel).r;
        float unlit = Unlit.Load(texel).r;
        float response = saturate((lit - unlit) * gain);

        if (response > threshold) {
            float scale = float(AccumulateScale(width, height));
            float2 uv = (float2(dispatchId.xy) + 0.5) / float2(width, height);

            InterlockedAdd(GroupSumU, uint(round(uv.x * scale)));
            InterlockedAdd(GroupSumV, uint(round(uv.y * scale)));
            InterlockedAdd(GroupSumUU, uint(round(uv.x * uv.x * scale)));
            InterlockedAdd(GroupSumVV, uint(round(uv.y * uv.y * scale)));
            InterlockedAdd(GroupSumUV, uint(round(uv.x * uv.y * scale)));
            InterlockedAdd(GroupCount, 1);
        }
    }

    GroupMemoryBarrierWithGroupSync();

    if (groupIndex == 0) {
        InterlockedAdd(Accumulate[0], GroupSumU);
        InterlockedAdd(Accumulate[1], GroupSumV);
        InterlockedAdd(Accumulate[2], GroupSumUU);
        InterlockedAdd(Accumulate[3], GroupSumVV);
        InterlockedAdd(Accumulate[4], GroupSumUV);
        InterlockedAdd(Accumulate[5], GroupCount);
    }
}

[numthreads(1, 1, 1)]
void finalize(uint3 dispatchId : SV_DispatchThreadID) {
    uint width;
    uint height;

    Lit.GetDimensions(width, height);

    float scale = float(AccumulateScale(width, height));
    float pixelCount = (float(width) * float(height));
    float count = float(Accumulate[5]);

    if (count <= 0.0) {
        Channels[0] = 0.0;
        Channels[1] = 0.0;
        Channels[2] = 0.0;
        Channels[3] = 0.0;
        Channels[4] = 0.0;
        Channels[5] = 0.0;
        Channels[6] = 0.0;
        Channels[7] = 0.0;
        Channels[8] = 0.0;

        return;
    }

    float meanU = (Accumulate[0] / scale) / count;
    float meanV = (Accumulate[1] / scale) / count;
    float meanUU = (Accumulate[2] / scale) / count;
    float meanVV = (Accumulate[3] / scale) / count;
    float meanUV = (Accumulate[4] / scale) / count;
    float varU = max(meanUU - (meanU * meanU), 0.0);
    float varV = max(meanVV - (meanV * meanV), 0.0);
    float covUV = meanUV - (meanU * meanV);
    float trace = varU + varV;
    float diff = varU - varV;
    float discriminant = sqrt(max((diff * diff) + (4.0 * covUV * covUV), 0.0));
    float lambdaMajor = max((trace + discriminant) * 0.5, 0.0);
    float lambdaMinor = max((trace - discriminant) * 0.5, 0.0);
    float2 axisMajor;

    if (abs(covUV) > 1e-8) {
        axisMajor = normalize(float2(covUV, lambdaMajor - varU));
    } else if (varU >= varV) {
        axisMajor = float2(1.0, 0.0);
    } else {
        axisMajor = float2(0.0, 1.0);
    }

    if (axisMajor.x < 0.0) {
        axisMajor = -axisMajor;
    }

    float2 axisMinor = float2(-axisMajor.y, axisMajor.x);
    float halfMajor = sqrt(3.0 * lambdaMajor);
    float halfMinor = sqrt(3.0 * lambdaMinor);
    float2 centre = float2(meanU, meanV);
    float2 ax = axisMajor * halfMajor;
    float2 ay = axisMinor * halfMinor;
    float2 topLeft = centre - ax - ay;
    float2 topRight = centre + ax - ay;
    float2 bottomRight = centre + ax + ay;
    float2 bottomLeft = centre - ax + ay;

    Channels[0] = clamp((topLeft.x * 2.0) - 1.0, -1.0, 1.0);
    Channels[1] = clamp(1.0 - (topLeft.y * 2.0), -1.0, 1.0);
    Channels[2] = clamp((topRight.x * 2.0) - 1.0, -1.0, 1.0);
    Channels[3] = clamp(1.0 - (topRight.y * 2.0), -1.0, 1.0);
    Channels[4] = clamp((bottomRight.x * 2.0) - 1.0, -1.0, 1.0);
    Channels[5] = clamp(1.0 - (bottomRight.y * 2.0), -1.0, 1.0);
    Channels[6] = clamp((bottomLeft.x * 2.0) - 1.0, -1.0, 1.0);
    Channels[7] = clamp(1.0 - (bottomLeft.y * 2.0), -1.0, 1.0);

    float coverage = saturate(count / pixelCount);

    Channels[8] = saturate(coverage / max(minCoverage, 0.0001));
}
