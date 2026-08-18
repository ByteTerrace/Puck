namespace Puck.Abstractions.Gpu;

/// <summary>The shared fullscreen-triangle vertex data every single-pass fragment shader (the overlay, the
/// swapchain blit, a post-render extension) draws through.</summary>
public static class FullscreenTriangle {
    /// <summary>The byte stride between consecutive vertices — one <see cref="GpuVertexFormat.R32G32Float"/>
    /// POSITION attribute at offset 0.</summary>
    public const uint StrideBytes = (sizeof(float) * 2);
    /// <summary>The vertex count a fullscreen-triangle draw call submits.</summary>
    public const uint VertexCount = 3;

    /// <summary>Creates the packed vertex bytes for one oversized triangle covering the whole clip-space quad:
    /// (-1,-1), (3,-1), (-1,3).</summary>
    /// <returns>Three tightly packed <c>float2</c> positions, <see cref="StrideBytes"/> apart.</returns>
    public static byte[] CreateVertexData() {
        var vertices = new (float X, float Y)[] {
            (-1f, -1f),
            (3f, -1f),
            (-1f, 3f),
        };
        var vertexData = new byte[((int)(StrideBytes * vertices.Length))];

        for (var index = 0; (index < vertices.Length); index++) {
            var offset = (index * ((int)StrideBytes));

            _ = BitConverter.TryWriteBytes(
                destination: vertexData.AsSpan(
                    length: sizeof(float),
                    start: offset
                ),
                value: vertices[index].X
            );
            _ = BitConverter.TryWriteBytes(
                destination: vertexData.AsSpan(
                    length: sizeof(float),
                    start: (offset + sizeof(float))
                ),
                value: vertices[index].Y
            );
        }

        return vertexData;
    }
}
