// The known source: a 16-texel-wide step, 0 in columns 0..7 and 1 in columns 8..15 on every channel and row, with
// alpha one. Resampling it to 64 texels puts the analytic answer at each destination column: bilinear and clamped
// Catmull-Rom differ by about ten codes beside the edge.
[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> step : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    step.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    float value = ((id.x < (width / 2u)) ? 0.0 : 1.0);

    step[id.xy] = float4(value, value, value, 1.0);
}
