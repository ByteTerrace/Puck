using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Holds the heap arena's laws apart from every other test: its registry of placed resources is the process's.</summary>
[CollectionDefinition(name: nameof(HeapArenaCollection), DisableParallelization = true)]
public sealed class HeapArenaCollection;

/// <summary>
/// Laws over a device's heap arena (<see cref="DirectXHeapArena"/>) on fake device calls: resources share a heap of their
/// type, category and role at the alignment the device reports, a resource past half a heap gets its own, a resource's
/// release finds its range through the token it carries rather than its address, a pool keeps one empty heap and
/// releases the next, a failed placement or tag gives back what it took, and the device's counts see one allocation per
/// heap. No device is created.
/// </summary>
[Collection(name: nameof(HeapArenaCollection))]
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXHeapArenaLawTests {
    private const ulong Heap = DirectXHeapArena.HeapBytes;
    private static readonly HRESULT Ok = new(value: 0);
    private static readonly HRESULT OutOfMemory = new(value: unchecked((int)0x8007000E));

    [Fact]
    public void ResourcesShareHeapsAndFindTheirRangesThroughTheirTokens() {
        var calls = new FakeHeapCalls();
        var work = new GpuDeviceMemoryWork(backend: "directx");
        var arena = new DirectXHeapArena(calls: calls, device: 0xD0, memory: work);
        var upload = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD;
        var gpu = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;

        // Two buffers share a heap at the device's alignment; a texture and a render target take heaps of their own
        // categories, and a buffer past half a heap that no heap holds gets one its size.
        var first = Create(arena: arena, description: Buffer(bytes: 1000UL), heapType: upload, role: GpuMemoryRole.HostVisible);
        var second = Create(arena: arena, description: Buffer(bytes: 1000UL), heapType: upload, role: GpuMemoryRole.HostVisible);
        var texture = Create(arena: arena, description: Texture(flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE), heapType: gpu, role: GpuMemoryRole.DeviceLocal);
        var target = Create(arena: arena, description: Texture(flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET), heapType: gpu, role: GpuMemoryRole.DeviceLocal);
        var large = Create(arena: arena, description: Buffer(bytes: Heap), heapType: upload, role: GpuMemoryRole.HostVisible);

        Assert.Equal(expected: 4, actual: arena.Heaps);
        Assert.Equal(
            expected: [
                $"heap 1 {Heap} {D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_BUFFERS}",
                $"place {first} heap 1 at 0",
                $"place {second} heap 1 at 65536",
                $"heap 2 {Heap} {D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_NON_RT_DS_TEXTURES}",
                $"place {texture} heap 2 at 0",
                $"heap 3 {Heap} {D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_RT_DS_TEXTURES}",
                $"place {target} heap 3 at 0",
                $"heap 4 {Heap} {D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_BUFFERS}",
                $"place {large} heap 4 at 0",
            ],
            actual: calls.Log
        );
        Assert.True(condition: work.TryRead(kind: GpuDeviceMemoryWork.Allocations, value: out var allocations));
        Assert.Equal(expected: 4L, actual: allocations);
        Assert.True(condition: work.TryRead(kind: GpuDeviceMemoryWork.Allocated, value: out var allocated));
        Assert.Equal(expected: ((long)(2UL * Heap)), actual: allocated);

        // A release finds the range through the resource's token, once; a resource the arena never placed, or one whose
        // token it already returned, is not its to release. A pool keeps one empty heap and releases the next.
        calls.Log.Clear();
        Assert.False(condition: DirectXHeapArena.Release(calls: calls, resource: 0x77));
        Assert.True(condition: DirectXHeapArena.Release(calls: calls, resource: first));
        Assert.False(condition: DirectXHeapArena.Release(calls: calls, resource: first));
        Assert.True(condition: DirectXHeapArena.Release(calls: calls, resource: second));
        Assert.True(condition: DirectXHeapArena.Release(calls: calls, resource: large));
        Assert.Equal(expected: ["release heap 4"], actual: calls.Log);

        // A failed placement, and a failed tag, give their range back to the heap, which the next resource takes.
        calls.Log.Clear();
        calls.FailPlacement = true;
        _ = Assert.Throws<DirectXException>(testCode: () => Create(arena: arena, description: Buffer(bytes: 1000UL), heapType: upload, role: GpuMemoryRole.HostVisible));
        calls.FailPlacement = false;
        calls.FailTag = true;
        _ = Assert.Throws<DirectXException>(testCode: () => Create(arena: arena, description: Buffer(bytes: 1000UL), heapType: upload, role: GpuMemoryRole.HostVisible));
        calls.FailTag = false;
        var again = Create(arena: arena, description: Buffer(bytes: 1000UL), heapType: upload, role: GpuMemoryRole.HostVisible);
        Assert.Equal(expected: ["place 0 heap 1 at 0", $"place {again - 1} heap 1 at 0", $"release resource {again - 1}", $"place {again} heap 1 at 0"], actual: calls.Log);

        // A heap the device refuses surfaces as the call's failure.
        calls.FailHeap = true;
        _ = Assert.Throws<DirectXException>(testCode: () => Create(arena: arena, description: Buffer(bytes: 1000UL), heapType: gpu, role: GpuMemoryRole.DeviceLocal));
        calls.FailHeap = false;

        // The device's release frees every heap and forgets every token, so no later release finds one.
        calls.Log.Clear();
        arena.FreeAll();
        Assert.Equal(expected: 0, actual: arena.Heaps);
        Assert.Equal(expected: ["release heap 1", "release heap 2", "release heap 3"], actual: calls.Log);
        Assert.False(condition: DirectXHeapArena.Release(calls: calls, resource: texture));
        Assert.True(condition: work.TryRead(kind: GpuDeviceMemoryWork.Released, value: out var released));
        Assert.Equal(expected: allocated, actual: released);

        // An arena with no counts places alike.
        var uncounted = new DirectXHeapArena(calls: calls, device: 0xD0, memory: null);
        _ = Create(arena: uncounted, description: Buffer(bytes: 1000UL), heapType: gpu, role: GpuMemoryRole.DeviceLocal);
        uncounted.FreeAll();
        _ = Assert.Throws<ArgumentNullException>(testCode: () => new DirectXHeapArena(calls: null!, device: 0, memory: null));
    }

    private static nint Create(DirectXHeapArena arena, D3D12_RESOURCE_DESC description, D3D12_HEAP_TYPE heapType, GpuMemoryRole role) =>
        ((nint)arena.Create(clearValue: null, description: in description, heapType: heapType, initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, role: role));
    private static D3D12_RESOURCE_DESC Buffer(ulong bytes) => new() {
        Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
        Height = 1,
        Width = bytes,
    };
    private static D3D12_RESOURCE_DESC Texture(D3D12_RESOURCE_FLAGS flags) => new() {
        Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
        Flags = flags,
        Height = 64,
        Width = 64,
    };

    // Heaps and resources are numbered handles, never dereferenced; a resource's tags live here, as private data would on
    // the resource. A texture sizes at 64 KiB, a buffer at its width rounded up to 64 KiB.
    private sealed class FakeHeapCalls : IDirectXCommandCalls {
        private readonly Dictionary<(nint Resource, Guid Key), long> m_tags = [];
        private nint m_heaps;
        private nint m_resources = 0x1000;

        public List<string> Log { get; } = [];
        public bool FailHeap { get; set; }
        public bool FailPlacement { get; set; }
        public bool FailTag { get; set; }

        public D3D12_RESOURCE_ALLOCATION_INFO AllocationInfo(D3D12_RESOURCE_DESC* description) => new() {
            Alignment = 65536UL,
            SizeInBytes = ((description->Dimension == D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER) ? ((description->Width + 65535UL) & ~65535UL) : 65536UL),
        };
        public HRESULT CreateHeap(D3D12_HEAP_DESC* description, ID3D12Heap** heap) {
            if (FailHeap) {
                return OutOfMemory;
            }
            *heap = ((ID3D12Heap*)(++m_heaps));
            Log.Add(item: $"heap {(nint)(*heap)} {description->SizeInBytes} {description->Flags}");

            return Ok;
        }
        public HRESULT CreatePlacedResource(ID3D12Heap* heap, ulong offset, D3D12_RESOURCE_DESC* description, D3D12_RESOURCE_STATES initialState, D3D12_CLEAR_VALUE* clearValue, ID3D12Resource** resource) {
            *resource = (FailPlacement ? null : ((ID3D12Resource*)(++m_resources)));
            Log.Add(item: $"place {(nint)(*resource)} heap {(nint)heap} at {offset}");

            return (FailPlacement ? OutOfMemory : Ok);
        }
        public void ReleaseHeap(ID3D12Heap* heap) => Log.Add(item: $"release heap {(nint)heap}");
        public void ReleaseResource(ID3D12Resource* resource) => Log.Add(item: $"release resource {(nint)resource}");
        public HRESULT Tag(ID3D12Resource* resource, Guid key, long value) {
            if (FailTag) {
                return OutOfMemory;
            }
            m_tags[(((nint)resource), key)] = value;

            return Ok;
        }
        public bool TryReadTag(ID3D12Resource* resource, Guid key, out long value) => m_tags.TryGetValue(key: (((nint)resource), key), value: out value);
        public HRESULT CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE type, ID3D12CommandAllocator** allocator) => throw new NotSupportedException();
        public HRESULT CreateCommandList(D3D12_COMMAND_LIST_TYPE type, ID3D12CommandAllocator* allocator, ID3D12GraphicsCommandList** commandList) => throw new NotSupportedException();
        public HRESULT CreateCommittedResource(D3D12_HEAP_PROPERTIES* heapProperties, D3D12_HEAP_FLAGS heapFlags, D3D12_RESOURCE_DESC* description, D3D12_RESOURCE_STATES initialState, D3D12_CLEAR_VALUE* clearValue, ID3D12Resource** resource) => throw new NotSupportedException();
        public HRESULT Map(ID3D12Resource* resource, void** data) => throw new NotSupportedException();
        public HRESULT ResetAllocator(ID3D12CommandAllocator* allocator) => throw new NotSupportedException();
        public HRESULT ResetList(ID3D12GraphicsCommandList* commandList, ID3D12CommandAllocator* allocator) => throw new NotSupportedException();
        public HRESULT Close(ID3D12GraphicsCommandList* commandList) => throw new NotSupportedException();
        public HRESULT Signal(ID3D12CommandQueue* queue, ID3D12Fence* fence, ulong value) => throw new NotSupportedException();
        public HRESULT QueueWait(ID3D12CommandQueue* queue, ID3D12Fence* fence, ulong value) => throw new NotSupportedException();
        public ulong CompletedValue(ID3D12Fence* fence) => throw new NotSupportedException();
        public HRESULT SetEventOnCompletion(ID3D12Fence* fence, ulong value, HANDLE fenceEvent) => throw new NotSupportedException();
        public HRESULT DeviceRemovedReason() => Ok;
        public HRESULT TimestampFrequency(ID3D12CommandQueue* queue, ulong* frequency) => throw new NotSupportedException();
        public HRESULT CreateQueryHeap(D3D12_QUERY_HEAP_DESC* description, ID3D12QueryHeap** heap) => throw new NotSupportedException();
    }
}
