using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// The heaps one device places its buffers and images in, in place of a committed resource each: a
/// <see cref="GpuMemoryPool{TKey, TBlock}"/> of heaps of <see cref="HeapBytes"/> per heap type, resource category (buffers,
/// render-target or depth textures, other textures, kept apart as resource heap tier 1 asks; a render target or depth
/// stencil stays committed, see <see cref="DirectXTextures.Create"/>) and role, each resource placed at an offset aligned
/// as the device reports for it. The pool's policy decides which heap a resource takes and when a heap goes. The device's
/// memory counts (<see cref="GpuDeviceMemoryWork"/>) count heaps, the memory the device actually holds, and a placed
/// resource's release (<see cref="DirectXDeviceMemory.CountReleased"/>) returns its range (<see cref="Release"/>), found
/// through a token the resource itself carries (private data under <see cref="TokenKey"/>), never through its address,
/// which a later resource may reuse. A placed resource holds its heap, so releasing the arena's reference never frees
/// memory a resource still sits in: <see cref="FreeAll"/> releases every heap once as the device is torn down, whatever
/// resources a device loss left in them, and retires the arena, which then places nothing; a resource released after it
/// returns no range. Thread-safe: a resource is created inside the pool's lock, so one device's placed creations run one
/// at a time and none overlaps <see cref="FreeAll"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXHeapArena {
    /// <summary>The bytes of a shared heap.</summary>
    public const ulong HeapBytes = (64UL << 20);

    /// <summary>The private-data key a placed resource carries its token under.</summary>
    public static readonly Guid TokenKey = new(g: "4f6b1c2e-8a0d-4e57-9b3f-2d7c6a1e5b90");

    // Every placed resource of every arena, by the token it carries, so a release that names only the resource finds its
    // range.
    private static readonly ConcurrentDictionary<long, (DirectXHeapArena Arena, GpuMemoryPlacement<nint> Placement)> Placed = [];

    private static long Tokens;

    private readonly IDirectXCommandCalls m_calls;
    private readonly nint m_device;
    private readonly GpuDeviceMemoryWork? m_memory;
    private readonly GpuMemoryPool<(D3D12_HEAP_TYPE Type, D3D12_HEAP_FLAGS Category, GpuMemoryRole Role), nint> m_pool;

    /// <summary>Initializes a new instance of the <see cref="DirectXHeapArena"/> class.</summary>
    /// <param name="calls">The device calls it makes.</param>
    /// <param name="device">The device, which keys its memory counts.</param>
    /// <param name="memory">The device's memory counts, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="calls"/> is <see langword="null"/>.</exception>
    public DirectXHeapArena(IDirectXCommandCalls calls, nint device, GpuDeviceMemoryWork? memory) {
        ArgumentNullException.ThrowIfNull(argument: calls);

        m_calls = calls;
        m_device = device;
        m_memory = memory;
        m_pool = new(allocate: Allocate, blockBytes: HeapBytes, free: (heap, _) => Free(heap: heap));
    }

    /// <summary>Gets the heaps the arena holds now.</summary>
    public int Heaps => m_pool.Blocks;

    /// <summary>Creates a resource placed in a heap of a type: one with room, or a new one.</summary>
    /// <param name="description">The resource's description.</param>
    /// <param name="heapType">The heap type it lives on.</param>
    /// <param name="initialState">The state it is created in.</param>
    /// <param name="clearValue">Its optimized clear value, or <see langword="null"/>.</param>
    /// <param name="role">What its memory is for, which decides whether its heap is counted.</param>
    /// <returns>The resource, owned by the caller, who reports its release (<see cref="DirectXDeviceMemory.CountReleased"/>).</returns>
    /// <exception cref="DeviceLostException">The device was removed.</exception>
    /// <exception cref="DirectXException">A creation failed for another reason.</exception>
    /// <exception cref="ObjectDisposedException">The arena was retired (<see cref="FreeAll"/>).</exception>
    public ID3D12Resource* Create(in D3D12_RESOURCE_DESC description, D3D12_HEAP_TYPE heapType, D3D12_RESOURCE_STATES initialState, D3D12_CLEAR_VALUE? clearValue, GpuMemoryRole role) {
        var resourceDescription = description;
        var sized = description;
        var info = m_calls.AllocationInfo(description: &sized);
        var category = ((description.Dimension == D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER)
            ? D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_BUFFERS
            : (((description.Flags & (D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL)) != 0)
                ? D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_RT_DS_TEXTURES
                : D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_NON_RT_DS_TEXTURES));
        var hasClear = clearValue.HasValue;
        var clear = clearValue.GetValueOrDefault();

        // The resource is created and registered under the pool's lock, so FreeAll, which takes it too, never releases its
        // heap between the placement and the creation, and its sweep of Placed sees every resource the arena created.
        return ((ID3D12Resource*)m_pool.Place(alignment: info.Alignment, key: (heapType, category, role), size: info.SizeInBytes, bind: placement => {
            var placedDescription = resourceDescription;
            var placedClear = clear;
            ID3D12Resource* resource = null;

            m_calls.CreatePlacedResource(
                clearValue: (hasClear ? &placedClear : null),
                description: &placedDescription,
                heap: ((ID3D12Heap*)placement.Native),
                initialState: initialState,
                offset: placement.Offset,
                resource: &resource
            ).ThrowIfFailed(calls: m_calls, operation: "ID3D12Device::CreatePlacedResource");

            var token = Interlocked.Increment(location: ref Tokens);
            var tagged = m_calls.Tag(key: TokenKey, resource: resource, value: token);

            if (tagged.Failed) {
                m_calls.ReleaseResource(resource: resource);
                tagged.ThrowIfFailed(calls: m_calls, operation: "ID3D12Object::SetPrivateData");
            }
            Placed[token] = (this, placement);

            return ((nint)resource);
        }));
    }
    /// <summary>Returns the range a placed resource held, as the resource is released.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="calls">The calls that read its token, or <see langword="null"/> for the resource's device's own.</param>
    /// <returns>Whether an arena placed it.</returns>
    public static bool Release(nint resource, IDirectXCommandCalls? calls = null) {
        if (Placed.IsEmpty) {
            return false;
        }
        if (!(calls ?? new DirectXDeviceCommandCalls(device: null)).TryReadTag(key: TokenKey, resource: ((ID3D12Resource*)resource), value: out var token) || !Placed.TryRemove(key: token, value: out var placed)) {
            return false;
        }
        placed.Arena.m_pool.Return(placement: placed.Placement);

        return true;
    }
    /// <summary>Releases every heap once, as the device is released, and retires the arena. A second call does
    /// nothing.</summary>
    public void FreeAll() {
        if (!m_pool.FreeAll()) {
            return;
        }
        foreach (var (token, placed) in Placed) {
            if (ReferenceEquals(objA: placed.Arena, objB: this)) { _ = Placed.TryRemove(key: token, value: out _); }
        }
    }

    // Creates a heap and counts it in the segment its type's memory pool lies in.
    private nint Allocate((D3D12_HEAP_TYPE Type, D3D12_HEAP_FLAGS Category, GpuMemoryRole Role) key, ulong bytes) {
        var description = new D3D12_HEAP_DESC {
            Flags = key.Category,
            Properties = new D3D12_HEAP_PROPERTIES { Type = key.Type },
            SizeInBytes = bytes,
        };
        ID3D12Heap* heap = null;

        m_calls.CreateHeap(description: &description, heap: &heap).ThrowIfFailed(calls: m_calls, operation: "ID3D12Device::CreateHeap");
        m_memory?.CountAllocated(
            allocation: ((nint)heap),
            bytes: checked((long)bytes),
            device: m_device,
            role: key.Role,
            segment: DirectXDeviceMemory.SegmentOf(calls: m_calls, heap: in description.Properties, segments: m_memory.SegmentsOf(device: m_device))
        );

        return ((nint)heap);
    }
    // Releases a heap and counts its release; the device's teardown and an emptied heap release alike.
    private void Free(nint heap) {
        _ = m_memory?.CountReleased(allocation: heap, device: m_device);
        m_calls.ReleaseHeap(heap: ((ID3D12Heap*)heap));
    }
}
