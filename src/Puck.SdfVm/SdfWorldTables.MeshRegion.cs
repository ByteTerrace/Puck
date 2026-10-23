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
// region is created with the tables, one draw record long so every set that binds it binds a buffer, and grows to the need
// plus an eighth without waiting for the device, the next upload carrying the words it held on the GPU; a frame without draws
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
    private ulong m_meshRegionUsedBytes;
    // The draws the frame being rendered rasterizes, from its staged draw list, and how many of them are glass.
    private uint m_meshDrawCount;
    private uint m_glassDrawCount;
    private SdfMeshBox?[] m_meshDrawBoxes = [];

    // The draw records, and one mesh's words, as the next write sends them.
    private uint[] m_meshDrawWords = [];
    private uint[] m_meshWords = [];

    /// <summary>Gets the words the mesh region's draw records and meshes take for the draws the latest frame staged; zero
    /// before a frame draws a mesh.</summary>
    public int MeshRegionWords => m_meshArena.Words;
    /// <summary>The most bytes one mesh region holds, a whole number of words (<see cref="GpuRegion.MaxByteCount"/>): the
    /// baked meshes a frame draws must fit it, and a frame that needs more throws.</summary>
    public const ulong MostMeshRegionBytes = ((ulong)GpuRegion.MaxByteCount);
    /// <summary>The unit a grown mesh region's size is rounded up to: 16 MiB.</summary>
    public const ulong MeshRegionGranuleBytes = (16UL << 20);
    /// <summary>Gets the bytes the mesh region holds, which its buffers are allocated for: one draw record's until a frame
    /// needs more. Safe to read from any thread.</summary>
    public ulong MeshRegionBytes => Volatile.Read(location: ref m_meshRegionBytes);
    /// <summary>Gets the bytes of the mesh region the latest planned draw list uses: its draw records and the meshes it
    /// places, which <see cref="MeshRegionBytes"/> holds with room to grow. Safe to read from any thread.</summary>
    public ulong MeshRegionUsedBytes => Volatile.Read(location: ref m_meshRegionUsedBytes);

    /// <summary>Returns the size a mesh region grows to when its meshes need <paramref name="needBytes"/>: the need plus an
    /// eighth, rounded up to <see cref="MeshRegionGranuleBytes"/> and capped at <see cref="MostMeshRegionBytes"/>. The size
    /// is never below the need and is a whole number of words.</summary>
    /// <param name="needBytes">The bytes the planned draws and meshes use; at most <see cref="MostMeshRegionBytes"/>.</param>
    /// <returns>The region's new size in bytes.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="needBytes"/> is past <see cref="MostMeshRegionBytes"/>.</exception>
    public static ulong MeshRegionGrownBytes(ulong needBytes) {
        if (needBytes > MostMeshRegionBytes) {
            throw new InvalidOperationException(message: $"The baked meshes need {needBytes} bytes of mesh region, past the {MostMeshRegionBytes} one region holds.");
        }

        var wanted = (needBytes + (needBytes / 8UL));

        return Math.Min(
            val1: (((wanted + (MeshRegionGranuleBytes - 1UL)) / MeshRegionGranuleBytes) * MeshRegionGranuleBytes),
            val2: MostMeshRegionBytes
        );
    }
    /// <summary>Gets the draws the latest frame's mesh passes rasterize in each view, the glass draws among them.</summary>
    public uint MeshDrawCount => m_meshDrawCount;
    /// <summary>Gets how many of <see cref="MeshDrawCount"/> are glass (<see cref="SdfMeshDraw.Glass"/>): the glass pass
    /// draws them, and the mesh pass and the shadow cascades draw the rest.</summary>
    public uint GlassDrawCount => m_glassDrawCount;
    /// <summary>Gets the opaque draws the latest frame's mesh pass and shadow cascades rasterize.</summary>
    public uint OpaqueDrawCount => (m_meshDrawCount - m_glassDrawCount);

    // The draw list the latest frame staged, whose draws a view's mesh pass records one call each.
    internal IReadOnlyList<SdfMeshDraw>? MeshDraws => m_meshDraws;
    // The world box of each staged draw, by its index in MeshDraws, or null for a draw no host box bounds
    // (SdfMeshBox.TryOf), which every pass draws.
    internal ReadOnlySpan<SdfMeshBox?> MeshDrawBoxes => m_meshDrawBoxes.AsSpan(length: ((int)m_meshDrawCount), start: 0);

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
            Volatile.Write(
                location: ref m_meshRegionUsedBytes,
                value: (((ulong)m_meshArena.Words) * sizeof(uint))
            );

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
        m_glassDrawCount = 0;
        if (m_meshDrawBoxes.Length < staged.Count) {
            m_meshDrawBoxes = new SdfMeshBox?[Math.Max(val1: staged.Count, val2: (2 * m_meshDrawBoxes.Length))];
        }
        for (var index = 0; (index < staged.Count); index++) {
            var draw = staged[index];

            if (draw.Glass) { m_glassDrawCount++; }
            m_meshDrawBoxes[index] = (SdfMeshBox.TryOf(box: out var box, draw: in draw) ? box : null);
        }
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
        // The byte offset is a long: a mesh placed past 512 M words starts past 2 GiB.
        _ = m_meshRegion.Write(bytes: MemoryMarshal.AsBytes(span: destination), offset: (((long)placement.VertexWord) * sizeof(uint)));
    }
    // Grows the region to MeshRegionGrownBytes of the need, refusing by name past what a region holds, without waiting for
    // the device (GpuRegion.Grow): the next upload clears the new buffer and copies the words the old one held into it on
    // the GPU, so every placed mesh stands where it was written and nothing it held is staged again, and the old buffer is
    // disposed once that upload completes (Retire). The region keeps its reserved copy sets, so growing takes no
    // descriptor range.
    private void EnsureMeshRegionCapacity(ulong bytes) {
        var current = ((ulong)m_meshRegion.ByteCount);

        if (bytes <= current) {
            return;
        }

        var grown = MeshRegionGrownBytes(needBytes: bytes);

        Retire(replaced: m_meshRegion.Grow(byteCount: ((long)grown)));
        m_seedMeshHistory = true;
        m_bindingRevision++;
        Volatile.Write(
            location: ref m_meshRegionBytes,
            value: grown
        );
    }
}
