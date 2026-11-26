using Puck.Maths;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>A run of pages a sparse buffer binds: its range in a page block and the name its family holds in the device's
/// memory counts (<see cref="VulkanMemoryArena.PlacePages"/>).</summary>
/// <param name="Placement">The range in its block.</param>
/// <param name="Name">The token of the name the range holds in the counts, or zero where nothing counts it.</param>
public readonly record struct VulkanPagePlacement(GpuMemoryPlacement<(nint Memory, nint Mapped)> Placement, long Name);

/// <summary>
/// Implements <see cref="IGpuMappedBuffer"/> on Vulkan: a sparse buffer (<c>VK_BUFFER_CREATE_SPARSE_BINDING_BIT</c> with
/// <c>VK_BUFFER_CREATE_SPARSE_RESIDENCY_BIT</c>, so it may stand partly bound) whose address range is bound a chunk at a
/// time (<see cref="MappingBytes"/>) to runs of the device's page blocks (<see cref="VulkanMemoryArena.PlacePages"/>),
/// through <see cref="VulkanLogicalDevice.BindSparse"/>, the device's one binding door.
/// <para>Invariant: a chunk is writable in <see cref="Pages"/> only while its pages are bound ahead of every submission
/// that may write it, and its pages return to their block only after the device no longer binds or reads them.
/// <see cref="Map"/> binds a chunk through the binding door, which orders the bind after every submission made before it
/// and every later submission after the bind, and only then marks the chunk writable. <see cref="Unmap"/> revokes a
/// chunk at once and leaves its unbinding to the release its caller retires behind the chunk's readers; the release
/// queues the unbinding through the same door and hands the pages to the arena's sparse retirement
/// (<see cref="VulkanMemoryArena.RetireSparse"/>) behind the timeline value the door answered, so no block is freed and
/// no range reused while the device may still bind it. The buffer itself is destroyed the same way, behind a checkpoint
/// of the timeline taken when it is disposed. The one place either is enforced is that retirement: this type returns
/// no page and destroys no buffer anywhere else. Where it stops: a bind or checkpoint whose outcome is unknown (the
/// call threw) leaves the timeline unable to certify anything, so what it covered is held until the device is torn
/// down (<see cref="VulkanMemoryArena.FreeAll"/>).</para>
/// <para>The buffer binds no memory of its own: every counted byte is a page run's. Called on the device's submission
/// thread, as the region that owns it is.</para>
/// </summary>
public sealed unsafe class VulkanGpuMappedBuffer : IGpuMappedBuffer {
    /// <summary>The bytes one mapping covers: 1 MiB, the chunk a Direct3D 12 reserved buffer maps, so a region's
    /// placements cover the same chunks on either backend. A device whose sparse page size does not divide it creates no
    /// mapped buffer (<see cref="TryCreate"/>).</summary>
    public const ulong MappingBytes = (1UL << 20);

    // VK_BUFFER_CREATE_SPARSE_BINDING_BIT and VK_BUFFER_CREATE_SPARSE_RESIDENCY_BIT (vulkan_core.h, SDK 1.4.363).
    private const uint BufferCreateSparseBinding = 0x00000001;
    private const uint BufferCreateSparseResidency = 0x00000002;
    private const uint MemoryPropertyDeviceLocalBit = 0x00000001;
    private const uint SharingModeExclusive = 0;
    private const uint StructureTypeBufferCreateInfo = 12;

    private readonly VulkanLogicalDevice m_device;
    private readonly VulkanMemoryArena m_arena;
    private readonly string m_family;
    private readonly ulong m_alignment;
    private readonly ulong m_reservedBytes;
    private readonly uint m_memoryType;
    // Each chunk's page run while it is bound; null where it holds no backing.
    private readonly VulkanPagePlacement?[] m_chunks;

    private nint m_buffer;
    private ulong m_mapped;
    private bool m_disposed;

    private VulkanGpuMappedBuffer(VulkanLogicalDevice device, nint buffer, ulong sizeBytes, ulong reservedBytes, ulong alignment, uint memoryType, string family) {
        m_device = device;
        m_arena = device.Commands.Arena;
        m_buffer = buffer;
        m_family = family;
        m_alignment = alignment;
        m_memoryType = memoryType;
        m_reservedBytes = reservedBytes;
        SizeBytes = sizeBytes;
        m_chunks = new VulkanPagePlacement?[checked((int)(reservedBytes / MappingBytes))];
        Pages = new GpuBufferPages(bytes: ((long)sizeBytes), pageBytes: ((int)MappingBytes), writable: false);
    }

    /// <inheritdoc/>
    public nint BufferHandle => m_buffer;
    /// <inheritdoc/>
    public ulong SizeBytes { get; }
    /// <inheritdoc/>
    public GpuBufferPages Pages { get; }
    /// <inheritdoc/>
    public ulong ChunkBytes => MappingBytes;
    /// <inheritdoc/>
    public ulong MappedBytes => m_mapped;
    /// <inheritdoc/>
    public ulong SpareBytes => m_arena.SparePageBytes(memoryTypeIndex: m_memoryType);

    /// <summary>Returns the bytes one mapping of a sparse buffer on a device covers (<see cref="ChunkBytes"/>), or zero
    /// where the device enabled no sparse buffers.</summary>
    /// <param name="device">The logical device.</param>
    /// <returns>The bytes, or zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public static ulong ChunkBytesOn(VulkanLogicalDevice device) {
        ArgumentNullException.ThrowIfNull(argument: device);

        return (device.SparseBuffersEnabled ? MappingBytes : 0UL);
    }
    /// <summary>Creates a sparse buffer with no chunk bound, where the device enabled sparse buffers with a binding
    /// queue, has the address space for it beside its other sparse buffers, binds storage views as long as the buffer,
    /// and pages its sparse buffers at a size that divides <see cref="MappingBytes"/>.</summary>
    /// <param name="device">The device context it is created on.</param>
    /// <param name="sizeBytes">The bytes of address range to reserve; not zero.</param>
    /// <param name="usage">The usages, as <see cref="VulkanGpuBufferFactory.ToVkBufferUsage"/> maps them.</param>
    /// <param name="name">The buffer's debug name, whose family names every page run it binds.</param>
    /// <param name="naming">The naming the buffer is handed to.</param>
    /// <returns>The buffer, owned by the caller, or <see langword="null"/> where the device creates none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="naming"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="VulkanException">The creation failed.</exception>
    public static VulkanGpuMappedBuffer? TryCreate(IVulkanDeviceContext device, ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name, GpuObjectNaming naming) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: naming);
        GpuBufferUsages.Validate(sizeBytes: sizeBytes, usage: usage);

        var logical = device.LogicalDevice;
        var commands = logical.Commands;
        // A storage view spans the buffer whole, and the device's view limit is a uint32 some devices set lower.
        var mostView = (logical.Capabilities?.MaxStorageBufferBytes ?? 0U);
        // Every chunk is a whole mapping, so the reservation is rounded up to one; the buffer's stated size stays the
        // one asked for, which its views and its page authority span.
        var reserved = (sizeBytes.CeilingDivide(divisor: MappingBytes) * MappingBytes);

        if (!logical.SparseBuffersEnabled || ((mostView != 0U) && (sizeBytes > mostView)) || !logical.TryReserveSparseAddress(bytes: reserved)) { return null; }

        var buffer = ((nint)0);

        try {
            var createInfo = new VkBufferCreateInfo {
                Flags = (BufferCreateSparseBinding | BufferCreateSparseResidency),
                SType = StructureTypeBufferCreateInfo,
                SharingMode = SharingModeExclusive,
                Size = reserved,
                Usage = VulkanGpuBufferFactory.ToVkBufferUsage(usage: usage),
            };

            commands.CreateBuffer(commands.Handle, in createInfo, 0, out buffer).ThrowIfFailed(device: commands, operation: "vkCreateBuffer(sparse)");
            if (buffer == 0) { throw new InvalidOperationException(message: "vkCreateBuffer returned success without a valid handle for a sparse buffer."); }
            commands.GetBufferMemoryRequirements(commands.Handle, buffer, out var requirements);
            // The page size is the buffer's own alignment, never the storage-buffer view alignment. A page that does not
            // divide a mapping would leave a chunk's bind unaligned, so such a device keeps contiguous buffers.
            if ((requirements.Alignment == 0UL) || ((MappingBytes % requirements.Alignment) != 0UL)) {
                commands.Destroy(destroy: commands.DestroyBuffer, handle: buffer);
                logical.ReleaseSparseAddress(bytes: reserved);
                Console.Error.WriteLine(value: $"[backing] sparse buffers unused: a page of {requirements.Alignment} bytes does not divide the {MappingBytes}-byte mapping");

                return null;
            }
            device.Instance.Commands.GetPhysicalDeviceMemoryProperties(logical.PhysicalDevice.Handle, out var memoryProperties);

            var memoryType = VulkanMemoryTypes.FindIndex(
                memoryProperties: in memoryProperties,
                memoryTypeBits: requirements.MemoryTypeBits,
                preferredProperties: MemoryPropertyDeviceLocalBit,
                requireProperties: true,
                resourceDescription: "a sparse buffer"
            );

            naming.Name(handle: buffer, kind: GpuObjectKind.Buffer, name: in name);

            return new VulkanGpuMappedBuffer(alignment: requirements.Alignment, buffer: buffer, device: logical, family: name.MemoryFamily(), memoryType: memoryType, reservedBytes: reserved, sizeBytes: sizeBytes);
        } catch {
            commands.Destroy(destroy: commands.DestroyBuffer, handle: buffer);
            logical.ReleaseSparseAddress(bytes: reserved);

            throw;
        }
    }
    /// <inheritdoc/>
    public void Map(ulong offset, ulong bytes) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        GpuMappedChunks.RequireRange(bytes: bytes, offset: offset, sizeBytes: SizeBytes);
        ReleaseCompleted();
        if (bytes == 0UL) { return; }

        var (first, last) = GpuMappedChunks.Touched(bytes: bytes, chunkBytes: MappingBytes, offset: offset);
        var taken = new List<(int Chunk, VulkanPagePlacement Pages)>();

        // Every run is placed before any is bound, so a refusal gives back what it took and binds nothing.
        try {
            for (var chunk = first; (chunk <= last); chunk++) {
                if (m_chunks[chunk] is null) { taken.Add(item: (chunk, m_arena.PlacePages(alignment: m_alignment, bytes: MappingBytes, family: m_family, memoryTypeIndex: m_memoryType))); }
            }
        } catch {
            foreach (var (_, pages) in taken) { m_arena.ReturnPages(pages: pages); }

            throw;
        }
        if (taken.Count == 0) { return; }

        var binds = new VkSparseMemoryBind[taken.Count];

        for (var at = 0; (at < binds.Length); at++) {
            var (chunk, pages) = taken[at];

            binds[at] = new VkSparseMemoryBind { Memory = pages.Placement.Native.Memory, MemoryOffset = pages.Placement.Offset, ResourceOffset = (((ulong)chunk) * MappingBytes), Size = MappingBytes };
        }
        try {
            _ = m_device.BindSparse(buffer: m_buffer, bindings: binds);
        } catch {
            // The bind's outcome is unknown, so the device may hold these pages bound: they go back only at teardown.
            foreach (var (_, pages) in taken) { m_arena.RetireSparse(after: null, release: () => m_arena.ReturnPages(pages: pages)); }

            throw;
        }
        foreach (var (chunk, pages) in taken) {
            m_chunks[chunk] = pages;
            m_mapped += MappingBytes;
            Pages.SetWritable(count: 1, firstPage: chunk, writable: true);
        }
    }
    /// <inheritdoc/>
    public GpuMappedRelease Unmap(ulong offset, ulong bytes) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        GpuMappedChunks.RequireRange(bytes: bytes, offset: offset, sizeBytes: SizeBytes);
        ReleaseCompleted();

        var (first, stop) = GpuMappedChunks.Inside(bytes: bytes, chunkBytes: MappingBytes, chunkCount: m_chunks.Length, offset: offset, sizeBytes: SizeBytes);
        List<(int Chunk, VulkanPagePlacement Pages)>? revoked = null;
        var released = 0UL;

        for (var chunk = first; (chunk < stop); chunk++) {
            if (m_chunks[chunk] is not { } pages) { continue; }
            Pages.SetWritable(count: 1, firstPage: chunk, writable: false);
            m_chunks[chunk] = null;
            m_mapped -= MappingBytes;
            released += MappingBytes;
            (revoked ??= []).Add(item: (chunk, pages));
        }

        return ((revoked is null) ? GpuMappedRelease.None : new GpuMappedRelease(bytes: released, release: () => Release(revoked: revoked)));
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;

        var buffer = m_buffer;
        var commands = m_device.Commands;
        var device = m_device;
        var reserved = m_reservedBytes;
        var arena = m_arena;
        List<VulkanPagePlacement> held = [];

        for (var chunk = 0; (chunk < m_chunks.Length); chunk++) {
            if (m_chunks[chunk] is { } pages) { held.Add(item: pages); }
            m_chunks[chunk] = null;
        }
        m_buffer = 0;
        m_mapped = 0UL;

        // The owner disposes the buffer once nothing reads it, but a binding this buffer queued may still name it, and
        // its pages stay bound until it is destroyed: both go behind a checkpoint taken after everything submitted so
        // far. A timeline that can give none (a lost device, a bind of unknown outcome) leaves them to the teardown.
        ulong? after;

        try {
            after = (commands.IsDestroyed ? null : device.Timeline.Checkpoint());
        } catch (Exception exception) when (exception is DeviceLostException or VulkanException or InvalidOperationException or ObjectDisposedException) {
            after = null;
        }
        arena.RetireSparse(after: after, release: () => {
            commands.Destroy(destroy: commands.DestroyBuffer, handle: buffer);
            device.ReleaseSparseAddress(bytes: reserved);
            foreach (var pages in held) { arena.ReturnPages(pages: pages); }
        });
        ReleaseCompleted();
    }

    // Releases revoked chunks once their readers have finished: queues their unbinding, in order after every submission
    // already made, and retires their pages behind the timeline value the binding door answers. A chunk mapped again
    // since its revocation was bound to new pages by the bind that mapped it, which took its old ones out of the buffer,
    // so those wait behind a checkpoint taken now. A disposed buffer retired its own destruction, which unbinds
    // everything, so its revoked pages wait behind a checkpoint as well.
    private void Release(List<(int Chunk, VulkanPagePlacement Pages)> revoked) {
        ulong? after;

        try {
            if (m_device.Commands.IsDestroyed) {
                after = null;
            } else {
                var unbound = new List<VkSparseMemoryBind>(capacity: revoked.Count);

                if (!m_disposed) {
                    foreach (var (chunk, _) in revoked) {
                        if (m_chunks[chunk] is null) { unbound.Add(item: new VkSparseMemoryBind { ResourceOffset = (((ulong)chunk) * MappingBytes), Size = MappingBytes }); }
                    }
                }
                after = ((unbound.Count > 0)
                    ? m_device.BindSparse(buffer: m_buffer, bindings: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: unbound))
                    : m_device.Timeline.Checkpoint());
            }
        } catch (Exception exception) when (exception is DeviceLostException or VulkanException or InvalidOperationException or ObjectDisposedException) {
            after = null;
        }

        var arena = m_arena;

        arena.RetireSparse(after: after, release: () => {
            foreach (var (_, pages) in revoked) { arena.ReturnPages(pages: pages); }
        });
        ReleaseCompleted();
    }
    // Releases what the arena's sparse retirement holds behind values the timeline has passed. A timeline that can no
    // longer be read (a lost device, a bind of unknown outcome) releases nothing: the teardown does.
    private void ReleaseCompleted() {
        if (m_device.Commands.IsDestroyed) { return; }
        try {
            m_arena.ReleaseSparse(completed: m_device.Timeline.Completed);
        } catch (Exception exception) when (exception is DeviceLostException or VulkanException or InvalidOperationException or ObjectDisposedException) {
        }
    }
}
