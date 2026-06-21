using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a vertex buffer and its backing memory, freeing both when disposed.
/// </summary>
public sealed class VulkanVertexBuffer : IGpuVertexBuffer {
    private bool m_disposed;
    private readonly IVulkanVertexBufferApi m_vertexBufferApi;

    /// <summary>Gets the native <c>VkBuffer</c> handle, or zero once disposed.</summary>
    public nint BufferHandle { get; private set; }
    /// <summary>Gets the native <c>VkDevice</c> handle that owns the buffer.</summary>
    public nint DeviceHandle { get; }
    /// <summary>Gets the native <c>VkDeviceMemory</c> handle backing the buffer, or zero once disposed.</summary>
    public nint MemoryHandle { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="VulkanVertexBuffer"/> class, taking ownership of an existing buffer and its memory.</summary>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle to own.</param>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the buffer.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle backing the buffer.</param>
    /// <param name="vertexBufferApi">The API used to destroy the buffer on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vertexBufferApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="bufferHandle"/>, <paramref name="deviceHandle"/>, or <paramref name="memoryHandle"/> is zero.</exception>
    public VulkanVertexBuffer(
        nint bufferHandle,
        nint deviceHandle,
        nint memoryHandle,
        IVulkanVertexBufferApi vertexBufferApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: vertexBufferApi);

        if (0 == bufferHandle) {
            throw new ArgumentException(
                message: "Vulkan vertex-buffer handle must be non-zero.",
                paramName: nameof(bufferHandle)
            );
        }

        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == memoryHandle) {
            throw new ArgumentException(
                message: "Vulkan device-memory handle must be non-zero.",
                paramName: nameof(memoryHandle)
            );
        }

        BufferHandle = bufferHandle;
        DeviceHandle = deviceHandle;
        MemoryHandle = memoryHandle;
        m_vertexBufferApi = vertexBufferApi;
    }

    /// <summary>Destroys the owned buffer and frees its backing memory. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_vertexBufferApi.DestroyVertexBuffer(request: new(
            BufferHandle: BufferHandle,
            DeviceHandle: DeviceHandle,
            MemoryHandle: MemoryHandle
        ));
        BufferHandle = 0;
        MemoryHandle = 0;
        m_disposed = true;
    }
}
