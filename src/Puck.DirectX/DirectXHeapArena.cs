using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// The heaps one device places its buffers and images in, in place of a committed resource each: heaps of
/// <see cref="HeapBytes"/> per heap type, resource category (buffers, render-target or depth textures, other textures, kept
/// apart as resource heap tier 1 asks; a render target or depth stencil stays committed, see
/// <see cref="DirectXTextures.Create"/>) and role, each resource placed at an offset a heap's free ranges hand out
/// (<see cref="GpuMemoryRanges"/>) at the alignment the device reports for it. A resource larger than half a heap gets a
/// heap of its own. A heap every resource left is released, but one empty heap a pool keeps. The device's memory counts
/// (<see cref="GpuDeviceMemoryWork"/>) count heaps, the memory the device actually holds, and a placed resource's release
/// (<see cref="DirectXDeviceMemory.CountReleased"/>) returns its range (<see cref="Release"/>), found through a token the
/// resource itself carries (private data under <see cref="TokenKey"/>), never through its address, which a later resource
/// may reuse. A placed resource holds its heap, so releasing the arena's reference never frees memory a resource still
/// sits in: <see cref="FreeAll"/> releases every heap once as the device is torn down, whatever resources a device loss
/// left in them, and retires the arena, which then places nothing; a resource released after it returns no range.
/// Thread-safe.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXHeapArena {
    /// <summary>The bytes of a shared heap.</summary>
    public const ulong HeapBytes = (64UL << 20);

    /// <summary>The private-data key a placed resource carries its token under.</summary>
    public static readonly Guid TokenKey = new(g: "4f6b1c2e-8a0d-4e57-9b3f-2d7c6a1e5b90");

    // Every placed resource of every arena, by the token it carries, so a release that names only the resource finds its
    // range.
    private static readonly ConcurrentDictionary<long, (DirectXHeapArena Arena, Heap Heap, ulong Offset, ulong Size)> Placed = [];

    private static long Tokens;

    private readonly IDirectXCommandCalls m_calls;
    private readonly nint m_device;
    private readonly GpuDeviceMemoryWork? m_memory;

    private readonly Lock m_gate = new();
    private bool m_retired;
    private readonly Dictionary<(D3D12_HEAP_TYPE Type, D3D12_HEAP_FLAGS Category, GpuMemoryRole Role), List<Heap>> m_pools = [];

    /// <summary>Initializes a new instance of the <see cref="DirectXHeapArena"/> class.</summary>
    /// <param name="calls">The device calls it makes.</param>
    /// <param name="device">The device, which keys its memory counts.</param>
    /// <param name="memory">The device's memory counts, or <see langword="null"/>.</param>
    public DirectXHeapArena(IDirectXCommandCalls calls, nint device, GpuDeviceMemoryWork? memory) {
        ArgumentNullException.ThrowIfNull(argument: calls);

        m_calls = calls;
        m_device = device;
        m_memory = memory;
    }

    /// <summary>Gets the heaps the arena holds now.</summary>
    public int Heaps {
        get {
            lock (m_gate) {
                return m_pools.Values.Sum(selector: static pool => pool.Count);
            }
        }
    }

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
        var info = m_calls.AllocationInfo(description: &resourceDescription);
        var category = ((description.Dimension == D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER)
            ? D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_BUFFERS
            : (((description.Flags & (D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL)) != 0)
                ? D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_RT_DS_TEXTURES
                : D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_NON_RT_DS_TEXTURES));
        var key = (heapType, category, role);
        Heap? chosen = null;
        var offset = 0UL;

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(condition: m_retired, instance: this);
            if (!m_pools.TryGetValue(key: key, value: out var pool)) {
                pool = [];
                m_pools.Add(key: key, value: pool);
            }
            foreach (var heap in pool) {
                if (heap.Ranges.TryAllocate(alignment: info.Alignment, offset: out offset, size: info.SizeInBytes)) {
                    chosen = heap;
                    break;
                }
            }
            if (chosen is null) {
                chosen = Allocate(bytes: ((info.SizeInBytes > (HeapBytes / 2UL)) ? info.SizeInBytes : HeapBytes), key: key);
                pool.Add(item: chosen);
                _ = chosen.Ranges.TryAllocate(alignment: info.Alignment, offset: out offset, size: info.SizeInBytes);
            }
        }

        ID3D12Resource* resource = null;
        var clear = clearValue.GetValueOrDefault();

        try {
            m_calls.CreatePlacedResource(
                clearValue: (clearValue.HasValue ? &clear : null),
                description: &resourceDescription,
                heap: chosen.Native,
                initialState: initialState,
                offset: offset,
                resource: &resource
            ).ThrowIfFailed(calls: m_calls, operation: "ID3D12Device::CreatePlacedResource");
        } catch {
            lock (m_gate) { Return(heap: chosen, offset: offset, size: info.SizeInBytes); }
            throw;
        }
        var token = Interlocked.Increment(location: ref Tokens);

        var tagged = m_calls.Tag(key: TokenKey, resource: resource, value: token);

        if (tagged.Failed) {
            m_calls.ReleaseResource(resource: resource);
            lock (m_gate) { Return(heap: chosen, offset: offset, size: info.SizeInBytes); }
            tagged.ThrowIfFailed(calls: m_calls, operation: "ID3D12Object::SetPrivateData");
        }
        Placed[token] = (this, chosen, offset, info.SizeInBytes);

        return resource;
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
        lock (placed.Arena.m_gate) {
            placed.Arena.Return(heap: placed.Heap, offset: placed.Offset, size: placed.Size);
        }

        return true;
    }
    /// <summary>Releases every heap once, as the device is released, and retires the arena. A second call does
    /// nothing.</summary>
    public void FreeAll() {
        lock (m_gate) {
            if (m_retired) {
                return;
            }

            m_retired = true;
            foreach (var pool in m_pools.Values) {
                foreach (var heap in pool) { Free(heap: heap); }
            }
            m_pools.Clear();
        }
        foreach (var (token, placed) in Placed) {
            if (ReferenceEquals(objA: placed.Arena, objB: this)) { _ = Placed.TryRemove(key: token, value: out _); }
        }
    }

    // Returns a range to its heap; a heap left empty goes when its pool already keeps another empty one.
    private void Return(Heap heap, ulong offset, ulong size) {
        heap.Ranges.Free(offset: offset, size: size);
        if (!heap.Ranges.IsEmpty || !m_pools.TryGetValue(key: heap.Key, value: out var pool)) { return; }
        if (pool.Any(predicate: other => (!ReferenceEquals(objA: other, objB: heap) && other.Ranges.IsEmpty))) {
            _ = pool.Remove(item: heap);
            Free(heap: heap);
        }
    }
    // Creates a heap and counts it.
    private Heap Allocate(ulong bytes, (D3D12_HEAP_TYPE Type, D3D12_HEAP_FLAGS Category, GpuMemoryRole Role) key) {
        var description = new D3D12_HEAP_DESC {
            Flags = key.Category,
            Properties = new D3D12_HEAP_PROPERTIES { Type = key.Type },
            SizeInBytes = bytes,
        };
        ID3D12Heap* heap = null;

        m_calls.CreateHeap(description: &description, heap: &heap).ThrowIfFailed(calls: m_calls, operation: "ID3D12Device::CreateHeap");
        if (m_memory is not null) {
            _ = m_memory.CountAllocated(allocation: ((nint)heap), bytes: checked((long)bytes), device: m_device, role: key.Role);
        }

        return new Heap(key: key, native: heap, ranges: new GpuMemoryRanges(capacity: bytes));
    }
    // Releases a heap and counts its release.
    private void Free(Heap heap) {
        _ = m_memory?.CountReleased(allocation: ((nint)heap.Native), device: m_device);
        m_calls.ReleaseHeap(heap: heap.Native);
    }

    // One heap: its native object, its free ranges and the pool it belongs to.
    private sealed class Heap((D3D12_HEAP_TYPE Type, D3D12_HEAP_FLAGS Category, GpuMemoryRole Role) key, ID3D12Heap* native, GpuMemoryRanges ranges) {
        public (D3D12_HEAP_TYPE Type, D3D12_HEAP_FLAGS Category, GpuMemoryRole Role) Key { get; } = key;
        public ID3D12Heap* Native { get; } = native;
        public GpuMemoryRanges Ranges { get; } = ranges;
    }
}
