namespace Puck.Abstractions.Gpu;

/// <summary>The width of the indices an indexed draw reads.</summary>
public enum GpuIndexFormat : uint {
    /// <summary>16-bit unsigned indices — <c>DXGI_FORMAT_R16_UINT</c> / <c>VK_INDEX_TYPE_UINT16</c>.</summary>
    UInt16 = 0,
    /// <summary>32-bit unsigned indices — <c>DXGI_FORMAT_R32_UINT</c> / <c>VK_INDEX_TYPE_UINT32</c>.</summary>
    UInt32 = 1,
}
/// <summary>Facts about a <see cref="GpuIndexFormat"/>.</summary>
public static class GpuIndexFormats {
    /// <summary>Gets the bytes one index of a format occupies.</summary>
    /// <param name="format">The index format.</param>
    /// <returns>The index's size in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not defined.</exception>
    public static uint SizeBytes(GpuIndexFormat format) => format switch {
        GpuIndexFormat.UInt16 => 2U,
        GpuIndexFormat.UInt32 => 4U,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: "The index format is not defined.",
            paramName: nameof(format)
        ),
    };
}
