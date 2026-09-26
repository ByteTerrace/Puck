// The bake sampling probe: samples one texel of one mip level of an uploaded image at the texel's center, through a
// point sampler at that level, and writes the value it reads into one texel of the output row. BakeSamplingDeviceLawTests
// dispatches it once per probe of tests/Puck.SignedDistance.Tests/Fixtures/bake-sampling.json, pushing the probe's
// index into its probe table. It binds one group, set 3: each register number equals its binding in space 3, and the
// pushed index is Direct3D 12's root constant at b0, space 4.

[[vk::binding(0, 3)]] Texture2D<float4> probeSource : register(t0, space3);
[[vk::binding(1, 3)]] SamplerState probeSampler : register(s1, space3);
[[vk::binding(2, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> probeOutput : register(u2, space3);
// Each probe: the texel's column and row at its level, the mip level sampled, and the output texel written.
[[vk::binding(3, 3)]] StructuredBuffer<uint4> probes : register(t3, space3);

struct ProbeIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<ProbeIndex> probeIndex : register(b0, space4);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint4 probe = probes[probeIndex.index];
    uint width;
    uint height;
    uint levels;

    probeSource.GetDimensions(probe.z, width, height, levels);

    float2 center = ((float2(probe.xy) + 0.5) / float2(width, height));

    probeOutput[uint2(probe.w, 0)] = probeSource.SampleLevel(probeSampler, center, probe.z);
}
