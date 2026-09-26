using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a buffer and its memory made by <see cref="IVulkanBufferApi"/>, and releases both through that API when
/// disposed. A <see cref="VulkanBufferMemory.HostCoherent"/> or <see cref="VulkanBufferMemory.HostCoherentDeviceLocal"/>
/// buffer is mapped once, at construction, and stays mapped
/// for its lifetime, so a write is visible to the device without a flush and a read after the device's work completes
/// needs no invalidate. A device-local buffer is never mapped, and its host operations throw.
/// </summary>
public sealed class VulkanBuffer : IGpuStorageBuffer {
    private readonly IVulkanBufferApi m_bufferApi;

    private bool m_disposed;
    private VulkanBufferHandles m_handles;
    private nint m_mappedPointer;

    /// <summary>Gets the native <c>VkBuffer</c> handle, or zero once disposed.</summary>
    public nint BufferHandle => m_handles.Buffer;
    /// <summary>Gets the buffer, its memory, and the device that owns them; zero handles once disposed.</summary>
    public VulkanBufferHandles Handles => m_handles;
    /// <summary>Gets the memory the buffer was allocated from.</summary>
    public VulkanBufferMemory Memory { get; }
    /// <summary>Gets the size, in bytes, of the buffer.</summary>
    public ulong SizeBytes { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanBuffer"/> class, taking ownership of a buffer made by
    /// <paramref name="bufferApi"/> and mapping it when its memory is host-coherent.</summary>
    /// <param name="bufferApi">The API that made the buffer and that maps and destroys it.</param>
    /// <param name="handles">The buffer and memory to own.</param>
    /// <param name="memory">The memory the buffer was allocated from.</param>
    /// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bufferApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The buffer or memory handle is zero.</exception>
    public VulkanBuffer(IVulkanBufferApi bufferApi, VulkanBufferHandles handles, VulkanBufferMemory memory, ulong sizeBytes) {
        ArgumentNullException.ThrowIfNull(argument: bufferApi);
        VulkanArgument.RequireHandle(
            handle: handles.Buffer,
            handleDescription: "buffer",
            paramName: nameof(handles)
        );
        VulkanArgument.RequireHandle(
            handle: handles.Memory,
            handleDescription: "device-memory",
            paramName: nameof(handles)
        );

        m_bufferApi = bufferApi;
        m_handles = handles;
        Memory = memory;
        SizeBytes = sizeBytes;

        if (memory is VulkanBufferMemory.HostCoherent or VulkanBufferMemory.HostCoherentDeviceLocal) {
            m_mappedPointer = bufferApi.Map(
                handles: handles,
                sizeBytes: sizeBytes
            );
        }
    }

    /// <summary>Creates a buffer through <paramref name="bufferApi"/> and returns its owner; a buffer whose owner cannot be
    /// constructed is destroyed before the exception propagates.</summary>
    /// <param name="bufferApi">The API that makes, maps, and destroys the buffer.</param>
    /// <param name="device">The device context the buffer is created on.</param>
    /// <param name="usage">A bitmask of <see cref="VulkanBufferUsageFlags"/>.</param>
    /// <param name="memory">The memory the buffer is allocated from.</param>
    /// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
    /// <returns>A new, owning <see cref="VulkanBuffer"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bufferApi"/> is <see langword="null"/>.</exception>
    public static VulkanBuffer Create(IVulkanBufferApi bufferApi, IVulkanDeviceContext device, uint usage, VulkanBufferMemory memory, ulong sizeBytes) {
        ArgumentNullException.ThrowIfNull(argument: bufferApi);

        var handles = bufferApi.Create(
            device: device,
            memory: memory,
            sizeBytes: sizeBytes,
            usage: usage
        );

        try {
            return new VulkanBuffer(
                bufferApi: bufferApi,
                handles: handles,
                memory: memory,
                sizeBytes: sizeBytes
            );
        } catch {
            bufferApi.Destroy(handles: handles);
            throw;
        }
    }

    private nint RequireMapping() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (0 == m_mappedPointer) {
            throw new InvalidOperationException(message: $"A {Memory} Vulkan buffer is not host-mapped; only a {VulkanBufferMemory.HostCoherent} or {VulkanBufferMemory.HostCoherentDeviceLocal} buffer has host access.");
        }

        return m_mappedPointer;
    }

    /// <summary>Unmaps the buffer when mapped, then destroys it and frees its memory. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (0 != m_mappedPointer) {
            m_bufferApi.Unmap(handles: m_handles);
            m_mappedPointer = 0;
        }

        m_bufferApi.Destroy(handles: m_handles);
        m_handles = default;
    }
    /// <summary>Copies the whole buffer into a new managed array through its mapping.</summary>
    /// <returns>A copy of the buffer's bytes.</returns>
    /// <exception cref="InvalidOperationException">The buffer is not host-coherent, or is too large for a managed array.</exception>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public unsafe byte[] Read() {
        var pointer = RequireMapping();

        if (SizeBytes > int.MaxValue) {
            throw new InvalidOperationException(message: "The Vulkan buffer is too large for a managed byte array.");
        }

        return new ReadOnlySpan<byte>(
            length: ((int)SizeBytes),
            pointer: ((void*)pointer)
        ).ToArray();
    }
    /// <summary>Copies the supplied data into the buffer's mapping from the start. No flush is needed: the memory is
    /// host-coherent.</summary>
    /// <typeparam name="T">The unmanaged element type of the data.</typeparam>
    /// <param name="data">The data to copy into the buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException">The data is larger than the buffer.</exception>
    /// <exception cref="InvalidOperationException">The buffer is not host-coherent.</exception>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged {
        Write(
            data: data,
            destinationOffsetBytes: 0UL
        );
    }
    /// <summary>Copies the supplied data into the buffer's mapping starting at <paramref name="destinationOffsetBytes"/>.
    /// No flush is needed: the memory is host-coherent.</summary>
    /// <typeparam name="T">The unmanaged element type of the data.</typeparam>
    /// <param name="data">The data to copy into the buffer.</param>
    /// <param name="destinationOffsetBytes">The byte offset into the buffer at which to begin writing.</param>
    /// <exception cref="ArgumentOutOfRangeException">The data plus destination offset exceeds the buffer.</exception>
    /// <exception cref="InvalidOperationException">The buffer is not host-coherent.</exception>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public unsafe void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged {
        var pointer = RequireMapping();
        var size = (((ulong)data.Length) * ((ulong)sizeof(T)));

        if (
            (destinationOffsetBytes > SizeBytes) ||
            (size > (SizeBytes - destinationOffsetBytes))
        ) {
            throw new ArgumentOutOfRangeException(
                message: "Data size plus destination offset exceeds the buffer size.",
                paramName: nameof(data)
            );
        }

        fixed (T* source = data) {
            Buffer.MemoryCopy(
                destination: ((void*)(((byte*)pointer) + destinationOffsetBytes)),
                destinationSizeInBytes: (SizeBytes - destinationOffsetBytes),
                source: source,
                sourceBytesToCopy: size
            );
        }
    }
}
