using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a host-visible storage buffer and its backing memory, with helpers to map, unmap, and write data;
/// frees both when disposed. Host-visible memory is always allocated HOST_COHERENT (see
/// <see cref="VulkanNativeStorageBufferApi"/>'s <c>FindMemoryTypeIndex</c> call), so a write is visible to the
/// GPU without an explicit flush; the buffer is therefore mapped once and kept mapped for its lifetime instead
/// of map/unmap-per-write (mirroring <c>DirectXGpuStorageBuffer</c>'s permanently-mapped upload heap) — a
/// device-local buffer (see <see cref="Puck.Vulkan.Factories.VulkanStorageBufferFactory"/>'s <c>deviceLocal</c> flag) is never
/// host-visible, so it is never mapped here; only a host-visible buffer maps eagerly at construction.
/// </summary>
public sealed class VulkanStorageBuffer : IGpuStorageBuffer {
    private bool m_disposed;
    private nint m_mappedPointer;
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
    /// <param name="deviceLocal">Whether the backing memory is device-local (GPU-only, never host-mapped) rather
    /// than host-visible. When <see langword="false"/> (the default), the buffer maps its memory immediately and
    /// keeps it mapped for the buffer's lifetime.</param>
    /// <exception cref="ArgumentNullException"><paramref name="storageBufferApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="bufferHandle"/>, <paramref name="deviceHandle"/>, or <paramref name="memoryHandle"/> is zero.</exception>
    public VulkanStorageBuffer(
        nint bufferHandle,
        nint deviceHandle,
        nint memoryHandle,
        ulong sizeBytes,
        IVulkanStorageBufferApi storageBufferApi,
        bool deviceLocal = false
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

        if (!deviceLocal) {
            m_mappedPointer = m_storageBufferApi.MapMemory(
                deviceHandle: DeviceHandle,
                memoryHandle: MemoryHandle,
                size: SizeBytes
            );
        }
    }

    /// <summary>Destroys the owned buffer and frees its backing memory. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (0 != m_mappedPointer) {
            m_storageBufferApi.UnmapMemory(
                deviceHandle: DeviceHandle,
                memoryHandle: MemoryHandle
            );
            m_mappedPointer = 0;
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
    /// <summary>Returns the buffer's persistent host mapping, mapping it on first call if construction did not
    /// (a device-local buffer misused for a host write fails here with the same native error it always has).</summary>
    /// <returns>A pointer to the mapped host memory.</returns>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public nint Map() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (0 != m_mappedPointer) {
            return m_mappedPointer;
        }

        m_mappedPointer = m_storageBufferApi.MapMemory(
            deviceHandle: DeviceHandle,
            memoryHandle: MemoryHandle,
            size: SizeBytes
        );
        return m_mappedPointer;
    }
    /// <summary>Unmaps the buffer's backing memory. <see cref="Write{T}(ReadOnlySpan{T})"/> no longer calls this per
    /// write (the mapping persists for the buffer's lifetime) — an explicit caller may still release the mapping
    /// early; a later <see cref="Map"/> or <see cref="Write{T}(ReadOnlySpan{T})"/> call re-maps it lazily.</summary>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public void Unmap() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (0 == m_mappedPointer) {
            return;
        }

        m_storageBufferApi.UnmapMemory(
            deviceHandle: DeviceHandle,
            memoryHandle: MemoryHandle
        );
        m_mappedPointer = 0;
    }
    /// <summary>Copies the supplied data into the buffer's persistent mapping from the start. No flush is needed —
    /// the backing memory is always HOST_COHERENT.</summary>
    /// <typeparam name="T">The unmanaged element type of the data.</typeparam>
    /// <param name="data">The data to copy into the buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException">The data is larger than the buffer.</exception>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged {
        Write(data: data, destinationOffsetBytes: 0UL);
    }
    /// <summary>Copies the supplied data into the buffer's persistent mapping starting at
    /// <paramref name="destinationOffsetBytes"/>. No flush is needed — the backing memory is always HOST_COHERENT.</summary>
    /// <typeparam name="T">The unmanaged element type of the data.</typeparam>
    /// <param name="data">The data to copy into the buffer.</param>
    /// <param name="destinationOffsetBytes">The byte offset into the buffer at which to begin writing.</param>
    /// <exception cref="ArgumentOutOfRangeException">The data plus destination offset exceeds the buffer.</exception>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public unsafe void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged {
        var size = ((ulong)data.Length * (ulong)sizeof(T));

        if ((destinationOffsetBytes > SizeBytes) || (size > (SizeBytes - destinationOffsetBytes))) {
            throw new ArgumentOutOfRangeException(
                message: "Data size plus destination offset exceeds storage buffer size.",
                paramName: nameof(data)
            );
        }

        var pointer = Map();

        fixed (T* source = data) {
            Buffer.MemoryCopy(
                destination: (void*)((byte*)pointer + destinationOffsetBytes),
                destinationSizeInBytes: (SizeBytes - destinationOffsetBytes),
                source: source,
                sourceBytesToCopy: size
            );
        }
    }
}
