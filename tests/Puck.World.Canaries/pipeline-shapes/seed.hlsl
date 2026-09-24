// The first compute stage: every texel of the half-float field holds (3/8, 0, 0, 1), and the raw word table holds 96
// at byte 0 and 32 at byte 4. The bindings are sparse (4 and 9); Direct3D 12 numbers registers per type in binding
// declaration order, so the field is u0 and the words u1.
[[vk::binding(4, 0)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> field : register(u0);
[[vk::binding(9, 0)]] RWByteAddressBuffer words : register(u1);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    field.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    field[id.xy] = float4(0.375, 0.0, 0.0, 1.0);
    if ((id.x == 0) && (id.y == 0)) {
        words.Store2(0, uint2(96, 32));
    }
}
