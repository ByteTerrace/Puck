using System.Numerics;
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
    // A copy of each draw last staged, by index, and whether that staging staged its whole list (no draw waiting on an
    // owed mesh): what StageChangedDraws compares a list against.
    private SdfMeshDraw[] m_stagedDraws = [];
    private int m_stagedCount;
    private bool m_stagedWhole;

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

    // The draw records as the next write sends them, and one mesh's words as a flush stages them.
    private uint[] m_meshDrawWords = [];
    private uint[] m_meshWords = [];
    // The region's source for its mesh section, and the placed meshes by their first word, sorted again after a plan
    // places or forgets one.
    private MeshSection? m_meshSection;
    private readonly List<(int Word, SdfMesh Mesh)> m_meshOrder = [];
    private bool m_meshOrderStale = true;
    // The zeros a flush stages into words no placed mesh holds.
    private static readonly uint[] ZeroWords = new uint[1 << 16];

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
            m_meshOrderStale |= !m_meshArena.Unchanged;
            Volatile.Write(
                location: ref m_meshRegionUsedBytes,
                value: (((ulong)m_meshArena.Words) * sizeof(uint))
            );

            if (m_meshArena.Words > 0) {
                if (m_meshArena.Relocated) {
                    FitMeshRegion(bytes: (((ulong)m_meshArena.Words) * sizeof(uint)));
                }
                EnsureMeshRegionCapacity(bytes: (((ulong)m_meshArena.Words) * sizeof(uint)));
                ShadowMeshRecords();
                // Every mesh is written again at once when the arena moved them or none was written yet, since no mesh's
                // words stand where its draw reads them. Otherwise the meshes a grown draw reserve moved are written now,
                // since their draws were drawn from the words the reserve took, and the rest this list placed are owed and
                // land over the next frames.
                if (m_meshArena.Relocated || !m_meshSectionWritten) {
                    m_meshesOwed.Clear();
                    foreach (var (mesh, placement) in m_meshArena.Placements) { WriteMeshWords(mesh: mesh, placement: placement); }
                } else {
                    m_meshesOwed.UnionWith(other: m_meshArena.Placed);
                    foreach (var mesh in m_meshArena.Moved) {
                        _ = m_meshesOwed.Remove(item: mesh);
                        WriteMeshWords(mesh: mesh, placement: m_meshArena.Placements[mesh]);
                    }
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

        // A list naming the meshes the last one named, in order, after a staging that staged that whole list, owes only
        // the draws that changed: every other record, box and motion row stands as it was staged.
        if (
            m_meshArena.Unchanged &&
            m_stagedWhole &&
            ReferenceEquals(objA: staged, objB: draws) &&
            (staged.Count == m_stagedCount) &&
            (m_meshArena.Words > 0)
        ) {
            StageChangedDraws(draws: staged);
            m_meshDraws = staged;
            m_meshRevision++;
            m_meshStaged = true;

            return;
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
        if (m_stagedDraws.Length < staged.Count) {
            m_stagedDraws = new SdfMeshDraw[Math.Max(val1: staged.Count, val2: (2 * m_stagedDraws.Length))];
        }
        for (var index = 0; (index < staged.Count); index++) {
            m_stagedDraws[index] = staged[index];
        }
        Array.Clear(array: m_stagedDraws, index: staged.Count, length: (m_stagedCount - Math.Min(val1: m_stagedCount, val2: staged.Count)));
        m_stagedCount = staged.Count;
        m_meshRevision++;
        m_meshStaged = ReferenceEquals(objA: staged, objB: draws);
        m_stagedWhole = m_meshStaged;
    }
    // Stages the draws of a list that changed since the draws last staged at their indices, which the list matches in
    // count and meshes: each changed draw's record, box and motion row, and the region's words for each run of them.
    private void StageChangedDraws(IReadOnlyList<SdfMeshDraw> draws) {
        var records = m_meshDrawWords.AsSpan(length: (draws.Count * SdfMeshRecord.DrawWords), start: 0);
        // The records as the region last held them, which a moved matrix is compared against before it is written.
        var old = m_meshRegion.ContentsAt(
            length: ((int)Math.Min(val1: m_meshRegion.ByteCount, val2: (((long)draws.Count) * SdfMeshRegion.DrawBytes))),
            offset: 0
        );
        var run = -1;

        for (var index = 0; (index <= draws.Count); index++) {
            if ((index < draws.Count) && (draws[index] is var draw) && !SameDraw(left: in draw, right: in m_stagedDraws[index])) {
                if (draw.Identity is not { } identity) {
                    throw new ArgumentException(message: $"Mesh draw {index} names no identity.", paramName: nameof(draws));
                }

                var record = records.Slice(length: SdfMeshRecord.DrawWords, start: (index * SdfMeshRecord.DrawWords));
                var offset = (index * SdfMeshRegion.DrawBytes);

                SdfMeshRegion.WriteDraw(draw: in draw, placement: m_meshArena.Placements[draw.Mesh], record: record);
                if (!identity.Equals(obj: m_meshIdentities[index])) {
                    m_seededMeshDraws.Add(length: 1, start: index);
                } else if ((offset > (old.Length - MeshMatrixBytes)) || !MemoryMarshal.AsBytes(span: record[..16]).SequenceEqual(other: old.Slice(length: MeshMatrixBytes, start: offset))) {
                    m_changedMeshMatrices.Add(length: 1, start: index);
                }
                m_meshIdentities[index] = identity;
                if (draw.Glass != m_stagedDraws[index].Glass) {
                    m_glassDrawCount = (draw.Glass ? (m_glassDrawCount + 1) : (m_glassDrawCount - 1));
                }
                m_meshDrawBoxes[index] = (SdfMeshBox.TryOf(box: out var box, draw: in draw) ? box : null);
                m_stagedDraws[index] = draw;
                if (run < 0) { run = index; }

                continue;
            }
            if (run >= 0) {
                _ = m_meshRegion.Write(
                    bytes: MemoryMarshal.AsBytes(span: records.Slice(length: ((index - run) * SdfMeshRecord.DrawWords), start: (run * SdfMeshRecord.DrawWords))),
                    offset: (((long)run) * SdfMeshRegion.DrawBytes)
                );
                run = -1;
            }
        }
    }
    // Whether two draws stage the same record, box and motion: the same mesh, identity, material and flags, and the same
    // bits in every float the record carries.
    private static bool SameDraw(in SdfMeshDraw left, in SdfMeshDraw right) {
        if (
            !ReferenceEquals(objA: left.Mesh, objB: right.Mesh) ||
            (left.Material != right.Material) ||
            (left.Glass != right.Glass) ||
            (left.IsDynamic != right.IsDynamic) ||
            (left.FrameSlot != right.FrameSlot) ||
            (BitConverter.SingleToInt32Bits(value: left.CarryScale) != BitConverter.SingleToInt32Bits(value: right.CarryScale)) ||
            !Equals(objA: left.Identity, objB: right.Identity)
        ) {
            return false;
        }

        var leftMatrix = left.ObjectToWorld;
        var rightMatrix = right.ObjectToWorld;
        var leftRotation = left.CarryRotation;
        var rightRotation = right.CarryRotation;
        var leftShift = left.CarryTranslation;
        var rightShift = right.CarryTranslation;
        var leftClip = left.ClipPlane;
        var rightClip = right.ClipPlane;

        return (
            MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(in leftMatrix)).SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(in rightMatrix))) &&
            MemoryMarshal.AsBytes(span: new ReadOnlySpan<Quaternion>(in leftRotation)).SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Quaternion>(in rightRotation))) &&
            MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector3>(in leftShift)).SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector3>(in rightShift))) &&
            MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector4>(in leftClip)).SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector4>(in rightClip)))
        );
    }
    // Writes the owed meshes the draw list names, in its order, until the frame's share of words is spent; a frame writes
    // at least one. A placed mesh no draw names stays owed until a list names it again.
    private void WriteOwedMeshes(IReadOnlyList<SdfMeshDraw> draws) {
        var written = 0L;

        for (var index = 0; ((index < draws.Count) && (m_meshesOwed.Count > 0)); index++) {
            var draw = draws[index];

            if (written >= MeshLandingWords) { return; }
            if (m_meshesOwed.Remove(item: draw.Mesh)) {
                WriteMeshWords(mesh: draw.Mesh, placement: m_meshArena.Placements[draw.Mesh]);
                written += SdfMeshRegion.WordsOf(mesh: draw.Mesh);
            }
        }
    }
    // Owes one placed mesh's words: the region keeps no copy of the mesh section, and the upload's flush stages the words
    // from the mesh itself (WriteSourcedMeshWords). The byte offset is a long: a mesh placed past 512 M words starts past
    // 2 GiB.
    private void WriteMeshWords(SdfMesh mesh, SdfMeshRegionMesh placement) =>
        m_meshRegion.OweSourced(
            length: (((long)SdfMeshRegion.WordsOf(mesh: mesh)) * sizeof(uint)),
            offset: (((long)placement.VertexWord) * sizeof(uint))
        );
    // One placed mesh's words, from its first vertex word, in the mesh scratch.
    private Span<uint> MeshWordsOf(SdfMesh mesh, SdfMeshRegionMesh placement) {
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

        return destination;
    }
    // Hands the mesh region's words past the draw reserve to the meshes the arena places there, once the region holds the
    // planned words: the host copy keeps the draw records alone.
    private void ShadowMeshRecords() =>
        m_meshRegion.Shadow(
            shadowBytes: Math.Clamp(
                max: m_meshRegion.ByteCount,
                min: SdfMeshRegion.DrawBytes,
                value: (((long)m_meshArena.DrawCapacity) * SdfMeshRegion.DrawBytes)
            ),
            source: (m_meshSection ??= new MeshSection(tables: this))
        );
    // Writes the mesh region's words [word, word + count), past the draw reserve, into a staging buffer: each placed mesh's
    // words where the arena placed it, and zeros in the words no placed mesh holds, which no draw reads.
    private void WriteSourcedMeshWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) {
        if (m_meshOrderStale) {
            m_meshOrder.Clear();
            foreach (var (mesh, placement) in m_meshArena.Placements) { m_meshOrder.Add(item: (placement.VertexWord, mesh)); }
            m_meshOrder.Sort(comparison: static (left, right) => left.Word.CompareTo(value: right.Word));
            m_meshOrderStale = false;
        }

        var end = (((long)word) + count);
        var cursor = (long)word;
        var low = 0;
        var high = m_meshOrder.Count;

        // The first placed mesh that ends past word.
        while (low < high) {
            var middle = ((low + high) >>> 1);
            var (start, mesh) = m_meshOrder[middle];

            if ((((long)start) + SdfMeshRegion.WordsOf(mesh: mesh)) <= word) {
                low = (middle + 1);
            } else {
                high = middle;
            }
        }
        for (var index = low; (index < m_meshOrder.Count) && (m_meshOrder[index].Word < end); index++) {
            var (start, mesh) = m_meshOrder[index];
            var words = MeshWordsOf(mesh: mesh, placement: m_meshArena.Placements[mesh]);
            var first = Math.Max(val1: cursor, val2: start);
            var last = Math.Min(val1: end, val2: (((long)start) + words.Length));

            WriteZeroWords(buffer: buffer, count: (first - cursor), destination: (destinationOffsetBytes + ((ulong)((cursor - word) * sizeof(uint)))));
            buffer.Write<uint>(
                data: words.Slice(length: ((int)(last - first)), start: ((int)(first - start))),
                destinationOffsetBytes: (destinationOffsetBytes + ((ulong)((first - word) * sizeof(uint))))
            );
            cursor = last;
        }
        WriteZeroWords(buffer: buffer, count: (end - cursor), destination: (destinationOffsetBytes + ((ulong)((cursor - word) * sizeof(uint)))));
    }
    private static void WriteZeroWords(IGpuStorageBuffer buffer, long count, ulong destination) {
        while (count > 0) {
            var piece = ((int)Math.Min(val1: count, val2: ZeroWords.Length));

            buffer.Write<uint>(data: ZeroWords.AsSpan(length: piece, start: 0), destinationOffsetBytes: destination);
            destination += (((ulong)piece) * sizeof(uint));
            count -= piece;
        }
    }
    // The mesh section's words as the region's source sees them.
    private sealed class MeshSection(SdfWorldTables tables) : IGpuRegionSource {
        public void WriteWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) =>
            tables.WriteSourcedMeshWords(buffer: buffer, count: count, destinationOffsetBytes: destinationOffsetBytes, word: word);
    }
    // Grows the region to MeshRegionGrownBytes of the need, refusing by name past what a region holds, without waiting for
    // the device (GpuRegion.Grow): the next upload clears the new buffer and copies the words the old one held into it on
    // the GPU, so every placed mesh stands where it was written and nothing it held is staged again, and the old buffer is
    // disposed once that upload completes (Retire). The region keeps its reserved copy sets, so growing takes no
    // descriptor range.
    // Shrinks the region to MeshRegionGrownBytes of the need when a relocation leaves it holding more than half as much
    // again, so the meshes no list names any more give back their device-local, staging and host-copy bytes. A
    // relocation writes every word anyway, so the shrunk region, which owes every word, costs that upload nothing more.
    private void FitMeshRegion(ulong bytes) {
        var fitted = MeshRegionGrownBytes(needBytes: bytes);

        if (((ulong)m_meshRegion.ByteCount) <= (fitted + (fitted / 2UL))) {
            return;
        }

        Retire(replaced: m_meshRegion.Shrink(byteCount: ((long)fitted)));
        m_seedMeshHistory = true;
        m_bindingRevision++;
        Volatile.Write(
            location: ref m_meshRegionBytes,
            value: fitted
        );
    }
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
