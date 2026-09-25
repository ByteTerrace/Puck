// Fills the pane with opaque magenta, a colour the world beside it never shows.
[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> output : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    output.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }

    output[id.xy] = float4(1.0, 0.0, 1.0, 1.0);
}
