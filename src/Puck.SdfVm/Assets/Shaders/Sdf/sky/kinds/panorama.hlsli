// Explicit equirectangular filtering wraps longitude while clamping the poles, without a new sampler policy.
#ifndef SKY_KIND_PANORAMA_HLSLI
#define SKY_KIND_PANORAMA_HLSLI

float4 sdfSkyPanorama(float3 localDirection, SdfSkyPanoramaData parameters,
    Texture2D<float4> source, inout uint samples) {
    if (parameters.Intensity <= 0.0) return 0.0;
    uint width;
    uint height;
    source.GetDimensions(width, height);
    float longitude = any(localDirection.xz != 0.0) ? atan2(localDirection.x, localDirection.z) : 0.0;
    float2 uv = float2(longitude * (1.0 / 6.283185307179586) + 0.5,
        acos(clamp(localDirection.y, -1.0, 1.0)) * (1.0 / 3.141592653589793));
    float4 color;
    if (parameters.Filter == 0u) {
        uint2 pixel = uint2(frac(uv.x) * width, min((uint)(uv.y * height), height - 1u));
        color = source.Load(int3(pixel, 0));
        samples += 1u;
    } else {
        float2 position = float2(frac(uv.x) * width, uv.y * height) - 0.5;
        int2 first = int2(floor(position));
        float2 blend = frac(position);
        int x0 = (first.x + (int)width) % (int)width;
        int x1 = (x0 + 1) % (int)width;
        int y0 = clamp(first.y, 0, (int)height - 1);
        int y1 = clamp(first.y + 1, 0, (int)height - 1);
        float4 top = lerp(source.Load(int3(x0, y0, 0)), source.Load(int3(x1, y0, 0)), blend.x);
        float4 bottom = lerp(source.Load(int3(x0, y1, 0)), source.Load(int3(x1, y1, 0)), blend.x);
        color = lerp(top, bottom, blend.y);
        samples += 4u;
    }
    return float4(color.rgb * parameters.Tint * parameters.Intensity, color.a);
}
#endif
