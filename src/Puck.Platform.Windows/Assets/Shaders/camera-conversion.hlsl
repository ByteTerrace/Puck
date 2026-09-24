// The camera frame converter's kernels (Win32D3D11CameraFrameConverter), compiled to cs_5_0 DXBC at build, one file per
// entry point (camera-conversion.<entry>.dxbc). Each writes one RGBA pixel of Target per thread from the camera surface
// copied into the converter's shader-readable input. The YUV conversion is data: the converter fills Conversion from
// the stream's colorimetry, in the order Win32D3D11CameraFrameConverter.ConversionConstants packs it.
cbuffer Conversion : register(b0) {
    // Limited range: 16 and 219; full range: 0 and 255. Luma in code units is (y * 255 - LumaOffset) / LumaScale.
    float LumaOffset;
    float LumaScale;
    // Limited range: 224; full range: 255. Chroma in code units is (c * 255 - 128) / ChromaScale.
    float ChromaScale;
    float ConversionPadding;
    // The matrix's coefficients: red = y + Matrix.x * v, green = y - Matrix.y * u - Matrix.z * v, blue = y + Matrix.w * u.
    float4 Matrix;
    // Where a chroma sample sits relative to the luma grid: 0 on an axis where it is cosited, 0.5 where it is centered.
    float2 ChromaOffset;
    float2 OffsetPadding;
};

float3 YuvToRgb(float y, float u, float v) {
    y = max(0.0, (((y * 255.0) - LumaOffset) / LumaScale));
    u = (((u * 255.0) - 128.0) / ChromaScale);
    v = (((v * 255.0) - 128.0) / ChromaScale);

    return saturate(float3(
        (y + (Matrix.x * v)),
        ((y - (Matrix.y * u)) - (Matrix.z * v)),
        (y + (Matrix.w * u))
    ));
}

// The input's views in the order the converter binds them. Packed YUY2 is viewed as R8G8B8A8 at half width, each texel
// one two-pixel macropixel Y0/U/Y1/V. Two-plane NV12 is its full-resolution luma plane (R8) here and its
// half-resolution interleaved chroma plane (R8G8) in Chroma, each a view over the one NV12 texture; Direct3D selects the
// plane from the view format. L8 infrared is one luminance channel. A narrower view reads its channels first.
Texture2D<float4> Source : register(t0);
Texture2D<float4> Chroma : register(t1);
RWTexture2D<float4> Target : register(u0);

bool Outside(uint2 position) {
    uint width;
    uint height;

    Target.GetDimensions(width, height);

    return ((position.x >= width) || (position.y >= height));
}

[numthreads(8, 8, 1)]
void packed(uint3 position : SV_DispatchThreadID) {
    if (Outside(position.xy)) {
        return;
    }

    float4 pair = Source.Load(int3((position.x >> 1), position.y, 0));
    float y = (((position.x & 1) == 0) ? pair.r : pair.b);

    Target[position.xy] = float4(YuvToRgb(y, pair.g, pair.a), 1.0);
}

[numthreads(8, 8, 1)]
void planar(uint3 position : SV_DispatchThreadID) {
    if (Outside(position.xy)) {
        return;
    }

    uint chromaWidth;
    uint chromaHeight;

    Chroma.GetDimensions(chromaWidth, chromaHeight);

    float2 chromaPosition = ((float2(position.xy) - ChromaOffset) * 0.5);
    int2 chromaBase = int2(floor(chromaPosition));
    float2 chromaBlend = frac(chromaPosition);
    int2 chromaMaximum = int2((chromaWidth - 1), (chromaHeight - 1));
    int2 chroma00 = clamp(chromaBase, int2(0, 0), chromaMaximum);
    int2 chroma11 = clamp((chromaBase + 1), int2(0, 0), chromaMaximum);
    float2 top = lerp(
        Chroma.Load(int3(chroma00, 0)).rg,
        Chroma.Load(int3(chroma11.x, chroma00.y, 0)).rg,
        chromaBlend.x
    );
    float2 bottom = lerp(
        Chroma.Load(int3(chroma00.x, chroma11.y, 0)).rg,
        Chroma.Load(int3(chroma11, 0)).rg,
        chromaBlend.x
    );
    float y = Source.Load(int3(position.xy, 0)).r;
    float2 uv = lerp(top, bottom, chromaBlend.y);

    Target[position.xy] = float4(YuvToRgb(y, uv.x, uv.y), 1.0);
}

[numthreads(8, 8, 1)]
void infrared(uint3 position : SV_DispatchThreadID) {
    if (Outside(position.xy)) {
        return;
    }

    float luminance = Source.Load(int3(position.xy, 0)).r;

    Target[position.xy] = float4(luminance, luminance, luminance, 1.0);
}
