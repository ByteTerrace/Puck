using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh region: the frame's mesh draws (SdfFrame.MeshDraws) laid out by SdfMeshRegion in one GpuRegion, under the
// residency policy the device's memory profile selects for its size (a ring where it selects in place), copied by the
// device's region-copy pipeline under the staged policy. A frame whose draw list is the one last packed repacks nothing; a new list is packed into the
// host copy and owes only the words that changed. The region is created by the first frame that draws a mesh, and
// grows, after every frame-ring fence retires, like program capacity; a frame without draws keeps it. No kernel reads
// it yet.
public sealed partial class SdfWorldEngine {
    private readonly Dictionary<SdfMesh, SdfMeshRegionMesh> m_meshPlacements = new(comparer: ReferenceEqualityComparer.Instance);

    // The draw list last packed, the words it packed into and their layout.
    private IReadOnlyList<SdfMeshDraw>? m_meshDraws;
    private SdfMeshRegionLayout m_meshLayout;
    private GpuRegion? m_meshRegion;
    private ulong m_meshRegionBytes;

    private uint[] m_meshWords = [];

    /// <summary>Gets the mesh region's layout for the draws the latest frame staged; empty before a frame draws a
    /// mesh.</summary>
    public SdfMeshRegionLayout MeshRegionLayout => m_meshLayout;
    /// <summary>Gets the bytes the mesh region holds, which its buffers are allocated for, or zero before a frame draws a
    /// mesh. Safe to read from any thread.</summary>
    public ulong MeshRegionBytes => Volatile.Read(location: ref m_meshRegionBytes);

    // The pool a staged mesh region creates beside the engine's own, which the engine's admission covers so the
    // region's first creation has room; a grown region returns its range before taking the same size again.
    private static GpuDescriptorPoolSizes MeshRegionPoolSizes =>
        GpuRegion.CopyPoolSizes(slotCount: FrameRingSize);

    // Records the staged region's copy for this slot; nothing without a region or with nothing owed.
    private void RecordMeshRegionCopy(nint commandBuffer) =>
        m_meshRegion?.RecordCopy(
            commandBuffer: commandBuffer,
            slot: m_currentSlot
        );
    // Packs a new draw list into the region, growing it first when the list needs more bytes, then sends this slot
    // what it owes. Called with the slot's fence retired.
    private void StageMeshRegion(IReadOnlyList<SdfMeshDraw> draws, int slot) {
        if (!ReferenceEquals(
            objA: draws,
            objB: m_meshDraws
        )) {
            var layout = SdfMeshRegion.Plan(
                draws: draws,
                meshes: m_meshPlacements
            );

            if (layout.Words > 0) {
                if (layout.Words > m_meshWords.Length) {
                    m_meshWords = new uint[layout.Words];
                }

                var words = m_meshWords.AsSpan(
                    length: layout.Words,
                    start: 0
                );

                SdfMeshRegion.Write(
                    destination: words,
                    draws: draws,
                    layout: layout,
                    meshes: m_meshPlacements
                );
                EnsureMeshRegionCapacity(bytes: layout.Bytes);
                _ = m_meshRegion!.Write(
                    bytes: MemoryMarshal.AsBytes(span: words),
                    offset: 0
                );
            }

            m_meshDraws = draws;
            m_meshLayout = layout;
        }

        m_meshRegion?.Flush(slot: slot);
    }
    // Creates the region at the size the draws need, or replaces it with one grown by half again (or to the need, if
    // larger), after every frame-ring fence retires. The replacement starts owing every word, so the next write sends
    // the whole packed list. The old region's copy pool is returned before the new one is created, so a grown region
    // reuses its range of the descriptor heap.
    private void EnsureMeshRegionCapacity(ulong bytes) {
        var current = ((ulong)(m_meshRegion?.ByteCount ?? 0));

        if (bytes <= current) {
            return;
        }

        var grown = Math.Max(
            val1: bytes,
            val2: (current + (current / 2UL))
        );

        // Headroom never carries a region past what one staged copy dispatch holds, unless the draws alone need more,
        // which GpuRegion refuses by name under the staged policy.
        grown = Math.Min(
            val1: (((grown + (sizeof(uint) - 1UL)) / sizeof(uint)) * sizeof(uint)),
            val2: Math.Max(
                val1: bytes,
                val2: (((ulong)GpuRegion.MaxStagedWords) * sizeof(uint))
            )
        );

        if (m_meshRegion is { } previous) {
            WaitForFrameRing();
            previous.Dispose();
            m_meshRegion = null;
            Volatile.Write(
                location: ref m_meshRegionBytes,
                value: 0UL
            );
        }

        var byteCount = checked((int)grown);
        var policy = GpuResidency.Select(
            byteCount: grown,
            profile: m_deviceContext.MemoryProfile
        );

        // An in-place region is written while no frame reading it is in flight, and the frame ring always has one in
        // flight, so the engine writes a per-slot ring instead.
        if (policy == GpuResidencyPolicy.InPlace) {
            policy = GpuResidencyPolicy.Ring;
        }

        m_meshRegion = new GpuRegion(
            bindings: m_gpu.Bindings,
            buffers: m_gpu.BufferFactory,
            byteCount: byteCount,
            copyPipeline: m_frameUploadPipeline,
            policy: policy,
            recorder: m_gpu.Recorder,
            slotCount: FrameRingSize
        );
        Volatile.Write(
            location: ref m_meshRegionBytes,
            value: grown
        );
    }
}
