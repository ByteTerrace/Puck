using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh region: the frame's mesh draws (SdfFrame.MeshDraws) laid out by SdfMeshRegion in one GpuRegion, created like
// every other engine region (SdfWorldEngine.Regions.cs) and copied with them in the upload pass. A frame whose draw list
// is the one last packed repacks nothing; a new list is packed into the host copy and owes only the words that changed.
// The region is created by the first frame that draws a mesh, and grows, after every frame-ring fence retires, like
// program capacity; a frame without draws keeps it. No kernel reads it yet.
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
                _ = m_meshRegion!.Write(
                    bytes: MemoryMarshal.AsBytes(span: words),
                    offset: 0
                );
            }

            m_meshDraws = draws;
            m_meshLayout = layout;
        }
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

        grown = (((grown + (sizeof(uint) - 1UL)) / sizeof(uint)) * sizeof(uint));

        if (m_meshRegion is { } previous) {
            WaitForFrameRing();
            previous.Dispose();
            m_meshRegion = null;
            Volatile.Write(
                location: ref m_meshRegionBytes,
                value: 0UL
            );
        }

        m_meshRegion = CreateRegion(
            byteCount: checked((int)grown),
            name: NameOf(part: "mesh-region")
        );
        Volatile.Write(
            location: ref m_meshRegionBytes,
            value: grown
        );
    }
}
