// A one-off image shader: the smallest pipeline, one compute pass writing one image, with no placed surface, mesh, or
// importer behind it.
[[vk::binding(0, 0)]] RWTexture2D<float4> image : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    image.GetDimensions(width, height);
    image[id.xy] = float4((float(id.x) / float(width)), (float(id.y) / float(height)), 0.5, 1.0);
}
