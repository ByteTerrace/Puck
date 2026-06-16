using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a host-visible readback buffer and its backing memory, freeing both when disposed.
/// </summary>
public sealed class VulkanFrameReadbackBuffer : IDisposable {
    private bool m_disposed;
    private readonly IVulkanFrameReadbackApi m_frameReadbackApi;

    /// <summary>Gets the native <c>VkBuffer</c> handle, or zero once disposed.</summary>
    public nint BufferHandle { get; private set; }
    /// <summary>Gets the native <c>VkDevice</c> handle that owns the buffer.</summary>
    public nint DeviceHandle { get; }
    /// <summary>Gets the native <c>VkDeviceMemory</c> handle backing the buffer, or zero once disposed.</summary>
    public nint MemoryHandle { get; private set; }
    /// <summary>Gets the size, in bytes, of the buffer.</summary>
    public ulong SizeBytes { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanFrameReadbackBuffer"/> class, taking ownership of an existing buffer and its memory.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the buffer.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle to own.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle backing the buffer.</param>
    /// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
    /// <param name="frameReadbackApi">The API used to destroy the buffer on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frameReadbackApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/>, <paramref name="bufferHandle"/>, or <paramref name="memoryHandle"/> is zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeBytes"/> is zero.</exception>
    public VulkanFrameReadbackBuffer(
        nint deviceHandle,
        nint bufferHandle,
        nint memoryHandle,
        ulong sizeBytes,
        IVulkanFrameReadbackApi frameReadbackApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: frameReadbackApi);

        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == bufferHandle) {
            throw new ArgumentException(
                message: "Vulkan readback buffer handle must be non-zero.",
                paramName: nameof(bufferHandle)
            );
        }

        if (0 == memoryHandle) {
            throw new ArgumentException(
                message: "Vulkan readback memory handle must be non-zero.",
                paramName: nameof(memoryHandle)
            );
        }

        if (0 == sizeBytes) {
            throw new ArgumentOutOfRangeException(
                actualValue: sizeBytes,
                message: "Vulkan readback buffer size must be non-zero.",
                paramName: nameof(sizeBytes)
            );
        }

        DeviceHandle = deviceHandle;
        BufferHandle = bufferHandle;
        MemoryHandle = memoryHandle;
        SizeBytes = sizeBytes;
        m_frameReadbackApi = frameReadbackApi;
    }

    /// <summary>Destroys the owned buffer and frees its backing memory. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_frameReadbackApi.DestroyBuffer(request: new(
            BufferHandle: BufferHandle,
            DeviceHandle: DeviceHandle,
            MemoryHandle: MemoryHandle
        ));
        BufferHandle = 0;
        MemoryHandle = 0;
        m_disposed = true;
    }
}
