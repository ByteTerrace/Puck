namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates the buffers a draw reads its geometry from: a host-visible buffer created once with its contents, whose
/// declared usages say whether it holds vertices, indices, or both.
/// </summary>
public interface IGpuGeometryBufferFactory {
    /// <summary>Creates a geometry buffer holding a copy of the supplied bytes.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="data">The bytes to copy in; not empty.</param>
    /// <param name="usage">The usages: <see cref="GpuBufferUsage.Vertex"/>, <see cref="GpuBufferUsage.Index"/>, or
    /// both.</param>
    /// <returns>A new, owning <see cref="IGpuBuffer"/> the size of <paramref name="data"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> is empty or undefined.</exception>
    IGpuBuffer Create(IGpuDeviceContext deviceContext, ReadOnlySpan<byte> data, GpuBufferUsage usage);
}
