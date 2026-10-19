using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// The device memory one device sub-allocates its buffers and offscreen images from: blocks of <see cref="BlockBytes"/>
/// per memory type, role and resource kind (buffers and images apart, so no block mixes linear and optimal resources
/// within the buffer-image granularity), each resource bound at an offset a block's free ranges hand out
/// (<see cref="GpuMemoryRanges"/>). A resource larger than half a block gets a block of its own. A block holding
/// host-visible memory is mapped once for its life, and a resource's mapping is the block's at its offset. A block every
/// resource left is freed, but one empty block a pool keeps, so a bake that frees and makes staging buffers does not
/// allocate again. The device's memory counts (<see cref="GpuDeviceMemoryWork"/>) count blocks, the memory the device
/// actually holds. A block is the arena's alone to free (<see cref="Owns"/>), exactly once: by the release that leaves a
/// pool a second empty block, or by <see cref="FreeAll"/>, which unmaps and frees every block left as the device is torn
/// down, whatever resources are still bound to it (a device loss leaves them so; freeing memory a lost device's resources
/// are bound to is valid), and retires the arena, which then binds and maps nothing. Thread-safe.
/// </summary>
public sealed unsafe class VulkanMemoryArena {
    /// <summary>The bytes of a shared block.</summary>
    public const ulong BlockBytes = (64UL << 20);

    private const uint StructureTypeMemoryAllocateInfo = 5;

    private readonly VulkanDeviceCommands m_device;

    private readonly Lock m_gate = new();
    private readonly Dictionary<(uint Type, GpuMemoryRole Role, bool Image), List<Block>> m_pools = [];
    private readonly Dictionary<nint, (Block Block, ulong Offset, ulong Size)> m_slices = [];
    // The memory of every block the arena holds, and once retired of every block it freed as it retired: no other
    // allocation reuses a retired arena's handles, since its device is being destroyed.
    private readonly HashSet<nint> m_blocks = [];
    private bool m_retired;

    /// <summary>Initializes a new instance of the <see cref="VulkanMemoryArena"/> class.</summary>
    /// <param name="device">The device whose memory it allocates.</param>
    public VulkanMemoryArena(VulkanDeviceCommands device) {
        m_device = device;
    }

    /// <summary>Gets the blocks the arena holds now.</summary>
    public int Blocks {
        get {
            lock (m_gate) {
                return m_pools.Values.Sum(selector: static pool => pool.Count);
            }
        }
    }

    /// <summary>Binds a buffer or an image to memory of a type: at an offset of a block with room, or of a new block.</summary>
    /// <param name="resource">The buffer or image, created unbound.</param>
    /// <param name="image">Whether it is an image.</param>
    /// <param name="requirements">Its memory requirements.</param>
    /// <param name="memoryTypeIndex">The memory type it binds to.</param>
    /// <param name="role">What its memory is for, which decides whether the block is counted and mapped.</param>
    /// <returns>The block's memory, which the resource's handles name.</returns>
    /// <exception cref="ObjectDisposedException">The arena was retired (<see cref="FreeAll"/>).</exception>
    public nint Bind(nint resource, bool image, in VkMemoryRequirements requirements, uint memoryTypeIndex, GpuMemoryRole role) {
        lock (m_gate) {
            ObjectDisposedException.ThrowIf(condition: m_retired, instance: this);

            var key = (memoryTypeIndex, role, image);

            if (!m_pools.TryGetValue(key: key, value: out var pool)) {
                pool = [];
                m_pools.Add(key: key, value: pool);
            }

            Block? chosen = null;
            var offset = 0UL;

            foreach (var block in pool) {
                if (block.Ranges.TryAllocate(alignment: requirements.Alignment, offset: out offset, size: requirements.Size)) {
                    chosen = block;
                    break;
                }
            }
            if (chosen is null) {
                chosen = Allocate(bytes: ((requirements.Size > (BlockBytes / 2UL)) ? requirements.Size : BlockBytes), key: key);
                pool.Add(item: chosen);
                _ = chosen.Ranges.TryAllocate(alignment: requirements.Alignment, offset: out offset, size: requirements.Size);
            }
            try {
                (image ? m_device.BindImageMemory : m_device.BindBufferMemory)(
                    m_device.Handle,
                    resource,
                    chosen.Memory,
                    offset
                ).ThrowIfFailed(device: m_device, operation: (image ? "vkBindImageMemory" : "vkBindBufferMemory"));
            } catch {
                chosen.Ranges.Free(offset: offset, size: requirements.Size);
                throw;
            }
            m_slices.Add(key: resource, value: (chosen, offset, requirements.Size));

            return chosen.Memory;
        }
    }
    /// <summary>Returns the memory a resource the arena bound held, once the resource is destroyed.</summary>
    /// <param name="resource">The buffer or image.</param>
    /// <returns>Whether the arena bound it; one it did not still owns its memory.</returns>
    public bool Release(nint resource) {
        lock (m_gate) {
            if (!m_slices.Remove(key: resource, value: out var slice)) {
                return false;
            }

            var block = slice.Block;

            block.Ranges.Free(offset: slice.Offset, size: slice.Size);
            if (block.Ranges.IsEmpty) {
                var pool = m_pools[block.Key];

                // The pool keeps one empty block for the next resource; a second goes.
                if (pool.Any(predicate: other => (!ReferenceEquals(objA: other, objB: block) && other.Ranges.IsEmpty))) {
                    _ = pool.Remove(item: block);
                    Free(block: block);
                }
            }

            return true;
        }
    }
    /// <summary>Returns where a host-visible resource the arena bound is mapped: its block's mapping at its offset.</summary>
    /// <param name="resource">The buffer.</param>
    /// <param name="pointer">The mapping, or zero.</param>
    /// <returns>Whether the arena bound the resource to mapped memory.</returns>
    public bool TryMapped(nint resource, out nint pointer) {
        lock (m_gate) {
            if (!m_retired && m_slices.TryGetValue(key: resource, value: out var slice) && (slice.Block.Mapped != 0)) {
                pointer = (slice.Block.Mapped + ((nint)slice.Offset));
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
            if (m_retired) {
                return;
            }

            m_retired = true;
            foreach (var pool in m_pools.Values) {
                foreach (var block in pool) { Free(block: block, forget: false); }
            }
            m_pools.Clear();
            m_slices.Clear();
        }
    }

    // Allocates a block, counts it, and maps it for its life when its memory is host-visible.
    private Block Allocate(ulong bytes, (uint Type, GpuMemoryRole Role, bool Image) key) {
        var role = key.Role;
        var allocateInfo = new VkMemoryAllocateInfo {
            AllocationSize = bytes,
            MemoryTypeIndex = key.Type,
            SType = StructureTypeMemoryAllocateInfo,
        };

        m_device.AllocateMemory(
            m_device.Handle,
            in allocateInfo,
            0,
            out var memory
        ).ThrowIfFailed(device: m_device, operation: "vkAllocateMemory");
        m_device.CountAllocated(allocationSize: bytes, memoryHandle: memory, role: role);

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

        return new Block(key: key, mapped: mapped, memory: memory, ranges: new GpuMemoryRanges(capacity: bytes));
    }
    // Unmaps and frees a block, counting its release; a block a release frees is forgotten, since a later allocation may
    // reuse its handle.
    private void Free(Block block, bool forget = true) {
        if (forget) { _ = m_blocks.Remove(item: block.Memory); }
        if (block.Mapped != 0) {
            m_device.UnmapMemory(m_device.Handle, block.Memory);
        }
        m_device.Destroy(destroy: m_device.FreeMemory, handle: block.Memory);
    }

    // One block: its memory, its mapping or zero, its free ranges and the pool it belongs to.
    private sealed class Block((uint Type, GpuMemoryRole Role, bool Image) key, nint memory, nint mapped, GpuMemoryRanges ranges) {
        public (uint Type, GpuMemoryRole Role, bool Image) Key { get; } = key;
        public nint Memory { get; } = memory;
        public nint Mapped { get; } = mapped;
        public GpuMemoryRanges Ranges { get; } = ranges;
    }
}
