// The known source: 16 texels wide and three 16-row bands tall, every channel equal, alpha one.
// - Rows 0..15, a step from 0 to 1 between columns 7 and 8: bilinear and clamped Catmull-Rom differ by about ten
//   codes beside the edge.
// - Rows 16..31, a step from 0.25 to 0.75 at the same place: beside it the raw Catmull-Rom undershoots the central
//   texels, so the neighbourhood clamp decides the value.
// - Rows 32..47, a step from 0.25 to 0.75 between columns 0 and 1: the cubic's left tap falls off the image, so the
//   edge clamp decides the value.
[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> step : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    step.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    float value;

    if (id.y < 16u) {
        value = ((id.x < 8u) ? 0.0 : 1.0);
    } else if (id.y < 32u) {
        value = ((id.x < 8u) ? 0.25 : 0.75);
    } else {
        value = ((id.x < 1u) ? 0.25 : 0.75);
    }

    step[id.xy] = float4(value, value, value, 1.0);
}
