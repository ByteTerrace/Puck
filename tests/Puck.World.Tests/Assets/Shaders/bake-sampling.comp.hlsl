// The bake sampling probe: samples one texel of one mip level of an uploaded image at the texel's center, through a
// point sampler at that level, and writes the value it reads into one texel of the output row. BakeSamplingDeviceLawTests
// dispatches it once per probe of tests/Puck.SignedDistance.Tests/Fixtures/bake-sampling.json. Each register number
// equals its binding (GpuRegisterNumbering.Binding); the push block is Direct3D 12's root constants at b0.

[[vk::combinedImageSampler]] [[vk::binding(0, 0)]] Texture2D<float4> probeSource : register(t0);
[[vk::combinedImageSampler]] [[vk::binding(0, 0)]] SamplerState probeSampler : register(s0);
[[vk::image_format("rgba32f")]] [[vk::binding(1, 0)]] RWTexture2D<float4> probeOutput : register(u1);

struct ProbePush {
    uint x;     // the probe texel's column at its level
    uint y;     // the probe texel's row at its level
    uint level; // the mip level sampled
    uint slot;  // the output texel written
};
[[vk::push_constant]] ProbePush probe;

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;
    uint levels;

    probeSource.GetDimensions(probe.level, width, height, levels);

    float2 center = ((float2(probe.x, probe.y) + 0.5) / float2(width, height));

    probeOutput[uint2(probe.slot, 0)] = probeSource.SampleLevel(probeSampler, center, probe.level);
}
