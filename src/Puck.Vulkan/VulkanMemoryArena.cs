using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// The device memory one device sub-allocates its buffers and offscreen images from: a
/// <see cref="GpuMemoryPool{TKey, TBlock}"/> of blocks of <see cref="BlockBytes"/> per memory type, role and resource kind
/// (buffers and images apart, so no block mixes linear and optimal resources within the buffer-image granularity), each
/// resource bound at an offset aligned as its requirements ask. The pool's policy decides which block a resource takes and
/// when a block goes. A block holding host-visible memory is mapped once for its life, and a resource's mapping is the
/// block's at its offset. The device's memory counts (<see cref="GpuDeviceMemoryWork"/>) count blocks, the memory the
/// device actually holds. A block is the arena's alone to free (<see cref="Owns"/>), exactly once: by the release that
/// empties it, or by <see cref="FreeAll"/>, which unmaps and frees every block left as the device is torn down, whatever
/// resources are still bound to it (a device loss leaves them so; freeing memory a lost device's resources are bound to is
/// valid), and retires the arena, which then binds and maps nothing. Thread-safe.
/// </summary>
public sealed unsafe class VulkanMemoryArena {
    /// <summary>The bytes of a shared block.</summary>
    public const ulong BlockBytes = (64UL << 20);

    private const uint StructureTypeMemoryAllocateInfo = 5;

    private readonly VulkanDeviceCommands m_device;

    // Guards the slices and the blocks' handles, and every call into the pool: the pool's allocate and free run under its
    // own lock and write m_blocks, so a pool call made outside this gate would race Owns.
    private readonly Lock m_gate = new();

    private readonly GpuMemoryPool<(uint Type, GpuMemoryRole Role, bool Image), (nint Memory, nint Mapped)> m_pool;

    // Each bound resource's placement and the name its family holds in the device's counts, by its handle and whether it
    // is an image: a buffer and an image may share a handle value, as a layer that wraps handles (RenderDoc) hands them
    // out.
    private readonly Dictionary<(nint Resource, bool Image), (GpuMemoryPlacement<(nint Memory, nint Mapped)> Placement, long Name)> m_slices = [];
    // The memory of every block the arena holds, and once retired of every block it freed as it retired: no other
    // allocation reuses a retired arena's handles, since its device is being destroyed.
    private readonly HashSet<nint> m_blocks = [];

    /// <summary>Initializes a new instance of the <see cref="VulkanMemoryArena"/> class.</summary>
    /// <param name="device">The device whose memory it allocates.</param>
    public VulkanMemoryArena(VulkanDeviceCommands device) {
        m_device = device;
        m_pool = new(allocate: Allocate, blockBytes: BlockBytes, free: Free);
        if (m_device.Memory is not null) { m_device.Memory.TrimRequested += Trim; }
    }

    /// <summary>Gets the blocks the arena holds now.</summary>
    public int Blocks => m_pool.Blocks;

    /// <summary>Binds a buffer or an image to memory of a type: at an offset of a block with room, or of a new block.</summary>
    /// <param name="resource">The buffer or image, created unbound.</param>
    /// <param name="image">Whether it is an image.</param>
    /// <param name="requirements">Its memory requirements.</param>
    /// <param name="memoryTypeIndex">The memory type it binds to.</param>
    /// <param name="role">What its memory is for, which decides whether the block is counted and mapped.</param>
    /// <param name="family">The family its placement is named to in the device's counts
    /// (<see cref="GpuObjectName.MemoryFamily"/>).</param>
    /// <returns>The block's memory, which the resource's handles name.</returns>
    /// <exception cref="ObjectDisposedException">The arena was retired (<see cref="FreeAll"/>).</exception>
    /// <exception cref="VulkanException">The block's allocation, mapping or the resource's binding failed.</exception>
    /// <exception cref="InvalidOperationException">The arena holds a binding of this handle already.</exception>
    public nint Bind(nint resource, bool image, in VkMemoryRequirements requirements, uint memoryTypeIndex, GpuMemoryRole role, string family) {
        var alignment = requirements.Alignment;
        var size = requirements.Size;

        lock (m_gate) {
            // A handle still recorded is one whose release never came; binding over it would orphan its range.
            if (m_slices.ContainsKey(key: (resource, image))) {
                throw new InvalidOperationException(message: $"The {(image ? "image" : "buffer")} 0x{resource:X} is bound already; a handle is bound once until its release.");
            }

            // The bind and its record run inside the pool's lock: a throw from either gives the range back.
            return m_pool.Place(alignment: alignment, key: (memoryTypeIndex, role, image), payer: family, size: size, bind: placement => {
                (image ? m_device.BindImageMemory : m_device.BindBufferMemory)(
                    m_device.Handle,
                    resource,
                    placement.Native.Memory,
                    placement.Offset
                ).ThrowIfFailed(device: m_device, operation: (image ? "vkBindImageMemory" : "vkBindBufferMemory"));
                m_slices.Add(key: (resource, image), value: (placement, (m_device.Memory?.CountPlaced(allocation: placement.Native.Memory, bytes: checked((long)placement.Size), device: m_device.Handle, family: family) ?? 0L)));

                return placement.Native.Memory;
            });
        }
    }
    /// <summary>Returns the memory a resource the arena bound held, once the resource is destroyed.</summary>
    /// <param name="resource">The buffer or image.</param>
    /// <param name="image">Whether it is an image.</param>
    /// <returns>Whether the arena bound it; one it did not still owns its memory.</returns>
    public bool Release(nint resource, bool image) {
        lock (m_gate) {
            if (!m_slices.Remove(key: (resource, image), value: out var slice)) {
                return false;
            }

            if (slice.Name != 0) { _ = m_device.Memory?.CountUnplaced(token: slice.Name); }
            m_pool.Return(placement: slice.Placement);

            return true;
        }
    }
    /// <summary>Returns where a host-visible resource the arena bound is mapped: its block's mapping at its offset.</summary>
    /// <param name="resource">The buffer.</param>
    /// <param name="pointer">The mapping, or zero.</param>
    /// <returns>Whether the arena bound the resource to mapped memory.</returns>
    public bool TryMapped(nint resource, out nint pointer) {
        lock (m_gate) {
            if (!m_pool.IsRetired && m_slices.TryGetValue(key: (resource, false), value: out var slice) && (slice.Placement.Native.Mapped != 0)) {
                pointer = (slice.Placement.Native.Mapped + ((nint)slice.Placement.Offset));
                return true;
            }
        }
        pointer = 0;

        return false;
    }
    /// <summary>Gets whether a memory handle is one of the arena's blocks, which only the arena frees, maps and
    /// unmaps; a retired arena still owns the blocks it freed as it retired.</summary>
    /// <param name="memory">The memory handle.</param>
    /// <returns>Whether it is one of the arena's blocks.</returns>
    public bool Owns(nint memory) {
        lock (m_gate) {
            return m_blocks.Contains(item: memory);
        }
    }
    /// <summary>Unmaps and frees every block once, as the device is torn down, and retires the arena. A second call
    /// does nothing.</summary>
    public void FreeAll() {
        lock (m_gate) {
            if (m_pool.FreeAll()) { m_slices.Clear(); }
            if (m_device.Memory is not null) { m_device.Memory.TrimRequested -= Trim; }
        }
    }

    /// <summary>Releases empty blocks and marks held blocks for release after their last fenced placement returns.</summary>
    public void Trim() {
        // Pool callbacks mutate m_blocks, so pressure uses the same outer lock as Bind and Release.
        lock (m_gate) { m_pool.Trim(); }
    }

    // Asks the ledger for a block at its size, allocates it, counts it, and maps it for its life when its memory is
    // host-visible. A block the account or the device refuses is never allocated, and its grant is handed back.
    private (nint Memory, nint Mapped) Allocate(GpuMemoryBlockRequest<(uint Type, GpuMemoryRole Role, bool Image)> request) {
        var key = request.Key;
        var bytes = request.Bytes;
        var role = key.Role;
        var allocateInfo = new VkMemoryAllocateInfo {
            AllocationSize = bytes,
            MemoryTypeIndex = key.Type,
            SType = StructureTypeMemoryAllocateInfo,
        };
        var memory = m_device.AllocateCounted(allocateInfo: in allocateInfo, family: null, payer: request.Payer, payerBytes: request.PayerBytes, role: role);

        var mapped = ((nint)0);

        if (role is GpuMemoryRole.HostVisible or GpuMemoryRole.HostVisibleDeviceLocal) {
            try {
                m_device.MapMemory(
                    m_device.Handle,
                    memory,
                    0,
                    ((nuint)bytes),
                    0,
                    out mapped
                ).ThrowIfFailed(device: m_device, operation: "vkMapMemory");
            } catch {
                m_device.Destroy(destroy: m_device.FreeMemory, handle: memory);
                throw;
            }
        }

        _ = m_blocks.Add(item: memory);

        return (memory, mapped);
    }
    // Unmaps and frees a block, counting its release; a block a release frees is forgotten, since a later allocation may
    // reuse its handle, while one the arena's retirement frees stays owned.
    private void Free((nint Memory, nint Mapped) block, bool retiring) {
        if (!retiring) { _ = m_blocks.Remove(item: block.Memory); }
        if (block.Mapped != 0) {
            m_device.UnmapMemory(m_device.Handle, block.Memory);
        }
        m_device.Destroy(destroy: m_device.FreeMemory, handle: block.Memory);
    }
}
