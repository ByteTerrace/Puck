// Phase-1 compute smoke test: write a UV gradient into the storage image, proving the compute-pipeline + dispatch
// + storage-image-write plumbing end to end. Entry CSMain is renamed to "main" for SPIR-V at build time.
[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]]
RWTexture2D<float4> Output : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    Output.GetDimensions(width, height);

    if ((id.x >= width) || (id.y >= height)) {
        return;
    }

    float2 uv = float2(id.xy) / float2(width, height);

    Output[id.xy] = float4(uv.x, uv.y, 0.5, 1.0);
}
