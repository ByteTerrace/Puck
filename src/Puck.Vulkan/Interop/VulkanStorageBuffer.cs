using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a host-visible storage buffer and its backing memory, with helpers to map, unmap, and write data;
/// frees both when disposed.
/// </summary>
public sealed class VulkanStorageBuffer : IDisposable {
    private bool m_disposed;
    private readonly IVulkanStorageBufferApi m_storageBufferApi;

    /// <summary>Gets the native <c>VkBuffer</c> handle, or zero once disposed.</summary>
    public nint BufferHandle { get; private set; }
    /// <summary>Gets the native <c>VkDevice</c> handle that owns the buffer.</summary>
    public nint DeviceHandle { get; }
    /// <summary>Gets the native <c>VkDeviceMemory</c> handle backing the buffer, or zero once disposed.</summary>
    public nint MemoryHandle { get; private set; }
    /// <summary>Gets the size, in bytes, of the buffer.</summary>
    public ulong SizeBytes { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanStorageBuffer"/> class, taking ownership of an existing buffer and its memory.</summary>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle to own.</param>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the buffer.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle backing the buffer.</param>
    /// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
    /// <param name="storageBufferApi">The API used to destroy the buffer and map its memory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="storageBufferApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="bufferHandle"/>, <paramref name="deviceHandle"/>, or <paramref name="memoryHandle"/> is zero.</exception>
    public VulkanStorageBuffer(
        nint bufferHandle,
        nint deviceHandle,
        nint memoryHandle,
        ulong sizeBytes,
        IVulkanStorageBufferApi storageBufferApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: storageBufferApi);

        if (0 == bufferHandle) {
            throw new ArgumentException(
                message: "Vulkan storage-buffer handle must be non-zero.",
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
        SizeBytes = sizeBytes;
        m_storageBufferApi = storageBufferApi;
    }

    /// <summary>Destroys the owned buffer and frees its backing memory. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_storageBufferApi.DestroyStorageBuffer(request: new(
            BufferHandle: BufferHandle,
            DeviceHandle: DeviceHandle,
            MemoryHandle: MemoryHandle
        ));
        BufferHandle = 0;
        MemoryHandle = 0;
        m_disposed = true;
    }
    /// <summary>Maps the buffer's backing memory into the host address space.</summary>
    /// <returns>A pointer to the mapped host memory.</returns>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public nint Map() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        return m_storageBufferApi.MapMemory(
            deviceHandle: DeviceHandle,
            memoryHandle: MemoryHandle,
            size: SizeBytes
        );
    }
    /// <summary>Unmaps the buffer's backing memory.</summary>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public void Unmap() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_storageBufferApi.UnmapMemory(
            deviceHandle: DeviceHandle,
            memoryHandle: MemoryHandle
        );
    }
    /// <summary>Maps the buffer, copies the supplied data into it from the start, and unmaps it.</summary>
    /// <typeparam name="T">The unmanaged element type of the data.</typeparam>
    /// <param name="data">The data to copy into the buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException">The data is larger than the buffer.</exception>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public unsafe void Write<T>(ReadOnlySpan<T> data) where T : unmanaged {
        var size = ((ulong)data.Length * (ulong)sizeof(T));

        if (size > SizeBytes) {
            throw new ArgumentOutOfRangeException(
                message: "Data size exceeds storage buffer size.",
                paramName: nameof(data)
            );
        }

        var pointer = Map();

        try {
            fixed (T* source = data) {
                Buffer.MemoryCopy(
                    destination: (void*)pointer,
                    destinationSizeInBytes: SizeBytes,
                    source: source,
                    sourceBytesToCopy: size
                );
            }
        } finally {
            Unmap();
        }
    }
}
