using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Puck.SdfVm;

// What the raster passes selected of the staged draws, read back from their GPU selection (SdfMeshOcclusion): every
// SelectionMarkFrames stages of a pass its kernel marks each draw a phase emitted a command for with the level it draws,
// and the marks land here once the node proves the slot complete. They give the triangles each pass draws by level and
// by the tier its mesh was baked at, and the placed mesh bytes no pass selected within a window. A mark names a row of
// the list it was staged under: one whose list's rows have changed since (MeshRowEpoch) names nothing and is counted
// stale, never read as unselected.
public sealed partial class SdfWorldTables {
    /// <summary>The stages of a raster pass between two that mark the draws they select: one in this many.</summary>
    public const int SelectionMarkFrames = 8;
    /// <summary>The uploads a mesh stays selected for after a pass last marked a draw naming it: a pass held at a lower
    /// rate marks less often, and a mesh a slow cascade still draws is not counted unselected between its marks.</summary>
    public const int SelectionWindowUploads = (16 * SelectionMarkFrames);

    // One more each time a staging changes which mesh or occupant a row holds, or how many rows the list has.
    private long m_meshRowEpoch;
    // The upload at which a pass last marked each row selected; zero for none since the row last changed occupant.
    private ulong[] m_rowSelectedAt = [];
    private long m_marksRead;
    private long m_marksStale;
    private ulong m_unselectedCountedAt;
    private long m_unselectedBytes;
    private int m_unselectedMeshes;
    private long m_selectedBytes;
    // Each raster selection's latest read marks, by the occlusion that read them.
    private readonly ConcurrentDictionary<object, SelectionSummary> m_selections = new(comparer: ReferenceEqualityComparer.Instance);

    private sealed record SelectionSummary(string Part, long[] Levels, long[] Tiers, long Draws);

    /// <summary>Gets the epoch of the staged list's rows: it moves whenever a staging changes the mesh or occupant a row
    /// holds or how many rows the list has, never for a moved matrix. Read on the frame thread.</summary>
    public long MeshRowEpoch => m_meshRowEpoch;

    // A row's mesh or occupant changed: marks staged before name another surface there.
    private void RowChanged(int row) {
        m_meshRowEpoch++;
        if (row < m_rowSelectedAt.Length) { m_rowSelectedAt[row] = 0UL; }
    }

    /// <summary>Reads one raster pass's selection marks, on the frame thread once its submission completed: each draw's two
    /// bits hold the level it drew plus one, or zero where no phase drew it. Marks staged under another row epoch are
    /// counted stale and read nothing.</summary>
    /// <param name="source">The selection that read the marks, which <see cref="ForgetSelectionMarks"/> forgets.</param>
    /// <param name="epoch">The row epoch the marks were staged under (<see cref="MeshRowEpoch"/>).</param>
    /// <param name="draws">The draws the pass selected among.</param>
    /// <param name="marks">The mark words, sixteen draws a word, the lowest draw in the lowest bits.</param>
    internal void ObserveSelectionMarks(SdfMeshOcclusion source, long epoch, uint draws, ReadOnlySpan<uint> marks) {
        if (m_disposed) { return; }
        if ((epoch != m_meshRowEpoch) || (draws != ((uint)m_stagedCount))) {
            m_marksStale++;
            return;
        }
        m_marksRead++;
        if (m_rowSelectedAt.Length < m_stagedDraws.Length) { Array.Resize(array: ref m_rowSelectedAt, newSize: m_stagedDraws.Length); }

        var levels = new long[3];
        var tiers = new long[(SdfMeshTier.Last + 2)];
        var selected = 0L;
        var stamp = (m_uploads + 1UL);

        for (var word = 0; (word < marks.Length); word++) {
            var bits = marks[word];

            for (var draw = (word * 16); ((bits != 0u) && (draw < draws)); draw++, bits >>= 2) {
                var level = ((int)(bits & 3u) - 1);

                if ((level < 0) || (m_stagedDraws[draw].Mesh is not { } mesh)) { continue; }

                var triangles = ((level == 0) ? mesh.TriangleCount : ((level <= mesh.Levels.Count) ? mesh.Levels[(level - 1)].TriangleCount : 0));

                levels[level] += triangles;
                tiers[Math.Clamp(value: (m_stagedDraws[draw].Tier + 1), min: 0, max: (tiers.Length - 1))] += triangles;
                m_rowSelectedAt[draw] = stamp;
                selected++;
            }
        }
        m_selections[source] = new SelectionSummary(Draws: selected, Levels: levels, Part: source.Part, Tiers: tiers);
        CountUnselected();
    }
    /// <summary>Forgets a raster selection's marks, for one that is disposed or now selects another residency's
    /// draws.</summary>
    /// <param name="source">The selection.</param>
    internal void ForgetSelectionMarks(SdfMeshOcclusion source) => m_selections.TryRemove(key: source, value: out _);

    // Sums the placed meshes no row naming them was marked selected within the window, once a mark round: a mesh no
    // draw names counts too, since it holds its words for nothing a pass reads.
    private void CountUnselected() {
        if ((m_uploads - m_unselectedCountedAt) < SelectionMarkFrames) { return; }
        m_unselectedCountedAt = m_uploads;

        var since = ((m_uploads > SelectionWindowUploads) ? (m_uploads - SelectionWindowUploads) : 0UL);
        var unselected = 0L;
        var meshes = 0;
        var selected = 0L;

        foreach (var mesh in m_meshArena.Placements.Keys) {
            var bytes = (((long)SdfMeshRegion.WordsOf(mesh: mesh)) * sizeof(uint));
            var used = false;

            for (var row = m_rowsByMesh.First(key: mesh); ((row >= 0) && !used); row = m_rowsByMesh.Next(row: row)) {
                used = ((row < m_rowSelectedAt.Length) && (m_rowSelectedAt[row] > since));
            }
            if (used) {
                selected += bytes;
            } else {
                unselected += bytes;
                meshes++;
            }
        }
        Volatile.Write(location: ref m_unselectedBytes, value: unselected);
        Volatile.Write(location: ref m_unselectedMeshes, value: meshes);
        Volatile.Write(location: ref m_selectedBytes, value: selected);
    }

    /// <summary>Describes what the raster passes selected, from their latest marks: per pass part, the triangles drawn at
    /// each level and at each tier the meshes were baked at (<c>none</c> for a mesh no bake tiered), the mesh bytes
    /// placed in the region that some pass selected and those none selected within
    /// <see cref="SelectionWindowUploads"/> uploads, and the mark rounds read and those stale. Safe to read from any
    /// thread.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeSelection() {
        var line = new StringBuilder();
        var parts = new SortedDictionary<string, (long[] Levels, long[] Tiers, long Draws)>(comparer: StringComparer.Ordinal);

        foreach (var summary in m_selections.Values) {
            if (!parts.TryGetValue(key: summary.Part, value: out var sum)) { sum = (new long[3], new long[summary.Tiers.Length], 0L); }
            for (var level = 0; (level < 3); level++) { sum.Levels[level] += summary.Levels[level]; }
            for (var tier = 0; (tier < summary.Tiers.Length); tier++) { sum.Tiers[tier] += summary.Tiers[tier]; }
            parts[summary.Part] = (sum.Levels, sum.Tiers, (sum.Draws + summary.Draws));
        }
        _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $"selection-marks={Volatile.Read(location: ref m_marksRead)} marks-stale={Volatile.Read(location: ref m_marksStale)} selected-bytes={Volatile.Read(location: ref m_selectedBytes)}B unselected-bytes={Volatile.Read(location: ref m_unselectedBytes)}B unselected-meshes={Volatile.Read(location: ref m_unselectedMeshes)}");
        foreach (var (part, (levels, tiers, draws)) in parts) {
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" drawn.{part}.draws={draws} drawn.{part}.level0={levels[0]}tri drawn.{part}.level1={levels[1]}tri drawn.{part}.level2={levels[2]}tri");
            for (var tier = 0; (tier < tiers.Length); tier++) {
                if (tiers[tier] == 0) { continue; }
                _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" drawn.{part}.tier{((tier == 0) ? "-none" : (tier - 1).ToString(provider: CultureInfo.InvariantCulture))}={tiers[tier]}tri");
            }
        }

        return line.ToString();
    }
}
