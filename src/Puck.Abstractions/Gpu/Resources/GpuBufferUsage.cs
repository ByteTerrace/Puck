namespace Puck.Abstractions.Gpu;

/// <summary>
/// The ways a buffer is read or written, declared when it is created through <see cref="IGpuBufferFactory"/>. A buffer
/// that serves several declares each.
/// </summary>
[Flags]
public enum GpuBufferUsage : uint {
    /// <summary>No usage; a buffer declares at least one.</summary>
    None = 0,
    /// <summary>The input assembler reads vertices from the buffer (Vulkan <c>VERTEX_BUFFER</c>; a Direct3D 12 vertex
    /// buffer view).</summary>
    Vertex = 0x1,
    /// <summary>The input assembler reads indices from the buffer (Vulkan <c>INDEX_BUFFER</c>; a Direct3D 12 index
    /// buffer view).</summary>
    Index = 0x2,
    /// <summary>A shader reads or writes the buffer through a binding (Vulkan <c>STORAGE_BUFFER</c>; a Direct3D 12
    /// shader resource or unordered access view).</summary>
    Storage = 0x4,
    /// <summary>A shader reads the buffer as constants (Vulkan <c>UNIFORM_BUFFER</c>; a Direct3D 12 constant buffer
    /// view).</summary>
    Uniform = 0x8,
    /// <summary>An indirect dispatch reads its group counts from the buffer (Vulkan <c>INDIRECT_BUFFER</c>; the
    /// Direct3D 12 <c>INDIRECT_ARGUMENT</c> state).</summary>
    Indirect = 0x10,
}
/// <summary>
/// The rules every buffer factory applies to its request.
/// </summary>
public static class GpuBufferUsages {
    /// <summary>Every defined usage.</summary>
    public const GpuBufferUsage All = ((GpuBufferUsage.Vertex | GpuBufferUsage.Index) | (GpuBufferUsage.Storage | GpuBufferUsage.Uniform)) | GpuBufferUsage.Indirect;

    /// <summary>Refuses an empty buffer and a usage that is empty or names an undefined bit.</summary>
    /// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
    /// <param name="usage">The declared usages.</param>
    /// <exception cref="ArgumentException"><paramref name="sizeBytes"/> is zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> is empty or undefined.</exception>
    public static void Validate(ulong sizeBytes, GpuBufferUsage usage) {
        if (sizeBytes == 0) {
            throw new ArgumentException(
                message: "A buffer holds at least one byte.",
                paramName: nameof(sizeBytes)
            );
        }

        if (
            (usage == GpuBufferUsage.None) ||
            ((usage & ~All) != 0)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: usage,
                message: "A buffer declares at least one defined usage.",
                paramName: nameof(usage)
            );
        }
    }
}
