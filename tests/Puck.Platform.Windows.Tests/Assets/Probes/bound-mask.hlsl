// A minimal puck.probe.manifest.v1 kernel that ignores its unbound socket and writes the frame constants' boundMask
// straight into Channels[0], so ProbeKernelTests can assert the bit a run was given without a real texture.
cbuffer ProbeFrame : register(b1) {
    float time;
    float deltaTime;
    uint frame;
    uint boundMask;
};

RWStructuredBuffer<uint> Accumulate : register(u0);
RWStructuredBuffer<float> Channels : register(u1);

[numthreads(8, 8, 1)]
void accumulate(uint3 dispatchId : SV_DispatchThreadID) {
}

[numthreads(1, 1, 1)]
void finalize(uint3 dispatchId : SV_DispatchThreadID) {
    Channels[0] = float(boundMask);
    Channels[1] = 1.0;
}
