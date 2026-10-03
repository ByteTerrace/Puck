// The scRGB fill: writes a half-float image texel by texel from a table of values, so a device law holds an image on the
// device whose contents it knows. ImportedImageConversionDeviceLawTests fills the image an HDR capture's copy stands for
// with it. It binds one group, set 3: each register number equals its binding in space 3.

[[vk::binding(0, 3)]] StructuredBuffer<float4> fillValues : register(t0, space3);
[[vk::binding(1, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> fillOutput : register(u1, space3);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    fillOutput.GetDimensions(width, height);

    if ((id.x >= width) || (id.y >= height)) {
        return;
    }

    fillOutput[id.xy] = fillValues[(id.y * width) + id.x];
}
