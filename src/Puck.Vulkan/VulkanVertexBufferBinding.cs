namespace Puck.Vulkan;

/// <summary>
/// Identifies a vertex buffer and the byte offset within it at which vertex data begins, for binding into a
/// command buffer.
/// </summary>
public readonly record struct VulkanVertexBufferBinding {
    /// <summary>Gets the native <c>VkBuffer</c> handle to bind.</summary>
    public nint BufferHandle { get; }
    /// <summary>Gets the byte offset within the buffer at which vertex data begins.</summary>
    public ulong Offset { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanVertexBufferBinding"/> struct.</summary>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle to bind.</param>
    /// <param name="offset">The byte offset within the buffer at which vertex data begins.</param>
    /// <exception cref="ArgumentException"><paramref name="bufferHandle"/> is zero.</exception>
    public VulkanVertexBufferBinding(nint bufferHandle, ulong offset = 0) {
        if (bufferHandle == 0) {
            throw new ArgumentException(
                message: "Vulkan vertex-buffer handle must be non-zero.",
                paramName: nameof(bufferHandle)
            );
        }

        BufferHandle = bufferHandle;
        Offset = offset;
    }
}
