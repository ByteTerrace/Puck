namespace Puck.Abstractions;

/// <summary>
/// Creates backend-neutral vertex buffers and uploads vertex data into them.
/// </summary>
public interface IGpuVertexBufferFactory {
    /// <summary>Creates a vertex buffer and uploads the supplied data into it.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="vertexData">The raw vertex data to upload.</param>
    /// <param name="strideBytes">The size, in bytes, of one vertex.</param>
    /// <returns>A new, owning <see cref="IGpuVertexBuffer"/>.</returns>
    IGpuVertexBuffer Create(IGpuDeviceContext deviceContext, byte[] vertexData, uint strideBytes);
}
