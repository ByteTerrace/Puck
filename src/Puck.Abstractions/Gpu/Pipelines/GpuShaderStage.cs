namespace Puck.Abstractions.Gpu;

/// <summary>
/// Backend-neutral shader stage flags. Each backend maps these flags to its native shader-stage representation.
/// </summary>
[Flags]
public enum GpuShaderStage : uint {
    /// <summary>No stage.</summary>
    None = 0,
    /// <summary>The vertex shader stage.</summary>
    Vertex = 0x00000001,
    /// <summary>The fragment (pixel) shader stage.</summary>
    Fragment = 0x00000010,
    /// <summary>The compute shader stage.</summary>
    Compute = 0x00000020,
}
