namespace Puck.Abstractions.Gpu;

/// <summary>The data format of one vertex attribute.</summary>
public enum GpuVertexFormat : uint {
    /// <summary>Two 32-bit floats — <c>DXGI_FORMAT_R32G32_FLOAT</c> / <c>VK_FORMAT_R32G32_SFLOAT</c>.</summary>
    R32G32Float = 0,
}
