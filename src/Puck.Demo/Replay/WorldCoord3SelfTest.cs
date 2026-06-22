using Puck.Maths;

namespace Puck.Demo.Replay;

/// <summary>
/// Checks that <see cref="WorldCoord3"/>'s cell carry (<see cref="WorldCoord3.Normalize"/>), cell-aware difference
/// (<see cref="WorldCoord3.Delta"/>), and translating add (<c>operator +</c>) are CORRECT — including the cross-cell
/// paths the demo's single-cell room never exercises. Each is compared against an ABSOLUTE fixed-point reference
/// (<c>cell·CellSize + local</c>) at small cell indices where that reference fits in a 64-bit integer, plus the
/// centred-offset invariant and the far-cell translation-invariance guarantee. (A determinism gate alone can't catch a
/// wrong-but-deterministic carry, so this runs alongside the fixed-point self-test.)
/// </summary>
internal static class WorldCoord3SelfTest {
    // A cell edge and half-edge in raw Q48.16 units, derived from the public constants (CellSizeLog2 world units scaled
    // by the fraction bit count). Kept in sync with WorldCoord3's private constants by construction.
    private const int CellRawLog2 = (WorldCoord3.CellSizeLog2 + FixedQ4816.FractionBitCount);
    private const long CellRaw = (1L << CellRawLog2);
    private const long HalfCellRaw = (1L << (CellRawLog2 - 1));

    /// <summary>Runs the self-test.</summary>
    /// <param name="message">A human-readable detail of the first failure, or a success summary.</param>
    /// <returns><see langword="true"/> when every check passes.</returns>
    public static bool Run(out string message) {
        // Carry cases along one axis — offsets that reach and pass ±half a cell, so Normalize must re-anchor.
        (long Cell, long LocalRaw)[] carryCases = [
            (0L, 0L), (0L, (4L << 16)), (0L, -(4L << 16)),
            (0L, (HalfCellRaw - 1L)), (0L, -HalfCellRaw),
            (0L, HalfCellRaw), (0L, (HalfCellRaw + (10L << 16))),
            (0L, (-HalfCellRaw - 1L)), (0L, -CellRaw),
            (5L, 0L), (-5L, 0L), (7L, (HalfCellRaw + 3L)),
        ];

        foreach (var (cell, localRaw) in carryCases) {
            var normalized = Coord(cell: cell, localRaw: localRaw).Normalize();

            // The centred-offset invariant: local lands in [-half, half).
            if (
                (normalized.Local.X.Value < -HalfCellRaw) ||
                (normalized.Local.X.Value >= HalfCellRaw)
            ) {
                message = $"Normalize left the offset out of range for (cell {cell}, raw {localRaw}): {normalized.Local.X.Value}";

                return false;
            }

            // The position is preserved: the absolute raw (cell·CellRaw + local) is unchanged by the carry.
            if (((cell * CellRaw) + localRaw) != ((normalized.CellX * CellRaw) + normalized.Local.X.Value)) {
                message = $"Normalize moved the position for (cell {cell}, raw {localRaw}).";

                return false;
            }
        }

        // Delta is the exact cell-aware difference for near cells.
        (long Cell, long LocalRaw)[] points = [(3L, (10L << 16)), (-2L, -(5L << 16)), (0L, 1234L), (1L, -(HalfCellRaw / 2L))];

        foreach (var (cellA, rawA) in points) {
            foreach (var (cellB, rawB) in points) {
                var delta = Coord(cell: cellA, localRaw: rawA).Delta(origin: Coord(cell: cellB, localRaw: rawB)).X.Value;
                var expected = (((cellA * CellRaw) + rawA) - ((cellB * CellRaw) + rawB));

                if (delta != expected) {
                    message = $"Delta wrong for (cell {cellA},{rawA}) − (cell {cellB},{rawB}): {delta}, want {expected}.";

                    return false;
                }
            }
        }

        // operator+ translates and re-anchors: adding a local delta moves the absolute position by EXACTLY that delta,
        // even across a cell boundary, and leaves the offset centred.
        long[] deltas = [0L, (3L << 16), -(3L << 16), HalfCellRaw, (-HalfCellRaw - 1L), ((2L * CellRaw) + 7L)];

        foreach (var (cell, localRaw) in points) {
            foreach (var d in deltas) {
                var moved = (Coord(cell: cell, localRaw: localRaw) + Vec(raw: d));

                if (((cell * CellRaw) + localRaw + d) != ((moved.CellX * CellRaw) + moved.Local.X.Value)) {
                    message = $"operator+ wrong for (cell {cell},{localRaw}) + {d}.";

                    return false;
                }

                if (
                    (moved.Local.X.Value < -HalfCellRaw) ||
                    (moved.Local.X.Value >= HalfCellRaw)
                ) {
                    message = $"operator+ left the offset out of range for (cell {cell},{localRaw}) + {d}: {moved.Local.X.Value}.";

                    return false;
                }
            }
        }

        // Far-cell translation invariance: a position and its origin shifted by the SAME huge cell delta yield the SAME
        // displacement — the planet-scale guarantee, in the type itself.
        var near = Coord(cell: 0L, localRaw: (123L << 16)).Delta(origin: Coord(cell: 0L, localRaw: (7L << 16))).X.Value;
        var far = Coord(cell: 1_000_000_000L, localRaw: (123L << 16)).Delta(origin: Coord(cell: 1_000_000_000L, localRaw: (7L << 16))).X.Value;

        if (near != far) {
            message = $"Delta is not translation-invariant across cells: near {near}, far {far}.";

            return false;
        }

        message = "WorldCoord3 cell carry, delta, and translating add are correct across cell boundaries";

        return true;
    }

    private static WorldCoord3 Coord(long cell, long localRaw) =>
        new(CellX: cell, CellY: 0L, CellZ: 0L, Local: Vec(raw: localRaw));
    private static FixedVector3 Vec(long raw) =>
        new(X: FixedQ4816.FromRawBits(value: raw), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
}
