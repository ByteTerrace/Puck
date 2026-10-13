using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh region: the frame's mesh draws (SdfFrame.MeshDraws) laid out by SdfMeshRegion in one GpuRegion, created like
// every other table (SdfWorldTables.Regions.cs) and copied with them in the upload. A frame whose draw list is the one
// last packed, at the revision and program composability last packed, repacks nothing; any other is packed into the
// host copy and owes only the words that changed. A list that names the meshes the region holds, at the same places and
// with the same atlases, repacks only its draw records, so bodies that move every frame never rewrite their meshes'
// vertices and indices. The region is created with the tables, one draw record long so every
// set that binds it binds a buffer, and grows, once the device is idle, like program capacity; a frame without draws
// keeps it. A view's mesh pass reads the draws' triangles from it, and primary a mesh hit's material. A textured mesh's
// texture coordinates are written moved into the mesh atlases the same draw list packs (SdfWorldTables.MeshAtlas.cs).
public sealed partial class SdfWorldTables {
    // The placements the region holds, and the scratch the next list plans into; the two swap after each plan.
    private Dictionary<SdfMesh, SdfMeshRegionMesh> m_meshPlacements = new(comparer: ReferenceEqualityComparer.Instance);
    private Dictionary<SdfMesh, SdfMeshRegionMesh> m_meshPlacementsNext = new(comparer: ReferenceEqualityComparer.Instance);
    // The region and atlases the mesh section was last written into and for.
    private GpuRegion? m_meshSectionRegion;
    private SdfMeshAtlas? m_meshSectionAtlas;

    // The draw list last packed, the words it packed into and their layout.
    private IReadOnlyList<SdfMeshDraw>? m_meshDraws;
    private long m_meshDrawsRevision;

    private bool m_meshIndirectInstancesComposable = true;

    private SdfMeshRegionLayout m_meshLayout;
    private GpuRegion m_meshRegion;
    // One more for every new draw list the region packs: the cadence signature folds it, so a frame whose draws moved
    // renders.
    private long m_meshRevision;
    private ulong m_meshRegionBytes;
    // The draws the frame being rendered rasterizes, from its staged draw list.
    private uint m_meshDrawCount;

    private uint[] m_meshWords = [];

    /// <summary>Gets the mesh region's layout for the draws the latest frame staged; empty before a frame draws a
    /// mesh.</summary>
    public SdfMeshRegionLayout MeshRegionLayout => m_meshLayout;
    /// <summary>Gets the bytes the mesh region holds, which its buffers are allocated for: one draw record's until a frame
    /// needs more. Safe to read from any thread.</summary>
    public ulong MeshRegionBytes => Volatile.Read(location: ref m_meshRegionBytes);
    /// <summary>Gets the draws the latest frame's mesh passes rasterize in each view.</summary>
    public uint MeshDrawCount => m_meshDrawCount;

    // The draw list the latest frame staged, whose draws a view's mesh pass records one call each.
    internal IReadOnlyList<SdfMeshDraw>? MeshDraws => m_meshDraws;
    // One more for every new draw list the region packs, which the view's cadence signature follows.
    internal long MeshRevision => m_meshRevision;
    internal bool MeshIndirectInstancesComposable => m_meshIndirectInstancesComposable;

    // Packs new draws, a new revision or changed field participation, growing the region when the list needs more
    // bytes; the upload sends the slot what it owes.
    private void StageMeshRegion(IReadOnlyList<SdfMeshDraw> draws, long revision) {
        if (
            !ReferenceEquals(
                objA: draws,
                objB: m_meshDraws
            ) ||
            (revision != m_meshDrawsRevision) ||
            (m_meshIndirectInstancesComposable != m_liveProgram.IndirectInstancesComposable)
        ) {
            var atlas = StageMeshAtlas(draws: draws);
            var impostors = StageImpostorAtlas(draws: draws);
            var layout = SdfMeshRegion.Plan(
                draws: draws,
                meshes: m_meshPlacementsNext
            );
            // The meshes stand when the region already holds them where this list places them, with the same atlases'
            // texture coordinates: only the draw records, whose matrices follow the bodies, are packed again.
            var meshesStand = (
                ReferenceEquals(objA: m_meshSectionRegion, objB: m_meshRegion) &&
                ReferenceEquals(objA: m_meshSectionAtlas, objB: atlas) &&
                (layout == m_meshLayout) &&
                SamePlacements(left: m_meshPlacementsNext, right: m_meshPlacements)
            );

            (m_meshPlacements, m_meshPlacementsNext) = (m_meshPlacementsNext, m_meshPlacements);

            if (layout.Words > m_meshWords.Length) {
                m_meshWords = new uint[layout.Words];
            }

            var words = m_meshWords.AsSpan(
                length: layout.Words,
                start: 0
            );

            if (layout.Words > 0) {
                SdfMeshRegion.WriteDraws(
                    atlas: atlas,
                    destination: words,
                    draws: draws,
                    impostors: impostors,
                    indirectInstancesComposable: m_liveProgram.IndirectInstancesComposable,
                    layout: layout,
                    meshes: m_meshPlacements
                );
                if (!meshesStand) {
                    SdfMeshRegion.WriteMeshes(
                        atlas: atlas,
                        destination: words,
                        layout: layout,
                        meshes: m_meshPlacements
                    );
                }
            }

            StageMeshMotion(
                draws: draws,
                words: words
            );

            if (layout.Words > 0) {
                EnsureMeshRegionCapacity(bytes: layout.Bytes);
                _ = m_meshRegion.Write(
                    bytes: MemoryMarshal.AsBytes(span: (meshesStand ? words[..layout.VertexWordOffset] : words)),
                    offset: 0
                );
                m_meshSectionRegion = m_meshRegion;
                m_meshSectionAtlas = atlas;
            }

            m_meshDraws = draws;
            m_meshDrawsRevision = revision;
            m_meshIndirectInstancesComposable = m_liveProgram.IndirectInstancesComposable;
            m_meshLayout = layout;
            m_meshRevision++;
        }

        m_meshDrawCount = ((uint)draws.Count);
        StageIndirectMeshes(draws: draws);
    }
    private static bool SamePlacements(Dictionary<SdfMesh, SdfMeshRegionMesh> left, Dictionary<SdfMesh, SdfMeshRegionMesh> right) {
        if (left.Count != right.Count) {
            return false;
        }
        foreach (var (mesh, placement) in left) {
            if (!right.TryGetValue(key: mesh, value: out var other) || (other != placement)) {
                return false;
            }
        }
        return true;
    }
    // Replaces the region with one grown by half again (or to the need, if larger), once the device is idle, since every
    // view's mesh pass reads it. The replacement starts owing every word, so the next write sends the whole packed list,
    // and writes its buffers into the reserved copy sets the old one wrote, so growing takes no descriptor range.
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

        m_deviceContext.TryWaitIdle();

        var replacement = CreateRegion(
            byteCount: checked((int)grown),
            region: MeshRegionIndex
        );

        m_meshRegion.Dispose();
        m_meshRegion = replacement;
        m_seedMeshHistory = true;
        m_bindingRevision++;
        Volatile.Write(
            location: ref m_meshRegionBytes,
            value: grown
        );
    }
}
