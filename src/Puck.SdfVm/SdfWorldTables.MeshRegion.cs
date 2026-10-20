using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh region: the frame's mesh draws (SdfFrame.MeshDraws) in one GpuRegion, created like every other table
// (SdfWorldTables.Regions.cs) and copied with them in the upload. The region's arena (SdfMeshRegionArena) keeps each mesh
// at the words it was first placed at, so a frame whose draw list is the one last packed, at the revision and program
// composability last packed, packs nothing, and any other packs its draw records and only the meshes the arena newly
// placed: a body that moves rewrites its record's matrix, and a mesh that lands writes its own words, never the meshes
// already placed. Newly placed meshes land within a frame's share of words (MeshLandingWords), in the list's order, and a
// draw whose mesh is still owed waits out of the staged records until it lands, so a body crossing into a world with all
// its meshes at once lands over a few frames. A plan that relocates every mesh writes every word at once. The
// region is created with the tables, one draw record long so every set that binds it binds a buffer, and grows to twice
// the need without waiting for the device, the next upload carrying the words it held on the GPU; a frame without draws
// keeps it. A view's mesh pass reads the draws' triangles from it, and primary a mesh hit's material.
public sealed partial class SdfWorldTables {
    private readonly SdfMeshRegionArena m_meshArena = new();
    // Whether the arena has written its meshes into the region.
    private bool m_meshSectionWritten;

    // The draw list last planned and its revision, and whether its draws are all staged: a list whose meshes' words are
    // still owed stages again each frame as they land.
    private IReadOnlyList<SdfMeshDraw>? m_meshSource;
    private long m_meshSourceRevision;
    private bool m_meshStaged;
    // The placed meshes whose words the region still owes, and the draws of the planned list whose meshes are written.
    private readonly HashSet<SdfMesh> m_meshesOwed = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMeshDraw> m_meshLanded = [];
    // The draws the latest frame staged, whose meshes are written.
    private IReadOnlyList<SdfMeshDraw>? m_meshDraws;

    /// <summary>The words of owed meshes a frame writes into the mesh region at most, past the first mesh it writes: 2 MB,
    /// so a body whose meshes land at once, as when it crosses into a world, lands over a few frames rather than in
    /// one.</summary>
    public const int MeshLandingWords = (1 << 19);

    private readonly GpuRegion m_meshRegion;
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

    // Packs new draws or a new revision, growing the region when the list needs more
    // bytes; the upload sends the slot what it owes.
    private void StageMeshRegion(IReadOnlyList<SdfMeshDraw> draws, long revision) {
        if (
            !ReferenceEquals(
                objA: draws,
                objB: m_meshSource
            ) ||
            (revision != m_meshSourceRevision)
        ) {
            m_meshArena.Plan(draws: draws);

            if (m_meshArena.Words > 0) {
                EnsureMeshRegionCapacity(bytes: (((ulong)m_meshArena.Words) * sizeof(uint)));
                // Every mesh is written again at once when the arena moved them or none was written yet, since no mesh's
                // words stand where its draw reads them; otherwise only the meshes this list placed are owed, and land over
                // the next frames.
                if (m_meshArena.Relocated || !m_meshSectionWritten) {
                    m_meshesOwed.Clear();
                    foreach (var (mesh, placement) in m_meshArena.Placements) { WriteMeshWords(mesh: mesh, placement: placement); }
                } else {
                    m_meshesOwed.UnionWith(other: m_meshArena.Placed);
                }
                m_meshSectionWritten = true;
            }

            m_meshSource = draws;
            m_meshSourceRevision = revision;
            m_meshStaged = false;
        }

        if (m_meshStaged) {
            return;
        }

        WriteOwedMeshes(draws: draws);

        // A draw whose mesh's words are still owed waits for them: the staged list holds the draws whose meshes are
        // written, and the next frames write the rest.
        var staged = draws;

        if ((m_meshesOwed.Count > 0) && draws.Any(predicate: draw => m_meshesOwed.Contains(item: draw.Mesh))) {
            m_meshLanded.Clear();
            foreach (var draw in draws) {
                if (!m_meshesOwed.Contains(item: draw.Mesh)) { m_meshLanded.Add(item: draw); }
            }
            staged = m_meshLanded;
        }

        var drawWords = (staged.Count * SdfMeshRecord.DrawWords);

        if (drawWords > m_meshDrawWords.Length) {
            m_meshDrawWords = new uint[drawWords];
        }

        var records = m_meshDrawWords.AsSpan(length: drawWords, start: 0);

        SdfMeshRegion.WriteDraws(
            destination: records,
            draws: staged,
            meshes: m_meshArena.Placements
        );
        StageMeshMotion(
            draws: staged,
            words: records
        );

        if (m_meshArena.Words > 0) {
            _ = m_meshRegion.Write(bytes: MemoryMarshal.AsBytes(span: records), offset: 0);
        }

        m_meshDraws = staged;
        m_meshDrawCount = ((uint)staged.Count);
        m_meshRevision++;
        m_meshStaged = ReferenceEquals(objA: staged, objB: draws);
    }
    // Writes the owed meshes the draw list names, in its order, until the frame's share of words is spent; a frame writes
    // at least one. A placed mesh no draw names stays owed until a list names it again.
    private void WriteOwedMeshes(IReadOnlyList<SdfMeshDraw> draws) {
        var written = 0L;

        foreach (var draw in draws) {
            if (written >= MeshLandingWords) { return; }
            if (m_meshesOwed.Remove(item: draw.Mesh)) {
                WriteMeshWords(mesh: draw.Mesh, placement: m_meshArena.Placements[draw.Mesh]);
                written += SdfMeshRegion.WordsOf(mesh: draw.Mesh);
            }
        }
    }
    // Writes one placed mesh's words into the region.
    private void WriteMeshWords(SdfMesh mesh, SdfMeshRegionMesh placement) {
        var words = SdfMeshRegion.WordsOf(mesh: mesh);

        if (words > m_meshWords.Length) {
            m_meshWords = new uint[Math.Max(val1: words, val2: (2 * m_meshWords.Length))];
        }

        var destination = m_meshWords.AsSpan(length: words, start: 0);

        SdfMeshRegion.WriteMesh(
            destination: destination,
            mesh: mesh,
            placement: new SdfMeshRegionMesh(
                IndexCount: placement.IndexCount,
                IndexWord: (placement.IndexWord - placement.VertexWord),
                MaterialWord: (placement.MaterialWord - placement.VertexWord),
                VertexWord: 0,
                SkinWord: ((placement.SkinWord == 0) ? 0 : (placement.SkinWord - placement.VertexWord))
            )
        );
        _ = m_meshRegion.Write(bytes: MemoryMarshal.AsBytes(span: destination), offset: (placement.VertexWord * sizeof(uint)));
    }
    // Grows the region to twice the need without waiting for the device (GpuRegion.Grow): the next upload clears the new
    // buffer and copies the words the old one held into it on the GPU, so every placed mesh stands where it was written
    // and nothing it held is staged again, and the old buffer is disposed once that upload completes (Retire). The
    // region keeps its reserved copy sets, so growing takes no descriptor range.
    private void EnsureMeshRegionCapacity(ulong bytes) {
        var current = ((ulong)m_meshRegion.ByteCount);

        if (bytes <= current) {
            return;
        }

        var grown = (2UL * bytes);

        grown = (((grown + (sizeof(uint) - 1UL)) / sizeof(uint)) * sizeof(uint));

        Retire(replaced: m_meshRegion.Grow(byteCount: checked((int)grown)));
        m_seedMeshHistory = true;
        m_bindingRevision++;
        Volatile.Write(
            location: ref m_meshRegionBytes,
            value: grown
        );
    }
}
