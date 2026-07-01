namespace Puck.Abstractions.Gpu;

/// <summary>
/// Backend-neutral shader stage flags. Each backend maps these flags to its native shader-stage representation.
/// </summary>
public static class GpuShaderStage {
    /// <summary>The vertex shader stage.</summary>
    public const uint Vertex = 0x00000001;
    /// <summary>The fragment (pixel) shader stage.</summary>
    public const uint Fragment = 0x00000010;
    /// <summary>The compute shader stage.</summary>
    public const uint Compute = 0x00000020;
}
