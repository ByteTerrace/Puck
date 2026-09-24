// Writes an image whose rows, not any clip space, fix its orientation: row 0 is the image's top, so the top half is
// red and the bottom half blue.
[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> stripes : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    stripes.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    stripes[id.xy] = ((id.y < (height / 2))
        ? float4(1.0, 0.0, 0.0, 1.0)
        : float4(0.0, 0.0, 1.0, 1.0));
}
