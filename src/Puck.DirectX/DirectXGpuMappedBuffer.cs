using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Maths;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>A run of 64 KiB tiles a reserved buffer maps: its range in a tile heap and the name its family holds in the
/// device's memory counts (<see cref="DirectXHeapArena.PlaceTiles"/>).</summary>
/// <param name="Placement">The range in its heap.</param>
/// <param name="Name">The token of the name the range holds in the counts, or zero where nothing counts it.</param>
public readonly record struct DirectXTilePlacement(GpuMemoryPlacement<nint> Placement, long Name);

/// <summary>
/// Implements <see cref="IGpuMappedBuffer"/> on Direct3D 12: a reserved buffer (<c>ID3D12Device::CreateReservedResource</c>)
/// whose 64 KiB tiles are mapped a chunk at a time (<see cref="ChunkTiles"/> tiles) to runs of the device's tile heaps
/// (<see cref="DirectXHeapArena.PlaceTiles"/>), through <c>ID3D12CommandQueue::UpdateTileMappings</c> on the device's queue.
/// <para>Invariant: a chunk is writable in <see cref="Pages"/> only while its tiles are mapped ahead of every submission
/// that may write it, and its tiles return to their heap only after the queue no longer reads them. <see cref="Map"/>
/// queues a chunk's mapping through <see cref="DirectXDeviceContext.CommandQueueHandle"/>, the door that first hands the
/// open batch's recorded lists to the queue, so every list recorded later executes after the mapping, and only then marks
/// the chunk writable. <see cref="Unmap"/> revokes a chunk at once and leaves its unmapping to the release its caller
/// retires behind the chunk's readers; the release queues the unmapping and a signal of the buffer's own fence after it,
/// and a run returns to its heap only once that fence has passed, so no heap is freed or range reused while the queue may
/// still map it. The reserved resource holds no memory of its own: every counted byte is a tile run's.</para>
/// <para>Called on the device's frame thread, as the region that owns it is.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuMappedBuffer : IGpuMappedBuffer {
    /// <summary>The bytes of one tile of a reserved buffer.</summary>
    public const ulong TileBytes = (64UL << 10);
    /// <summary>The tiles one mapping covers: 1 MiB, so a residency whose region holds a few draw records backs a chunk,
    /// not a heap, and a 4 GiB reservation's page table stays a few thousand chunks.</summary>
    public const uint ChunkTiles = 16U;

    private readonly DirectXDeviceContext m_context;
    // The arena and device the buffer was created on: a lost device's successor has its own, which never takes these tiles.
    private readonly DirectXHeapArena m_arena;
    private readonly nint m_device;
    private readonly string m_family;
    // Each chunk's tile run while it is mapped; null where it holds no backing.
    private readonly DirectXTilePlacement?[] m_chunks;
    private readonly uint m_tiles;

    // Tile runs whose chunks were released, each with the fence value signaled after the queue's update that took them out
    // of the buffer; a run returns to its heap only once the fence reaches it, so no heap is freed or range reused while
    // the buffer may still map it.
    private readonly Queue<(ulong Value, DirectXTilePlacement Tiles)> m_returning = [];
    private readonly nint m_fence;

    private nint m_resource;
    private ulong m_mapped;
    private ulong m_fenceValue;
    private bool m_disposed;

    private DirectXGpuMappedBuffer(DirectXDeviceContext context, nint resource, ulong sizeBytes, string family) {
        m_context = context;
        m_arena = context.HeapArena;
        m_device = context.Device.Handle;
        ((ID3D12Device*)m_device)->CreateFence(Flags: default, InitialValue: 0, ppFence: out ID3D12Fence* fence);
        m_fence = ((nint)fence);
        m_resource = resource;
        m_family = family;
        SizeBytes = sizeBytes;
        m_tiles = checked((uint)sizeBytes.CeilingDivide(divisor: TileBytes));
        m_chunks = new DirectXTilePlacement?[m_tiles.CeilingDivide(divisor: ChunkTiles)];
        Pages = new GpuBufferPages(bytes: ((long)sizeBytes), pageBytes: ((int)ChunkBytes), writable: false);
    }

    /// <inheritdoc/>
    public nint BufferHandle => m_resource;
    /// <inheritdoc/>
    public ulong SizeBytes { get; }
    /// <inheritdoc/>
    public GpuBufferPages Pages { get; }
    /// <inheritdoc/>
    public ulong ChunkBytes => (ChunkTiles * TileBytes);
    /// <inheritdoc/>
    public ulong MappedBytes => m_mapped;

    /// <summary>Creates a reserved buffer with no tile mapped, where the device has reserved resources at tier 2 or above
    /// (tier 1 leaves an unmapped tile's reads undefined).</summary>
    /// <param name="context">The device context it is created on.</param>
    /// <param name="sizeBytes">The bytes of address range to reserve; not zero.</param>
    /// <param name="usage">The usages; <see cref="GpuBufferUsage.Storage"/> allows unordered access.</param>
    /// <param name="name">The buffer's debug name, whose family names every tile run it maps.</param>
    /// <returns>The buffer, owned by the caller, or <see langword="null"/> where the device has no such tier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="DeviceLostException">The device was removed.</exception>
    /// <exception cref="DirectXException">The creation failed for another reason.</exception>
    public static DirectXGpuMappedBuffer? TryCreate(DirectXDeviceContext context, ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) {
        ArgumentNullException.ThrowIfNull(argument: context);
        GpuBufferUsages.Validate(sizeBytes: sizeBytes, usage: usage);

        var device = ((ID3D12Device*)context.Device.Handle);

        if ((context.Capabilities?.ReservedResourcesTier ?? 0U) < 2U) {
            return null;
        }

        var calls = new DirectXDeviceCommandCalls(device: device);
        var description = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Flags = (((usage & GpuBufferUsage.Storage) != 0) ? D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS : D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE),
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Height = 1,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = sizeBytes,
        };
        ID3D12Resource* resource = null;

        calls.CreateReservedResource(description: &description, initialState: DirectXGpuBufferFactory.DeviceLocalState, resource: &resource)
            .ThrowIfFailed(calls: calls, operation: "ID3D12Device::CreateReservedResource");
        context.Services.Naming.Name(handle: ((nint)resource), kind: GpuObjectKind.Buffer, name: in name);

        return new DirectXGpuMappedBuffer(context: context, family: name.MemoryFamily(), resource: ((nint)resource), sizeBytes: sizeBytes);
    }

    /// <inheritdoc/>
    public void Map(ulong offset, ulong bytes) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        RequireRange(bytes: bytes, offset: offset);
        ReturnCompleted();
        if (bytes == 0UL) { return; }

        var first = ((int)(offset / ChunkBytes));
        var last = ((int)offset.LastUnitOf(bytes: bytes, unit: ChunkBytes));
        var arena = m_arena;
        var taken = new List<(int Chunk, DirectXTilePlacement Tiles)>();

        // Every run is placed before any is mapped, so a refusal gives back what it took and maps nothing.
        try {
            for (var chunk = first; (chunk <= last); chunk++) {
                if (m_chunks[chunk] is null) { taken.Add(item: (chunk, arena.PlaceTiles(bytes: (TilesOf(chunk: chunk) * TileBytes), family: m_family))); }
            }
        } catch {
            foreach (var (_, tiles) in taken) { arena.ReturnTiles(tiles: tiles); }

            throw;
        }
        if (taken.Count == 0) { return; }

        var calls = DirectXDeviceCommandCalls.Of(deviceContext: m_context);
        var queue = ((ID3D12CommandQueue*)m_context.CommandQueueHandle);

        foreach (var (chunk, tiles) in taken) {
            calls.UpdateTileMappings(
                firstTile: (((uint)chunk) * ChunkTiles),
                heap: ((ID3D12Heap*)tiles.Placement.Native),
                heapFirstTile: ((uint)(tiles.Placement.Offset / TileBytes)),
                queue: queue,
                resource: ((ID3D12Resource*)m_resource),
                tileCount: TilesOf(chunk: chunk)
            );
            m_chunks[chunk] = tiles;
            m_mapped += tiles.Placement.Size;
            Pages.SetWritable(count: 1, firstPage: chunk, writable: true);
        }
    }
    /// <inheritdoc/>
    public GpuMappedRelease Unmap(ulong offset, ulong bytes) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        RequireRange(bytes: bytes, offset: offset);
        ReturnCompleted();

        var end = (offset + bytes);
        // A chunk lies wholly inside when the range covers it to its end, or to the buffer's end for the last, partial one.
        var first = ((int)offset.CeilingDivide(divisor: ChunkBytes));
        var stop = ((end == SizeBytes) ? m_chunks.Length : ((int)(end / ChunkBytes)));
        List<(int Chunk, DirectXTilePlacement Tiles)>? revoked = null;
        var released = 0UL;

        for (var chunk = first; (chunk < stop); chunk++) {
            if (m_chunks[chunk] is not { } tiles) { continue; }
            Pages.SetWritable(count: 1, firstPage: chunk, writable: false);
            m_chunks[chunk] = null;
            m_mapped -= tiles.Placement.Size;
            released += tiles.Placement.Size;
            (revoked ??= []).Add(item: (chunk, tiles));
        }

        return ((revoked is null) ? GpuMappedRelease.None : new GpuMappedRelease(bytes: released, release: () => Release(revoked: revoked)));
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        // A queued update may still name the resource and its tiles' heaps: the release waits for it on the fence, which a
        // removed device completes at once.
        if ((m_returning.Count > 0) && (DirectXSharedFence.ReadCompletedValue(fence: ((ID3D12Fence*)m_fence)) < m_fenceValue)) {
            try {
                ((ID3D12Fence*)m_fence)->SetEventOnCompletion(Value: m_fenceValue, hEvent: default);
            } catch (System.Runtime.InteropServices.COMException) {
                // A removed device runs nothing further; its tiles go back with it.
            }
        }
        if (m_resource != 0) {
            _ = ((IUnknown*)m_resource)->Release();
            m_resource = 0;
        }
        // The owner disposes the buffer once nothing reads it; a released resource's tiles need no unmapping.
        for (var chunk = 0; (chunk < m_chunks.Length); chunk++) {
            if (m_chunks[chunk] is { } tiles) { m_arena.ReturnTiles(tiles: tiles); }
            m_chunks[chunk] = null;
        }
        while (m_returning.Count > 0) { m_arena.ReturnTiles(tiles: m_returning.Dequeue().Tiles); }
        _ = ((IUnknown*)m_fence)->Release();
        m_mapped = 0UL;
    }

    // The tiles of a chunk: ChunkTiles, or fewer for the last chunk of a reservation that is not a whole number of them.
    private uint TilesOf(int chunk) => Math.Min(val1: ChunkTiles, val2: (m_tiles - (((uint)chunk) * ChunkTiles)));
    private void RequireRange(ulong offset, ulong bytes) {
        if ((offset > SizeBytes) || (bytes > (SizeBytes - offset))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(bytes), message: $"The range of {bytes} bytes at {offset} passes the buffer's {SizeBytes}.");
        }
    }
    // Releases revoked chunks once their readers have finished: queues their unmapping, in order after every submission
    // already made, and the fence signal after it, and returns their tiles once the fence reaches it. A chunk mapped again
    // since its revocation holds new tiles, which the update that mapped them already took its old ones out of, so it
    // waits on the same signal. A disposed buffer's resource is gone, and a lost device's queue runs nothing, so their
    // tiles only go back.
    private void Release(List<(int Chunk, DirectXTilePlacement Tiles)> revoked) {
        if (m_disposed || (m_context.Device.Handle != m_device)) {
            foreach (var (_, tiles) in revoked) { m_arena.ReturnTiles(tiles: tiles); }

            return;
        }

        var calls = DirectXDeviceCommandCalls.Of(deviceContext: m_context);
        var queue = ((ID3D12CommandQueue*)m_context.CommandQueueHandle);

        foreach (var (chunk, _) in revoked) {
            if (m_chunks[chunk] is not null) { continue; }
            calls.UpdateTileMappings(firstTile: (((uint)chunk) * ChunkTiles), heap: null, heapFirstTile: 0U, queue: queue, resource: ((ID3D12Resource*)m_resource), tileCount: TilesOf(chunk: chunk));
        }
        DirectXCommandCalls.Signal(calls: calls, fence: ((ID3D12Fence*)m_fence), queue: queue, value: ++m_fenceValue);
        foreach (var (_, tiles) in revoked) { m_returning.Enqueue(item: (m_fenceValue, tiles)); }
        ReturnCompleted();
    }
    // Returns the released tile runs whose update the queue has executed.
    private void ReturnCompleted() {
        if (m_returning.Count == 0) { return; }

        var completed = DirectXSharedFence.ReadCompletedValue(fence: ((ID3D12Fence*)m_fence));

        while ((m_returning.Count > 0) && (m_returning.Peek().Value <= completed)) { m_arena.ReturnTiles(tiles: m_returning.Dequeue().Tiles); }
    }
}
