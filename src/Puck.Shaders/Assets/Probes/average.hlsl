// puck.probe.v1 kernel: Frame is the socket the kind measures and writes (t0, the trigger and the output extent);
// ProbeConfig is the manifest's bound config in declaration order (three 4-byte floats, padded to a 16-byte block);
// Accumulate is cleared before accumulate runs and is never read by the caller; Channels is this kind's three channels,
// then confidence, written once by finalize; Output is the kernel's texture output, the frame's extent.
//
// Every InterlockedAdd sums a channel value in [0, 1] scaled by AccumulateScale(width, height) into a uint slot
// (RWStructuredBuffer<uint> has no float atomic); the scale is derived from the frame's pixel count, so each of
// Accumulate[0..2] stays at or under width * height * scale <= uint.MaxValue at any resolution.

Texture2D<float4> Frame : register(t0);

cbuffer ProbeConfig : register(b0) {
    float tintR;
    float tintG;
    float tintB;
};

RWStructuredBuffer<uint> Accumulate : register(u0);
RWStructuredBuffer<float> Channels : register(u1);
RWTexture2D<float4> Output : register(u2);

static const uint MaxAccumulateScale = 1024;

groupshared uint GroupRed;
groupshared uint GroupGreen;
groupshared uint GroupBlue;

uint AccumulateScale(uint width, uint height) {
    return min(MaxAccumulateScale, 0xFFFFFFFFu / max(width * height, 1u));
}

[numthreads(8, 8, 1)]
void accumulate(uint3 dispatchId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex) {
    uint width;
    uint height;

    Frame.GetDimensions(width, height);

    if (groupIndex == 0) {
        GroupRed = 0;
        GroupGreen = 0;
        GroupBlue = 0;
    }

    GroupMemoryBarrierWithGroupSync();

    if ((dispatchId.x < width) && (dispatchId.y < height)) {
        float scale = float(AccumulateScale(width, height));
        float3 color = saturate(Frame.Load(int3(int(dispatchId.x), int(dispatchId.y), 0)).rgb);

        InterlockedAdd(GroupRed, uint(round(color.r * scale)));
        InterlockedAdd(GroupGreen, uint(round(color.g * scale)));
        InterlockedAdd(GroupBlue, uint(round(color.b * scale)));
        Output[dispatchId.xy] = float4(color * float3(tintR, tintG, tintB), 1.0);
    }

    GroupMemoryBarrierWithGroupSync();

    if (groupIndex == 0) {
        InterlockedAdd(Accumulate[0], GroupRed);
        InterlockedAdd(Accumulate[1], GroupGreen);
        InterlockedAdd(Accumulate[2], GroupBlue);
    }
}

[numthreads(1, 1, 1)]
void finalize(uint3 dispatchId : SV_DispatchThreadID) {
    uint width;
    uint height;

    Frame.GetDimensions(width, height);

    float total = (float(AccumulateScale(width, height)) * float(max(width * height, 1u)));

    Channels[0] = saturate(float(Accumulate[0]) / total);
    Channels[1] = saturate(float(Accumulate[1]) / total);
    Channels[2] = saturate(float(Accumulate[2]) / total);
    Channels[3] = (((width * height) > 0u) ? 1.0 : 0.0);
}
