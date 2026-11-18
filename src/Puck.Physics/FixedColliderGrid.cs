using System.Runtime.InteropServices;
using Puck.Maths;

namespace Puck.Physics;

/// <summary>A uniform grid over world X and Z of a static collider set's bounds, so a query visits the colliders near
/// its region rather than all of them: the one broad phase every static contact provider narrows its candidates with
/// (<see cref="FixedColliderCandidates"/>). A collider without bounds, or one spanning more than
/// <see cref="CellLimit"/> cells, joins every query. The grid never changes once built and may be read from any
/// number of threads at once; each caller hands <see cref="Candidates"/> its own scratch list.</summary>
public sealed class FixedColliderGrid {
    // Each cell is 4 m of Q48.16 raw units.
    private const int CellShift = 18;
    /// <summary>The most cells a collider or a query region may span before it falls back: a collider joins every
    /// query, a region queries every collider.</summary>
    public const int CellLimit = 64;

    private readonly Dictionary<(int X, int Z), int[]> m_cells;
    private readonly int[] m_unbounded;

    /// <summary>Initializes a new instance of the <see cref="FixedColliderGrid"/> class over a set's bounds.</summary>
    /// <param name="boxes">Each collider's bounds, at its index (<see cref="FixedStaticColliderSet.Boxes"/>).</param>
    public FixedColliderGrid(ReadOnlySpan<FixedAabb> boxes) {
        var cells = new Dictionary<(int X, int Z), List<int>>();
        var unbounded = new List<int>();

        for (var index = 0; (index < boxes.Length); index++) {
            ref readonly var box = ref boxes[index];

            if (box.IsEverywhere) {
                unbounded.Add(item: index);
                continue;
            }
            var range = CellRange(box: in box);

            if (CellCount(range: range) > CellLimit) {
                unbounded.Add(item: index);
                continue;
            }
            for (var x = range.X0; (x <= range.X1); x++) {
                for (var z = range.Z0; (z <= range.Z1); z++) {
                    if (!cells.TryGetValue(key: (x, z), value: out var list)) {
                        list = [];
                        cells.Add(key: (x, z), value: list);
                    }
                    list.Add(item: index);
                }
            }
        }
        m_cells = new Dictionary<(int X, int Z), int[]>(capacity: cells.Count);
        foreach (var (cell, list) in cells) {
            m_cells.Add(key: cell, value: [.. list]);
        }
        m_unbounded = [.. unbounded];
    }

    /// <summary>Lists the colliders whose bounds meet a region grown by the contact skin, ascending and distinct, for a
    /// query whose body stays inside <paramref name="region"/>; the solver checks every collider once the body's
    /// bounds leave it, so the region trades only speed, never the result.</summary>
    /// <param name="boxes">The set's bounds the grid was built over.</param>
    /// <param name="region">The region a query's body stays inside.</param>
    /// <param name="skin">The contact skin a collider acts across.</param>
    /// <param name="scratch">The caller's list the indices are written into; the result reads it until the next call.</param>
    /// <returns>The candidates, or the default value (every collider) when the region spans more than
    /// <see cref="CellLimit"/> cells.</returns>
    public FixedColliderCandidates Candidates(ReadOnlySpan<FixedAabb> boxes, FixedAabb region, FixedQ4816 skin, List<int> scratch) {
        ArgumentNullException.ThrowIfNull(argument: scratch);
        var reach = region.Grow(margin: skin);
        var range = CellRange(box: in reach);

        if (CellCount(range: range) > CellLimit) { return default; }
        scratch.Clear();
        foreach (var index in m_unbounded) {
            if (boxes[index].Intersects(other: in reach)) {
                scratch.Add(item: index);
            }
        }
        for (var x = range.X0; (x <= range.X1); x++) {
            for (var z = range.Z0; (z <= range.Z1); z++) {
                if (!m_cells.TryGetValue(key: (x, z), value: out var cell)) { continue; }
                foreach (var index in cell) {
                    if (boxes[index].Intersects(other: in reach)) {
                        scratch.Add(item: index);
                    }
                }
            }
        }
        scratch.Sort();
        var span = CollectionsMarshal.AsSpan(list: scratch);
        var count = 0;

        for (var read = 0; (read < span.Length); read++) {
            if ((count == 0) || (span[(count - 1)] != span[read])) { span[count++] = span[read]; }
        }
        return new FixedColliderCandidates(indices: span[..count], region: region);
    }

    // The cells a world-axis box covers, inclusive.
    private static (int X0, int X1, int Z0, int Z1) CellRange(in FixedAabb box) =>
        (((int)(box.Minimum.X.Value >> CellShift)), ((int)(box.Maximum.X.Value >> CellShift)),
         ((int)(box.Minimum.Z.Value >> CellShift)), ((int)(box.Maximum.Z.Value >> CellShift)));
    private static long CellCount((int X0, int X1, int Z0, int Z1) range) =>
        (((((long)range.X1) - range.X0) + 1L) * ((((long)range.Z1) - range.Z0) + 1L));
}
