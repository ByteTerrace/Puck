using Puck.Maths;

namespace Puck.Post;

/// <summary>
/// Tier-A stage A2. Checks that <see cref="WorldCoord3"/>'s cell carry (<see cref="WorldCoord3.Normalize"/>),
/// cell-aware difference (<see cref="WorldCoord3.Delta"/>), and translating add (<c>operator +</c>) are CORRECT —
/// including the cross-cell paths a single-cell scene never exercises — each compared against an absolute fixed-point
/// reference (<c>cell·CellSize + local</c>), plus the centred-offset invariant and far-cell translation invariance.
/// </summary>
internal sealed class WorldCoord3Stage : IPostStage {
    private const int CellRawLog2 = (WorldCoord3.CellSizeLog2 + FixedQ4816.FractionBitCount);
    private const long CellRaw = (1L << CellRawLog2);
    private const long HalfCellRaw = (1L << (CellRawLog2 - 1));

    /// <inheritdoc/>
    public string Name => "worldcoord3";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        // The type stores X/Y/Z cells independently, so proving one axis leaves the other two unproven — the original
        // stage only exercised X. Run the whole carry/delta/operator+/far-cell battery on each axis.
        foreach (var axis in new[] { Axis.X, Axis.Y, Axis.Z }) {
            var failure = RunAxis(axis: axis);

            if (failure is not null) {
                return PostStageOutcome.Fail(detail: failure);
            }
        }

        return PostStageOutcome.Pass(detail: "cell carry, delta, and translating add are correct across cell boundaries on all three axes");
    }

    // The full correctness battery on ONE axis; returns a failure description, or null on success.
    private static string? RunAxis(Axis axis) {
        // Carry cases along the axis — offsets that reach and pass ±half a cell, so Normalize must re-anchor.
        (long Cell, long LocalRaw)[] carryCases = [
            (0L, 0L), (0L, (4L << 16)), (0L, -(4L << 16)),
            (0L, (HalfCellRaw - 1L)), (0L, -HalfCellRaw),
            (0L, HalfCellRaw), (0L, (HalfCellRaw + (10L << 16))),
            (0L, (-HalfCellRaw - 1L)), (0L, -CellRaw),
            (5L, 0L), (-5L, 0L), (7L, (HalfCellRaw + 3L)),
        ];

        foreach (var (cell, localRaw) in carryCases) {
            var (normCell, normLocal) = Component(axis: axis, coord: Coord(axis: axis, cell: cell, localRaw: localRaw).Normalize());

            if ((normLocal < -HalfCellRaw) || (normLocal >= HalfCellRaw)) {
                return $"[{axis}] Normalize left the offset out of range for (cell {cell}, raw {localRaw})";
            }

            if (((cell * CellRaw) + localRaw) != ((normCell * CellRaw) + normLocal)) {
                return $"[{axis}] Normalize moved the position for (cell {cell}, raw {localRaw})";
            }
        }

        // Delta is the exact cell-aware difference for near cells.
        (long Cell, long LocalRaw)[] points = [(3L, (10L << 16)), (-2L, -(5L << 16)), (0L, 1234L), (1L, -(HalfCellRaw / 2L))];

        foreach (var (cellA, rawA) in points) {
            foreach (var (cellB, rawB) in points) {
                var delta = Component(axis: axis, delta: Coord(axis: axis, cell: cellA, localRaw: rawA).Delta(origin: Coord(axis: axis, cell: cellB, localRaw: rawB)));
                var expected = (((cellA * CellRaw) + rawA) - ((cellB * CellRaw) + rawB));

                if (delta != expected) {
                    return $"[{axis}] Delta wrong for (cell {cellA},{rawA}) − (cell {cellB},{rawB})";
                }
            }
        }

        // operator+ translates and re-anchors: adding a local delta moves the absolute position by exactly that delta,
        // even across a cell boundary, and leaves the offset centred.
        long[] deltas = [0L, (3L << 16), -(3L << 16), HalfCellRaw, (-HalfCellRaw - 1L), ((2L * CellRaw) + 7L)];

        foreach (var (cell, localRaw) in points) {
            foreach (var d in deltas) {
                var (movedCell, movedLocal) = Component(axis: axis, coord: (Coord(axis: axis, cell: cell, localRaw: localRaw) + Vec(axis: axis, raw: d)));

                if (((cell * CellRaw) + localRaw + d) != ((movedCell * CellRaw) + movedLocal)) {
                    return $"[{axis}] operator+ wrong for (cell {cell},{localRaw}) + {d}";
                }

                if ((movedLocal < -HalfCellRaw) || (movedLocal >= HalfCellRaw)) {
                    return $"[{axis}] operator+ left the offset out of range for (cell {cell},{localRaw}) + {d}";
                }
            }
        }

        // Far-cell translation invariance: a position and its origin shifted by the SAME huge cell delta yield the SAME
        // displacement — the planet-scale guarantee, in the type itself.
        var near = Component(axis: axis, delta: Coord(axis: axis, cell: 0L, localRaw: (123L << 16)).Delta(origin: Coord(axis: axis, cell: 0L, localRaw: (7L << 16))));
        var far = Component(axis: axis, delta: Coord(axis: axis, cell: 1_000_000_000L, localRaw: (123L << 16)).Delta(origin: Coord(axis: axis, cell: 1_000_000_000L, localRaw: (7L << 16))));

        if (near != far) {
            return $"[{axis}] Delta is not translation-invariant across cells: near {near}, far {far}";
        }

        return null;
    }

    private enum Axis { X, Y, Z }

    // Build a coordinate carrying (cell, localRaw) on the tested axis and zero on the others.
    private static WorldCoord3 Coord(Axis axis, long cell, long localRaw) => axis switch {
        Axis.X => new(CellX: cell, CellY: 0L, CellZ: 0L, Local: Vec(axis: Axis.X, raw: localRaw)),
        Axis.Y => new(CellX: 0L, CellY: cell, CellZ: 0L, Local: Vec(axis: Axis.Y, raw: localRaw)),
        _ => new(CellX: 0L, CellY: 0L, CellZ: cell, Local: Vec(axis: Axis.Z, raw: localRaw)),
    };

    // A displacement vector with `raw` on the tested axis and zero on the others.
    private static FixedVector3 Vec(Axis axis, long raw) => axis switch {
        Axis.X => new(X: FixedQ4816.FromRawBits(value: raw), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
        Axis.Y => new(X: FixedQ4816.Zero, Y: FixedQ4816.FromRawBits(value: raw), Z: FixedQ4816.Zero),
        _ => new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.FromRawBits(value: raw)),
    };

    // The (cell, localRaw) of the tested axis from a coordinate.
    private static (long Cell, long LocalRaw) Component(Axis axis, WorldCoord3 coord) => axis switch {
        Axis.X => (coord.CellX, coord.Local.X.Value),
        Axis.Y => (coord.CellY, coord.Local.Y.Value),
        _ => (coord.CellZ, coord.Local.Z.Value),
    };

    // The tested axis's raw value from a displacement (a Delta result).
    private static long Component(Axis axis, FixedVector3 delta) => axis switch {
        Axis.X => delta.X.Value,
        Axis.Y => delta.Y.Value,
        _ => delta.Z.Value,
    };
}
