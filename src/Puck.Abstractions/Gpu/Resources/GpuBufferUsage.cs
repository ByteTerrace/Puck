namespace Puck.Abstractions.Gpu;

/// <summary>
/// The ways a geometry buffer is read, declared when it is created through <see cref="IGpuGeometryBufferFactory"/>.
/// </summary>
[Flags]
public enum GpuBufferUsage : uint {
    /// <summary>No usage; a geometry buffer declares at least one.</summary>
    None = 0,
    /// <summary>The input assembler reads vertices from the buffer (Vulkan <c>VERTEX_BUFFER</c>; a Direct3D 12 vertex
    /// buffer view).</summary>
    Vertex = 0x1,
    /// <summary>The input assembler reads indices from the buffer (Vulkan <c>INDEX_BUFFER</c>; a Direct3D 12 index
    /// buffer view).</summary>
    Index = 0x2,
}
/// <summary>
/// The rules every geometry buffer factory applies to its request.
/// </summary>
public static class GpuBufferUsages {
    /// <summary>The usages a geometry buffer may declare.</summary>
    public const GpuBufferUsage All = GpuBufferUsage.Vertex | GpuBufferUsage.Index;

    /// <summary>Refuses an empty buffer, or a usage that is empty or undefined.</summary>
    /// <param name="sizeBytes">The buffer's size in bytes.</param>
    /// <param name="usage">The declared usages.</param>
    /// <exception cref="ArgumentException"><paramref name="sizeBytes"/> is zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> is empty or undefined.</exception>
    public static void Validate(int sizeBytes, GpuBufferUsage usage) {
        if (sizeBytes == 0) {
            throw new ArgumentException(
                message: "A geometry buffer holds at least one byte.",
                paramName: nameof(sizeBytes)
            );
        }

        if (
            (usage == GpuBufferUsage.None) ||
            ((usage & ~All) != 0)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: usage,
                message: "A geometry buffer declares at least one defined usage.",
                paramName: nameof(usage)
            );
        }
    }
}
