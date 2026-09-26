using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh region: the frame's mesh draws (SdfFrame.MeshDraws) laid out by SdfMeshRegion in one GpuRegion, created like
// every other engine region (SdfWorldEngine.Regions.cs) and copied with them in the upload pass. A frame whose draw list
// is the one last packed repacks nothing; a new list is packed into the host copy and owes only the words that changed.
// The region is created with the engine, one draw record long so every set that binds it binds a buffer, and grows,
// after every frame-ring fence retires, like program capacity, rebinding its new buffers; a frame without draws keeps
// it. The mesh pass reads the draws' triangles from it, and primary a mesh hit's material.
public sealed partial class SdfWorldEngine {
    private readonly Dictionary<SdfMesh, SdfMeshRegionMesh> m_meshPlacements = new(comparer: ReferenceEqualityComparer.Instance);

    // The draw list last packed, the words it packed into and their layout.
    private IReadOnlyList<SdfMeshDraw>? m_meshDraws;
    private SdfMeshRegionLayout m_meshLayout;
    private GpuRegion m_meshRegion;
    // One more for every new draw list the region packs: the cadence signature folds it, so a frame whose draws moved
    // renders.
    private long m_meshRevision;
    private ulong m_meshRegionBytes;

    private uint[] m_meshWords = [];

    /// <summary>Gets the mesh region's layout for the draws the latest frame staged; empty before a frame draws a
    /// mesh.</summary>
    public SdfMeshRegionLayout MeshRegionLayout => m_meshLayout;
    /// <summary>Gets the bytes the mesh region holds, which its buffers are allocated for: one draw record's until a frame
    /// needs more. Safe to read from any thread.</summary>
    public ulong MeshRegionBytes => Volatile.Read(location: ref m_meshRegionBytes);

    // Packs a new draw list into the region, growing it first when the list needs more bytes; the upload pass sends the
    // slot what it owes. Called with the slot's fence retired.
    private void StageMeshRegion(IReadOnlyList<SdfMeshDraw> draws) {
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
                _ = m_meshRegion.Write(
                    bytes: MemoryMarshal.AsBytes(span: words),
                    offset: 0
                );
            }

            m_meshDraws = draws;
            m_meshLayout = layout;
            m_meshRevision++;
        }

        m_meshDrawCount = ((uint)draws.Count);
    }
    // Replaces the region with one grown by half again (or to the need, if larger), after every frame-ring fence retires,
    // and binds the replacement's buffers into every set. The replacement starts owing every word, so the next write
    // sends the whole packed list, and writes its buffers into the reserved copy sets the old one wrote, so growing takes
    // no descriptor range.
    private void EnsureMeshRegionCapacity(ulong bytes) {
        var current = ((ulong)m_meshRegion.ByteCount);

        if (bytes <= current) {
            return;
        }

        var grown = Math.Max(
            val1: bytes,
            val2: (current + (current / 2UL))
        );

        grown = (((grown + (sizeof(uint) - 1UL)) / sizeof(uint)) * sizeof(uint));

        WaitForFrameRing();

        using var scope = new GpuCreationScope();
        var replacement = scope.Own(created: CreateRegion(
            byteCount: checked((int)grown),
            region: MeshRegionIndex
        ));
        var previous = m_meshRegion;

        m_meshRegion = replacement;

        try {
            for (var slot = 0; (slot < FrameRingSize); slot++) {
                BindMeshRegion(slot: slot);
            }
        } catch {
            m_meshRegion = previous;

            for (var slot = 0; (slot < FrameRingSize); slot++) {
                BindMeshRegion(slot: slot);
            }

            throw;
        }

        scope.Complete();
        previous.Dispose();
        Volatile.Write(
            location: ref m_meshRegionBytes,
            value: grown
        );
    }
}
