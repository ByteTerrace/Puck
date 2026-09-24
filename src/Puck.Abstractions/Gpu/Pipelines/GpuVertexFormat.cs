namespace Puck.Abstractions.Gpu;

/// <summary>The data format of one vertex attribute.</summary>
public enum GpuVertexFormat : uint {
    /// <summary>Two 32-bit floats — <c>DXGI_FORMAT_R32G32_FLOAT</c> / <c>VK_FORMAT_R32G32_SFLOAT</c>.</summary>
    R32G32Float = 0,
    /// <summary>Three 32-bit floats — <c>DXGI_FORMAT_R32G32B32_FLOAT</c> / <c>VK_FORMAT_R32G32B32_SFLOAT</c>.</summary>
    R32G32B32Float = 1,
    /// <summary>Four 32-bit floats — <c>DXGI_FORMAT_R32G32B32A32_FLOAT</c> / <c>VK_FORMAT_R32G32B32A32_SFLOAT</c>.</summary>
    R32G32B32A32Float = 2,
}
/// <summary>Facts about a <see cref="GpuVertexFormat"/>.</summary>
public static class GpuVertexFormats {
    /// <summary>Gets the bytes one attribute of a format occupies.</summary>
    /// <param name="format">The attribute format.</param>
    /// <returns>The attribute's size in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not defined.</exception>
    public static uint SizeBytes(GpuVertexFormat format) => format switch {
        GpuVertexFormat.R32G32Float => 8U,
        GpuVertexFormat.R32G32B32Float => 12U,
        GpuVertexFormat.R32G32B32A32Float => 16U,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: "The vertex attribute format is not defined.",
            paramName: nameof(format)
        ),
    };
}
