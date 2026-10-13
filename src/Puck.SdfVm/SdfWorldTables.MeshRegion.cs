using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh region: the frame's mesh draws (SdfFrame.MeshDraws) in one GpuRegion, created like every other table
// (SdfWorldTables.Regions.cs) and copied with them in the upload. The region's arena (SdfMeshRegionArena) keeps each mesh
// at the words it was first placed at, so a frame whose draw list is the one last packed, at the revision and program
// composability last packed, packs nothing, and any other packs its draw records and only the meshes the arena newly
// placed: a body that moves rewrites its record's matrix, and a mesh that lands writes its own words, never the meshes
// already placed. A plan that relocates every mesh, the mesh atlases changing (a textured mesh's texture coordinates are
// written moved into them, SdfWorldTables.MeshAtlas.cs), or a grown region writes every word. The region is created with
// the tables, one draw record long so every set that binds it binds a buffer, and grows to twice the need, once the device
// is idle, like program capacity; a frame without draws keeps it. A view's mesh pass reads the draws' triangles from it,
// and primary a mesh hit's material.
public sealed partial class SdfWorldTables {
    private readonly SdfMeshRegionArena m_meshArena = new();
    // The region and atlases the arena's meshes were last written into and for.
    private GpuRegion? m_meshSectionRegion;
    private SdfMeshAtlas? m_meshSectionAtlas;

    // The draw list last packed.
    private IReadOnlyList<SdfMeshDraw>? m_meshDraws;
    private long m_meshDrawsRevision;

    private bool m_meshIndirectInstancesComposable = true;

    private GpuRegion m_meshRegion;
    // One more for every new draw list the region packs: the cadence signature folds it, so a frame whose draws moved
    // renders.
    private long m_meshRevision;
    private ulong m_meshRegionBytes;
    // The draws the frame being rendered rasterizes, from its staged draw list.
    private uint m_meshDrawCount;

    // The draw records, and one mesh's words, as the next write sends them.
    private uint[] m_meshDrawWords = [];
    private uint[] m_meshWords = [];

    /// <summary>Gets the words the mesh region's draw records and meshes take for the draws the latest frame staged; zero
    /// before a frame draws a mesh.</summary>
    public int MeshRegionWords => m_meshArena.Words;
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

            m_meshArena.Plan(draws: draws);

            var drawWords = (draws.Count * SdfMeshRegion.DrawWords);

            if (drawWords > m_meshDrawWords.Length) {
                m_meshDrawWords = new uint[drawWords];
            }

            var records = m_meshDrawWords.AsSpan(length: drawWords, start: 0);

            SdfMeshRegion.WriteDraws(
                atlas: atlas,
                destination: records,
                draws: draws,
                impostors: impostors,
                indirectInstancesComposable: m_liveProgram.IndirectInstancesComposable,
                meshes: m_meshArena.Placements
            );
            StageMeshMotion(
                draws: draws,
                words: records
            );

            if (m_meshArena.Words > 0) {
                var grown = EnsureMeshRegionCapacity(bytes: (((ulong)m_meshArena.Words) * sizeof(uint)));
                // Every mesh is written again when the arena moved them, the region is new, or the atlases their
                // texture coordinates are moved into changed; otherwise only the meshes this list placed.
                var all = (grown || m_meshArena.Relocated || !ReferenceEquals(objA: m_meshSectionRegion, objB: m_meshRegion) || !ReferenceEquals(objA: m_meshSectionAtlas, objB: atlas));

                _ = m_meshRegion.Write(bytes: MemoryMarshal.AsBytes(span: records), offset: 0);
                if (all) {
                    foreach (var (mesh, placement) in m_meshArena.Placements) { WriteMeshWords(atlas: atlas, mesh: mesh, placement: placement); }
                } else {
                    foreach (var mesh in m_meshArena.Placed) { WriteMeshWords(atlas: atlas, mesh: mesh, placement: m_meshArena.Placements[mesh]); }
                }
                m_meshSectionRegion = m_meshRegion;
                m_meshSectionAtlas = atlas;
            }

            m_meshDraws = draws;
            m_meshDrawsRevision = revision;
            m_meshIndirectInstancesComposable = m_liveProgram.IndirectInstancesComposable;
            m_meshRevision++;
        }

        m_meshDrawCount = ((uint)draws.Count);
        StageIndirectMeshes(draws: draws);
    }
    // Writes one placed mesh's words into the region.
    private void WriteMeshWords(SdfMesh mesh, SdfMeshRegionMesh placement, SdfMeshAtlas? atlas) {
        var words = SdfMeshRegion.WordsOf(mesh: mesh);

        if (words > m_meshWords.Length) {
            m_meshWords = new uint[Math.Max(val1: words, val2: (2 * m_meshWords.Length))];
        }

        var destination = m_meshWords.AsSpan(length: words, start: 0);

        SdfMeshRegion.WriteMesh(
            atlas: atlas,
            destination: destination,
            mesh: mesh,
            placement: new SdfMeshRegionMesh(
                IndexCount: placement.IndexCount,
                IndexWord: (placement.IndexWord - placement.VertexWord),
                MaterialWord: (placement.MaterialWord - placement.VertexWord),
                VertexWord: 0
            )
        );
        _ = m_meshRegion.Write(bytes: MemoryMarshal.AsBytes(span: destination), offset: (placement.VertexWord * sizeof(uint)));
    }
    // Replaces the region with one twice the need, once the device is idle, since every view's mesh pass reads it, and
    // returns whether it did. The replacement starts owing every word and writes its buffers into the reserved copy sets
    // the old one wrote, so growing takes no descriptor range.
    private bool EnsureMeshRegionCapacity(ulong bytes) {
        var current = ((ulong)m_meshRegion.ByteCount);

        if (bytes <= current) {
            return false;
        }

        var grown = (2UL * bytes);

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

        return true;
    }
}
