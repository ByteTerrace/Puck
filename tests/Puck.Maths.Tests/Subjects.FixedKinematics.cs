using System.Globalization;
using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- the kinematics family: FixedPosition, FixedRigidTransform, the two rate integrators ----
    //
    // One cell spans 2^CellSizeLog2 world units and one world unit is 2^FractionBitCount raws, so a cell spans 2^36 raws
    // and the canonical band is [−2^35, 2^35). Both constants are derived from the two PUBLIC ones rather than written
    // as literals, so a change to either moves every law below with it. FixedPosition needs no operand fold anywhere:
    // every long is a legal cell index and every long a legal offset, and the refusals are part of the contract, so the
    // sampled raw reaches subject and oracle unchanged.
    private const int CellRawLog2 = (FixedPosition.CellSizeLog2 + FixedQ4816.FractionBitCount);
    private const long HalfCellRaw = (1L << (CellRawLog2 - 1));

    // Whether an exact arbitrary-width value is representable in the signed 64-bit carrier — the predicate every
    // refusal statement in this family is written against, stated once.
    private static bool WithinCarrier(BigInteger value) =>
        ((value >= long.MinValue) && (value <= long.MaxValue));
    // Constructs through TryCreate and SUBSTITUTES the origin where the construction refuses, identically on both sides.
    // The refusal itself belongs to position.canonical-vs-oracle, not to a displacement or translation law.
    private static FixedPosition PositionOrZero(long cellX, long cellY, long cellZ, long localX, long localY, long localZ) =>
        (FixedPosition.TryCreate(
            cellX: cellX,
            cellY: cellY,
            cellZ: cellZ,
            local: Space(
                x: localX,
                y: localY,
                z: localZ
            ),
            result: out var result
        )
            ? result
            : FixedPosition.Zero
        );

    /// <summary>Proves <see cref="FixedPosition"/>'s canonicalization against the exact integer split at every swept
    /// axis triple: the accepted cell indices and offsets, the refusal predicate and the whole-position residue it
    /// leaves, the canonical band, the idempotence of <see cref="FixedPosition.Normalize"/>, the deconstruction order,
    /// the throwing constructor's agreement with <see cref="FixedPosition.TryCreate"/>, and the two construction
    /// shorthands.</summary>
    /// <param name="left">The three offset raws in its first three lanes, and a replacement offset in the fourth.</param>
    /// <param name="right">The three cell indices in its first three lanes, and a second replacement offset in the fourth.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PositionCanonicalExact(long[] left, long[] right) {
        var expectedCells = new BigInteger[3];
        var expectedLocals = new BigInteger[3];
        var representable = true;

        for (var axis = 0; (axis < 3); ++axis) {
            var (cell, local) = Oracles.CellSplit(
                cell: right[axis],
                localRaw: left[axis],
                cellRawLog2: CellRawLog2
            );

            expectedCells[axis] = cell;
            expectedLocals[axis] = local;
            representable &= WithinCarrier(value: cell);
        }

        var offset = Space(
            x: left[0],
            y: left[1],
            z: left[2]
        );
        var accepted = FixedPosition.TryCreate(
            cellX: right[0],
            cellY: right[1],
            cellZ: right[2],
            local: offset,
            result: out var created
        );

        if (accepted != representable) { return $"TryCreate at cells ({right[0]},{right[1]},{right[2]}) offsets ({left[0]},{left[1]},{left[2]}) reported {accepted}"; }

        if (!accepted) {
            if (created != FixedPosition.Zero) { return "a refused construction did not leave Zero behind"; }
            if (!Throws<OverflowException>(action: () => _ = new FixedPosition(
                cellX: right[0],
                cellY: right[1],
                cellZ: right[2],
                local: offset
            ))) {
                return "the throwing constructor accepted a position TryCreate refused";
            }

            return null;
        }

        Span<long> cells = [created.CellX, created.CellY, created.CellZ];
        Span<long> locals = [created.Local.X.Value, created.Local.Y.Value, created.Local.Z.Value];

        for (var axis = 0; (axis < 3); ++axis) {
            if (cells[axis] != expectedCells[axis]) { return $"axis {axis}'s canonical cell is {cells[axis]}, expected {expectedCells[axis]}"; }
            if (locals[axis] != expectedLocals[axis]) { return $"axis {axis}'s canonical offset is {locals[axis]}, expected {expectedLocals[axis]}"; }
            if (
                (locals[axis] < -HalfCellRaw) ||
                (locals[axis] >= HalfCellRaw)
            ) { return $"axis {axis}'s canonical offset {locals[axis]} left the half-open band"; }
        }

        if (created.Normalize() != created) { return "Normalize moved an accepted position"; }
        if (new FixedPosition(
            cellX: right[0],
            cellY: right[1],
            cellZ: right[2],
            local: offset
        ) != created) { return "the throwing constructor disagrees with TryCreate where both succeed"; }

        var (deconstructedX, deconstructedY, deconstructedZ, deconstructedLocal) = created;

        if (
            (deconstructedX != created.CellX) ||
            (deconstructedY != created.CellY) ||
            (deconstructedZ != created.CellZ) ||
            (deconstructedLocal != created.Local)
        ) {
            return "Deconstruct did not hand back (CellX, CellY, CellZ, Local) in the declared order";
        }

        // The declared cell size, read as an OBSERVABLE fact rather than as a constant compared with itself: an offset
        // of exactly half a cell carries UP into the next cell, minus half a cell does not, and one raw below half a
        // cell does not — which is what CellSizeLog2 == 20 and FixedQ4816.FractionBitCount == 16 together mean.
        var atHalf = FixedPosition.FromLocal(local: Space(
            x: HalfCellRaw,
            y: -HalfCellRaw,
            z: (HalfCellRaw - 1L)
        ));

        if (
            (1L != atHalf.CellX) ||
            (-HalfCellRaw != atHalf.Local.X.Value)
        ) { return "an offset of exactly half a cell did not carry up"; }
        if (
            (0L != atHalf.CellY) ||
            (-HalfCellRaw != atHalf.Local.Y.Value)
        ) { return "an offset of minus half a cell carried"; }
        if (
            (0L != atHalf.CellZ) ||
            ((HalfCellRaw - 1L) != atHalf.Local.Z.Value)
        ) { return "an offset one raw below half a cell carried"; }
        if (FixedPosition.Zero != default) { return "Zero is not the default value"; }
        if (FixedPosition.Zero.Normalize() != FixedPosition.Zero) { return "Zero is not canonical"; }

        if (!FixedPosition.TryCreate(
            cellX: 0L,
            cellY: 0L,
            cellZ: 0L,
            local: offset,
            result: out var anchored
        )) { return "TryCreate refused an offset at the origin cell"; }
        if (FixedPosition.FromLocal(local: offset) != anchored) { return "FromLocal disagrees with TryCreate at the origin cell"; }

        var replacement = Space(
            x: left[3],
            y: right[3],
            z: left[0]
        );
        var replaceable = FixedPosition.TryCreate(
            cellX: created.CellX,
            cellY: created.CellY,
            cellZ: created.CellZ,
            local: replacement,
            result: out var replaced
        );

        if (replaceable) {
            if (created.WithLocal(local: replacement) != replaced) { return "WithLocal disagrees with TryCreate against the CURRENT cell"; }
        } else if (!Throws<OverflowException>(action: () => _ = created.WithLocal(local: replacement))) {
            return "WithLocal accepted a replacement offset TryCreate refused";
        }

        return null;
    }
    /// <summary>Proves <see cref="FixedPosition.TryDelta"/> against the single exact displacement expression on every
    /// swept pair, in both directions: the value through both code paths, the refusal predicate and its zero residue,
    /// the two throwing readers, exact antisymmetry, and that both paths are actually reached.</summary>
    /// <param name="left">The target's three cell indices then its three offset raws.</param>
    /// <param name="right">The origin's three cell indices then its three offset raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PositionDeltaExact(long[] left, long[] right) {
        var coord = PositionOrZero(
            cellX: left[0],
            cellY: left[1],
            cellZ: left[2],
            localX: left[3],
            localY: left[4],
            localZ: left[5]
        );
        var origin = PositionOrZero(
            cellX: right[0],
            cellY: right[1],
            cellZ: right[2],
            localX: right[3],
            localY: right[4],
            localZ: right[5]
        );

        if (PositionDeltaFailure(
            coord: coord,
            origin: origin
        ) is { } forward) { return forward; }
        if (PositionDeltaFailure(
            coord: origin,
            origin: coord
        ) is { } backward) { return backward; }

        if (
            coord.TryDelta(
            delta: out var there,
            origin: origin
        ) &&
            origin.TryDelta(
            delta: out var back,
            origin: coord
        )
        ) {
            if (there.X.Value != unchecked(-back.X.Value)) { return $"antisymmetry failed on X: {there.X.Value} against {back.X.Value}"; }
            if (there.Y.Value != unchecked(-back.Y.Value)) { return $"antisymmetry failed on Y: {there.Y.Value} against {back.Y.Value}"; }
            if (there.Z.Value != unchecked(-back.Z.Value)) { return $"antisymmetry failed on Z: {there.Z.Value} against {back.Z.Value}"; }
        }

        return PositionDeltaPathsReached();
    }

    private static string? PositionDeltaFailure(FixedPosition coord, FixedPosition origin) {
        var expectedX = Oracles.CellDelta(
            cell: coord.CellX,
            localRaw: coord.Local.X.Value,
            originCell: origin.CellX,
            originLocalRaw: origin.Local.X.Value,
            cellRawLog2: CellRawLog2
        );
        var expectedY = Oracles.CellDelta(
            cell: coord.CellY,
            localRaw: coord.Local.Y.Value,
            originCell: origin.CellY,
            originLocalRaw: origin.Local.Y.Value,
            cellRawLog2: CellRawLog2
        );
        var expectedZ = Oracles.CellDelta(
            cell: coord.CellZ,
            localRaw: coord.Local.Z.Value,
            originCell: origin.CellZ,
            originLocalRaw: origin.Local.Z.Value,
            cellRawLog2: CellRawLog2
        );
        var representable = (WithinCarrier(value: expectedX) && WithinCarrier(value: expectedY) && WithinCarrier(value: expectedZ));
        var accepted = coord.TryDelta(
            delta: out var delta,
            origin: origin
        );

        if (accepted != representable) { return $"TryDelta reported {accepted} where the exact displacement ({expectedX},{expectedY},{expectedZ}) is representable: {representable}"; }

        if (!accepted) {
            if (delta != FixedVector3.Zero) { return "a refused TryDelta did not leave the zero vector"; }
            if (!Throws<OverflowException>(action: () => _ = coord.Delta(origin: origin))) { return "Delta accepted a displacement TryDelta refused"; }
            if (!Throws<OverflowException>(action: () => _ = (coord - origin))) { return "the subtraction operator accepted a displacement TryDelta refused"; }

            return null;
        }

        if (delta.X.Value != expectedX) { return $"the X displacement is {delta.X.Value}, expected {expectedX}"; }
        if (delta.Y.Value != expectedY) { return $"the Y displacement is {delta.Y.Value}, expected {expectedY}"; }
        if (delta.Z.Value != expectedZ) { return $"the Z displacement is {delta.Z.Value}, expected {expectedZ}"; }
        if (coord.Delta(origin: origin) != delta) { return "Delta disagrees with TryDelta"; }
        if ((coord - origin) != delta) { return "the subtraction operator disagrees with Delta"; }

        return null;
    }
    // Both TryDelta paths, REACHED rather than believed reachable, and both counted so a gate moved to a constant would
    // leave one arm dark. The classification here is the exact BigInteger reading of the source's own gate — a
    // non-overflowing cell subtraction AND |cellDelta| at most 2²⁶ — never the source's spelling of it.
    private static string? PositionDeltaPathsReached() {
        var narrow = 0;
        var wide = 0;

        foreach (var (cell, originCell) in PositionDeltaPathLadder) {
            var coord = PositionOrZero(
                cellX: cell,
                cellY: 0L,
                cellZ: 0L,
                localX: 0L,
                localY: 0L,
                localZ: 0L
            );
            var origin = PositionOrZero(
                cellX: originCell,
                cellY: 0L,
                cellZ: 0L,
                localX: 0L,
                localY: 0L,
                localZ: 0L
            );
            var cellDelta = (new BigInteger(value: cell) - originCell);

            if (
                WithinCarrier(value: cellDelta) &&
                (BigInteger.Abs(value: cellDelta) <= (BigInteger.One << 26))
            ) { ++narrow; } else { ++wide; }

            if (PositionDeltaFailure(
                coord: coord,
                origin: origin
            ) is { } failure) { return failure; }
        }

        if (0 == narrow) { return "no witness reached TryDelta's long path"; }
        if (0 == wide) { return "no witness reached TryDelta's Int128 path"; }

        return null;
    }

    /// <summary>Proves <see cref="FixedPosition.TryTranslate"/> against the exact canonicalization of the exact
    /// per-axis sum on every swept pair: the value through both the long and the <c>Int128</c> path, the refusal
    /// predicate and its zero residue, the throwing operator, the zero-vector fixed point, and that both paths — chosen
    /// by the WORST axis — are actually reached.</summary>
    /// <param name="left">The base position's three cell indices then its three offset raws.</param>
    /// <param name="right">The displacement's three raws, then a second displacement's three raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PositionTranslateExact(long[] left, long[] right) {
        var value = PositionOrZero(
            cellX: left[0],
            cellY: left[1],
            cellZ: left[2],
            localX: left[3],
            localY: left[4],
            localZ: left[5]
        );

        if (PositionTranslateFailure(
            value: value,
            delta: Space(
                x: right[0],
                y: right[1],
                z: right[2]
            )
        ) is { } first) { return first; }
        if (PositionTranslateFailure(
            value: value,
            delta: Space(
                x: right[3],
                y: right[4],
                z: right[5]
            )
        ) is { } second) { return second; }

        if (!value.TryTranslate(
            delta: FixedVector3.Zero,
            result: out var unmoved
        )) { return "translating by the zero vector refused"; }
        if (unmoved != value) { return "translating by the zero vector moved an already-canonical position"; }

        return PositionTranslatePathsReached();
    }

    private static string? PositionTranslateFailure(FixedPosition value, FixedVector3 delta) {
        Span<long> cells = [value.CellX, value.CellY, value.CellZ];
        Span<long> locals = [value.Local.X.Value, value.Local.Y.Value, value.Local.Z.Value];
        Span<long> deltas = [delta.X.Value, delta.Y.Value, delta.Z.Value];
        var expectedCells = new BigInteger[3];
        var expectedLocals = new BigInteger[3];
        var representable = true;

        for (var axis = 0; (axis < 3); ++axis) {
            var (cell, local) = Oracles.CellSplit(
                cell: cells[axis],
                localRaw: (new BigInteger(value: locals[axis]) + deltas[axis]),
                cellRawLog2: CellRawLog2
            );

            expectedCells[axis] = cell;
            expectedLocals[axis] = local;
            representable &= WithinCarrier(value: cell);
        }

        var accepted = value.TryTranslate(
            delta: delta,
            result: out var translated
        );

        if (accepted != representable) { return $"TryTranslate by ({deltas[0]},{deltas[1]},{deltas[2]}) reported {accepted}, expected {representable}"; }

        if (!accepted) {
            if (translated != FixedPosition.Zero) { return "a refused TryTranslate did not leave Zero behind"; }
            if (!Throws<OverflowException>(action: () => _ = (value + delta))) { return "the addition operator accepted a translation TryTranslate refused"; }

            return null;
        }

        Span<long> movedCells = [translated.CellX, translated.CellY, translated.CellZ];
        Span<long> movedLocals = [translated.Local.X.Value, translated.Local.Y.Value, translated.Local.Z.Value];

        for (var axis = 0; (axis < 3); ++axis) {
            if (movedCells[axis] != expectedCells[axis]) { return $"axis {axis}'s translated cell is {movedCells[axis]}, expected {expectedCells[axis]}"; }
            if (movedLocals[axis] != expectedLocals[axis]) { return $"axis {axis}'s translated offset is {movedLocals[axis]}, expected {expectedLocals[axis]}"; }
        }

        if ((value + delta) != translated) { return "the addition operator disagrees with TryTranslate"; }

        return null;
    }
    // Both TryTranslate paths, reached and counted. The narrow branch is taken only when ALL THREE component adds
    // survive the carrier, so the second witness — one overflowing axis against two that do not — is what forces the
    // wide canonicalizer to run on components that never needed it.
    private static string? PositionTranslatePathsReached() {
        var narrow = 0;
        var wide = 0;

        foreach (var (localRaw, deltaRaw) in PositionTranslatePathLadder) {
            var value = PositionOrZero(
                cellX: 0L,
                cellY: 0L,
                cellZ: 0L,
                localX: localRaw,
                localY: 0L,
                localZ: 0L
            );
            var delta = Space(
                x: deltaRaw,
                y: 65536L,
                z: -65536L
            );
            var exact = (new BigInteger(value: value.Local.X.Value) + deltaRaw);

            if (WithinCarrier(value: exact)) { ++narrow; } else { ++wide; }

            if (PositionTranslateFailure(
                delta: delta,
                value: value
            ) is { } failure) { return failure; }
        }

        if (0 == narrow) { return "no witness reached TryTranslate's long path"; }
        if (0 == wide) { return "no witness reached TryTranslate's Int128 path"; }

        return null;
    }

    /// <summary>Proves the exact torsor structure <see cref="FixedPosition"/> claims: a translation followed by the
    /// displacement back recovers the displacement BIT-FOR-BIT, a position's displacement from itself is exactly zero,
    /// <see cref="FixedPosition.FromLocal"/>'s re-anchoring never moves the displacement, the two displacement routes
    /// agree exactly where the carrier's vector add did not wrap, and <see cref="FixedPosition.Zero"/> is a two-sided
    /// neutral.</summary>
    /// <param name="left">The base position's three cell indices then its three offset raws.</param>
    /// <param name="right">The first displacement's three raws then the second's.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PositionGroupStructureExact(long[] left, long[] right) {
        var value = PositionOrZero(
            cellX: left[0],
            cellY: left[1],
            cellZ: left[2],
            localX: left[3],
            localY: left[4],
            localZ: left[5]
        );
        var first = Space(
            x: right[0],
            y: right[1],
            z: right[2]
        );
        var second = Space(
            x: right[3],
            y: right[4],
            z: right[5]
        );

        if (value.TryTranslate(
            delta: first,
            result: out var moved
        )) {
            if (!moved.TryDelta(
                delta: out var recovered,
                origin: value
            )) { return "the displacement from a position to its own translation is not representable"; }
            if (recovered != first) { return $"the torsor law failed: ({right[0]},{right[1]},{right[2]}) came back as ({recovered.X.Value},{recovered.Y.Value},{recovered.Z.Value})"; }
        }

        if (!value.TryDelta(
            delta: out var self,
            origin: value
        )) { return "a position's displacement from itself is not representable"; }
        if (self != FixedVector3.Zero) { return "a position's displacement from itself is not the zero vector"; }
        if (!FixedPosition.FromLocal(local: first).TryDelta(
            origin: FixedPosition.Zero,
            delta: out var reanchored
        )) { return "FromLocal's displacement from the origin is not representable"; }
        if (reanchored != first) { return "FromLocal's re-anchoring moved the DISPLACEMENT, not only the stored offset"; }

        var combined = (first + second);
        var unwrapped = (
            ((new BigInteger(value: right[0]) + right[3]) == combined.X.Value) &&
            ((new BigInteger(value: right[1]) + right[4]) == combined.Y.Value) &&
            ((new BigInteger(value: right[2]) + right[5]) == combined.Z.Value)
        );
        var chained = FixedPosition.Zero;
        var sequential = (value.TryTranslate(
            delta: first,
            result: out var step
        ) && step.TryTranslate(
            delta: second,
            result: out chained
        ));
        var composite = value.TryTranslate(
            delta: combined,
            result: out var direct
        );

        if (sequential) {
            Span<long> cells = [value.CellX, value.CellY, value.CellZ];
            Span<long> locals = [value.Local.X.Value, value.Local.Y.Value, value.Local.Z.Value];
            Span<long> chainedCells = [chained.CellX, chained.CellY, chained.CellZ];
            Span<long> chainedLocals = [chained.Local.X.Value, chained.Local.Y.Value, chained.Local.Z.Value];

            for (var axis = 0; (axis < 3); ++axis) {
                var exact = ((new BigInteger(value: right[axis]) + right[(axis + 3)]) + locals[axis]);

                var (cell, local) = Oracles.CellSplit(
                    cell: cells[axis],
                    localRaw: exact,
                    cellRawLog2: CellRawLog2
                );

                if (chainedCells[axis] != cell) { return $"axis {axis}'s chained cell is {chainedCells[axis]}, expected {cell}"; }
                if (chainedLocals[axis] != local) { return $"axis {axis}'s chained offset is {chainedLocals[axis]}, expected {local}"; }
            }

            if (
                composite &&
                unwrapped &&
                (chained != direct)
            ) { return "the two displacement routes disagree where the carrier's vector add did not wrap"; }
        }

        if ((FixedPosition.Zero + FixedVector3.Zero) != FixedPosition.Zero) { return "Zero is not a fixed point of the zero translation"; }

        var freshOrigin = new FixedPosition(
            cellX: 0L,
            cellY: 0L,
            cellZ: 0L,
            local: FixedVector3.Zero
        );
        var againstZero = value.TryDelta(
            origin: FixedPosition.Zero,
            delta: out var zeroDelta
        );
        var againstFresh = value.TryDelta(
            delta: out var freshDelta,
            origin: freshOrigin
        );

        if (
            (againstZero != againstFresh) ||
            (zeroDelta != freshDelta)
        ) { return "Zero and a freshly constructed origin measure different displacements"; }

        return null;
    }
    /// <summary>Proves <see cref="FixedPosition.ToRenderRelative"/> over a hand-derived, exactly representable
    /// single-precision ladder, that the far-from-origin row renders the identical floats as the same displacement at
    /// the origin, that the presentation seam refuses exactly where <see cref="FixedPosition.Delta"/> does, and that it
    /// is <c>Delta(origin).ToVector3()</c> on the same operands.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PositionRenderRelativeLadder() {
        var rows = PositionRenderLadder.Length;

        for (var row = 0; (row < rows); ++row) {
            var x = PositionRenderLadder[row];
            var y = PositionRenderLadder[((row + 1) % rows)];
            var z = PositionRenderLadder[((row + 2) % rows)];
            var coord = new FixedPosition(
                cellX: x.Cell,
                cellY: y.Cell,
                cellZ: z.Cell,
                local: Space(
                    x: x.LocalRaw,
                    y: y.LocalRaw,
                    z: z.LocalRaw
                )
            );
            var origin = new FixedPosition(
                cellX: x.OriginCell,
                cellY: y.OriginCell,
                cellZ: z.OriginCell,
                local: Space(
                    x: x.OriginLocalRaw,
                    y: y.OriginLocalRaw,
                    z: z.OriginLocalRaw
                )
            );
            var rendered = coord.ToRenderRelative(origin: origin);

            if (rendered.X != x.Expected) { return $"row {row}'s X renders {rendered.X}, expected {x.Expected}"; }
            if (rendered.Y != y.Expected) { return $"row {row}'s Y renders {rendered.Y}, expected {y.Expected}"; }
            if (rendered.Z != z.Expected) { return $"row {row}'s Z renders {rendered.Z}, expected {z.Expected}"; }
            if (rendered != coord.Delta(origin: origin).ToVector3()) { return $"row {row} disagrees with Delta(origin).ToVector3()"; }
        }

        // The thesis, measured: one world unit away from a position a trillion cells out renders the SAME float triple
        // as one world unit away from the origin. Row 4 is the far pair and row 1 the near one, and the three axes are
        // built the same way so the comparison is over all three at once.
        var nearCoord = new FixedPosition(
            cellX: 0L,
            cellY: 0L,
            cellZ: 0L,
            local: Space(
                x: 65536L,
                y: 65536L,
                z: 65536L
            )
        );
        var farCoord = new FixedPosition(
            cellX: 1_000_000_000_000L,
            cellY: 1_000_000_000_000L,
            cellZ: 1_000_000_000_000L,
            local: Space(
                x: 65536L,
                y: 65536L,
                z: 65536L
            )
        );
        var farOrigin = new FixedPosition(
            cellX: 1_000_000_000_000L,
            cellY: 1_000_000_000_000L,
            cellZ: 1_000_000_000_000L,
            local: FixedVector3.Zero
        );

        if (nearCoord.ToRenderRelative(origin: FixedPosition.Zero) != farCoord.ToRenderRelative(origin: farOrigin)) {
            return "a unit displacement a trillion cells from the world origin does not render the same floats as one at the origin";
        }

        // The presentation seam refuses exactly where the exact path does.
        var high = new FixedPosition(
            cellX: (1L << 47),
            cellY: 0L,
            cellZ: 0L,
            local: FixedVector3.Zero
        );
        var low = new FixedPosition(
            cellX: -(1L << 47),
            cellY: 0L,
            cellZ: 0L,
            local: FixedVector3.Zero
        );

        if (high.TryDelta(
            delta: out _,
            origin: low
        )) { return "the hand-listed unrepresentable pair is representable"; }
        if (!Throws<OverflowException>(action: () => _ = high.ToRenderRelative(origin: low))) { return "ToRenderRelative did not refuse where Delta does"; }

        return null;
    }
    /// <summary>Proves <see cref="FixedPosition"/>'s rendering is invariant and bounded: the hand-written
    /// <c>PrintMembers</c> formats the three cell indices with <see cref="CultureInfo.InvariantCulture"/> — closing
    /// the one ambient-culture read the synthesized formatter carried — and hands <c>Local</c> to
    /// <see cref="FixedVector3"/>'s own bounded, invariant formatter.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PositionPrintsInvariantCells() {
        // Hand-assembled expectations: negative cells pin the invariant '-' sign, and the 2^-16 local raw pins the
        // exact dyadic expansion. ENVELOPE: InvariantGlobalization is on for this suite, so a hostile ambient culture
        // cannot be constructed as a control; the statement is the exact expected string, whose '-' and '.' could not
        // survive a culture-honouring formatter under a culture that spells either differently.
        const string ExpectedPosition = "FixedPosition { CellX = -5, CellY = 2, CellZ = -9, Local = FixedVector3 { X = -1.5, Y = 0, Z = 0.0000152587890625 } }";
        const string ExpectedOrigin = "FixedPosition { CellX = 0, CellY = 0, CellZ = 0, Local = FixedVector3 { X = 0, Y = 0, Z = 0 } }";

        var position = new FixedPosition(
            cellX: -5L,
            cellY: 2L,
            cellZ: -9L,
            local: Space(
                x: -98304L,
                y: 0L,
                z: 1L
            )
        );
        var actual = position.ToString();

        if (ExpectedPosition != actual) { return $"the position printed \"{actual}\""; }

        var actualOrigin = FixedPosition.Zero.ToString();

        if (ExpectedOrigin != actualOrigin) { return $"the origin printed \"{actualOrigin}\""; }

        return null;
    }

    // T1 — the render ladder. Each row is one AXIS of a rigid-motion-free displacement, and the three axes of every
    // rendered triple take three DIFFERENT rows, so a transposed component fails on placement before any value is
    // compared. Every expected displacement is an integral or half-integral number of world units below 2²⁴, hence
    // exactly representable in binary32: the law compares floats with EXACT equality and forms no double anywhere.
    // Hand-derived from the displacement each row denotes — the origin, both unit signs, a whole cell (2²⁰ units), the
    // far-from-origin pair, the same displacement reached across a cell boundary, the 2⁻¹⁶ half unit, and the reversed
    // far pair.
    private static readonly (long Cell, long LocalRaw, long OriginCell, long OriginLocalRaw, float Expected)[] PositionRenderLadder = [
        (0L, 0L, 0L, 0L, 0f),
        (0L, 65536L, 0L, 0L, 1f),
        (0L, -65536L, 0L, 0L, -1f),
        (1L, 0L, 0L, 0L, 1048576f),
        (1_000_000_000_000L, 65536L, 1_000_000_000_000L, 0L, 1f),
        (1_000_000_000_001L, -65536L, 1_000_000_000_000L, 0L, 1048575f),
        (0L, 32768L, 0L, 0L, 0.5f),
        (1_000_000_000_000L, 0L, 1_000_000_000_000L, 65536L, -1f),
    ];
    // The two TryDelta arms' witnesses, as (cell, originCell) pairs at a zero offset: inside the conservative gate, at
    // it, one past it, past what the carrier can hold, and the pair whose cell subtraction overflows outright.
    private static readonly (long Cell, long OriginCell)[] PositionDeltaPathLadder = [
        (0L, 0L),
        (1L, -1L),
        ((1L << 26), 0L),
        (((1L << 26) + 1L), 0L),
        ((1L << 27), 0L),
        (long.MaxValue, long.MinValue),
    ];
    // The two TryTranslate arms' witnesses, as (offset raw, displacement raw): the first three keep the component add
    // inside the carrier, the last two overflow it while the other two axes do not.
    private static readonly (long LocalRaw, long DeltaRaw)[] PositionTranslatePathLadder = [
        (0L, 65536L),
        (32768L, -65536L),
        ((HalfCellRaw - 1L), 65536L),
        ((HalfCellRaw - 1L), long.MaxValue),
        (-HalfCellRaw, long.MinValue),
    ];

    // ---- FixedRigidTransform ----
    //
    // A transform is EIGHT raws in the declared order (Real.X, Real.Y, Real.Z, Real.W, Dual.X, Dual.Y, Dual.Z, Dual.W).
    // The permutation onto the doubling tower's basis order is two applications of the hypercomplex family's declared
    // quaternion permutation — the ONE convention the subject and the doubling oracles share.
    private static FixedRigidTransform RigidOf(ReadOnlySpan<long> lanes) =>
        new(Value: new(
            Real: new FixedQuaternion(
                X: Raw(value: lanes[0]),
                Y: Raw(value: lanes[1]),
                Z: Raw(value: lanes[2]),
                W: Raw(value: lanes[3])
            ),
            Dual: new FixedQuaternion(
                X: Raw(value: lanes[4]),
                Y: Raw(value: lanes[5]),
                Z: Raw(value: lanes[6]),
                W: Raw(value: lanes[7])
            )
        ));
    private static void RigidLanes(FixedRigidTransform value, Span<long> lanes) {
        WriteQuaternionLanes(
            value: value.Value.Real,
            result: lanes[..4]
        );
        WriteQuaternionLanes(
            value: value.Value.Dual,
            result: lanes[4..]
        );
    }
    private static void RigidToDoublingLanes(ReadOnlySpan<long> rigid, Span<long> doubling) {
        QuaternionToDoublingLanes(
            quaternion: rigid[..4],
            doubling: doubling[..4]
        );
        QuaternionToDoublingLanes(
            quaternion: rigid[4..],
            doubling: doubling[4..]
        );
    }
    private static void DoublingToRigidLanes(ReadOnlySpan<long> doubling, Span<long> rigid) {
        DoublingToQuaternionLanes(
            doubling: doubling[..4],
            quaternion: rigid[..4]
        );
        DoublingToQuaternionLanes(
            doubling: doubling[4..],
            quaternion: rigid[4..]
        );
    }

    /// <summary>The subject rigid composition as an eight-lane vector operation, the real block's lanes first.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void RigidComposeLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        RigidLanes(
            value: (RigidOf(lanes: left) * RigidOf(lanes: right)),
            lanes: result
        );
    /// <summary>The shared-nothing oracle for the rigid composition — the doubling recursion's charged sums with ONE
    /// rounding per lane, read through the declared lane permutation.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void RigidComposeOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> leftDoubling = stackalloc long[8];
        Span<long> rightDoubling = stackalloc long[8];
        Span<long> product = stackalloc long[8];

        RigidToDoublingLanes(
            doubling: leftDoubling,
            rigid: left
        );
        RigidToDoublingLanes(
            doubling: rightDoubling,
            rigid: right
        );
        Oracles.DoublingDualProduct(
            floors: 2,
            left: leftDoubling,
            result: product,
            right: rightDoubling,
            shift: FixedQ4816.FractionBitCount
        );
        DoublingToRigidLanes(
            doubling: product,
            rigid: result
        );
    }
    /// <summary>Proves the group's unit and its inversion exactly: <see cref="FixedRigidTransform.Identity"/> is a
    /// two-sided identity for the composition with no rounding remainder, it is
    /// <see cref="FixedRigidTransform.MultiplicativeIdentity"/> read through a second name,
    /// <see cref="FixedRigidTransform.Inverse"/> is the wrapped componentwise negation of the six vector lanes and an
    /// involution over the whole raw range, and on the exact unit ladder <c>T · T⁻¹</c> is Identity bit-for-bit.</summary>
    /// <param name="left">The first transform's eight lanes, raw.</param>
    /// <param name="right">The second transform's eight lanes, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RigidIdentityAndInverseExact(long[] left, long[] right) =>
        (RigidIdentityAndInverseFailure(lanes: left) ?? (RigidIdentityAndInverseFailure(lanes: right) ?? RigidUnitInverseLadderFailure()));

    private static string? RigidIdentityAndInverseFailure(long[] lanes) {
        var value = RigidOf(lanes: lanes);

        if ((value * FixedRigidTransform.Identity) != value) { return "Identity is not an exact right identity for the composition"; }
        if ((FixedRigidTransform.Identity * value) != value) { return "Identity is not an exact left identity for the composition"; }
        if (FixedRigidTransform.MultiplicativeIdentity != FixedRigidTransform.Identity) { return "MultiplicativeIdentity is not Identity"; }
        if (FixedRigidTransform.Identity.Value.Real != FixedQuaternion.Identity) { return "Identity's real block is not the unit quaternion"; }
        if (FixedRigidTransform.Identity.Value.Dual != FixedQuaternion.AdditiveIdentity) { return "Identity's dual block is not zero"; }

        var inverse = value.Inverse();
        Span<long> inverseLanes = stackalloc long[8];

        RigidLanes(
            lanes: inverseLanes,
            value: inverse
        );

        for (var lane = 0; (lane < 8); ++lane) {
            var expected = (((lane % 4) == 3)
                ? lanes[lane]
                : Oracles.WrapToRaw(value: -new BigInteger(value: lanes[lane]))
            );

            if (inverseLanes[lane] != expected) { return $"lane {lane} of the inverse is {inverseLanes[lane]}, expected {expected}"; }
        }

        if (inverse.Inverse() != value) { return "the inverse is not an involution"; }
        if (value.Rotation != value.Value.Real) { return "Rotation is not Value.Real"; }
        if (RigidOf(lanes: inverseLanes) != inverse) { return "the positional constructor did not round-trip the inverse's lanes"; }

        return null;
    }
    private static string? RigidUnitInverseLadderFailure() {
        foreach (var rotor in RigidUnitRotors) {
            foreach (var translation in RigidTranslationLadder) {
                var value = FixedRigidTransform.FromRotationTranslation(
                    rotation: QuaternionOf(lanes: rotor.Lanes),
                    translation: Space(
                        x: translation[0],
                        y: translation[1],
                        z: translation[2]
                    )
                );

                if ((value * value.Inverse()) != FixedRigidTransform.Identity) { return $"T · T⁻¹ is not Identity at rotor [{rotor.Lanes[0]},{rotor.Lanes[1]},{rotor.Lanes[2]},{rotor.Lanes[3]}] translation [{translation[0]},{translation[1]},{translation[2]}]"; }
                if ((value.Inverse() * value) != FixedRigidTransform.Identity) { return $"T⁻¹ · T is not Identity at rotor [{rotor.Lanes[0]},{rotor.Lanes[1]},{rotor.Lanes[2]},{rotor.Lanes[3]}] translation [{translation[0]},{translation[1]},{translation[2]}]"; }
            }
        }

        return null;
    }

    /// <summary>Proves <see cref="FixedRigidTransform.Translation"/> against the doubling recursion's own
    /// <c>2·dual·conj(real)</c> at every swept transform, that the doubling is a WRAPPING add rather than a saturating
    /// one, that the discarded scalar lane is exactly zero on the unit ladder, and the two poles the encoding
    /// makes.</summary>
    /// <param name="left">The first transform's eight lanes, raw.</param>
    /// <param name="right">The second transform's eight lanes, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RigidTranslationExact(long[] left, long[] right) {
        if (RigidTranslationFailure(lanes: left) is { } first) { return first; }
        if (RigidTranslationFailure(lanes: right) is { } second) { return second; }
        if (FixedRigidTransform.Identity.Translation != FixedVector3.Zero) { return "Identity's translation is not the zero vector"; }

        var pure = new FixedRigidTransform(Value: new(
            Real: FixedQuaternion.Identity,
            Dual: new FixedQuaternion(
                X: Raw(value: left[4]),
                Y: Raw(value: left[5]),
                Z: Raw(value: left[6]),
                W: FixedQ4816.Zero
            )
        ));
        var translation = pure.Translation;

        if (translation.X.Value != Oracles.WrapToRaw(value: (new BigInteger(value: left[4]) << 1))) { return "a rotation-free transform's X translation is not twice its dual vector part"; }
        if (translation.Y.Value != Oracles.WrapToRaw(value: (new BigInteger(value: left[5]) << 1))) { return "a rotation-free transform's Y translation is not twice its dual vector part"; }
        if (translation.Z.Value != Oracles.WrapToRaw(value: (new BigInteger(value: left[6]) << 1))) { return "a rotation-free transform's Z translation is not twice its dual vector part"; }

        return RigidOrthogonalResidualLadderFailure();
    }

    private static string? RigidTranslationFailure(long[] lanes) {
        var translation = RigidOf(lanes: lanes).Translation;
        Span<long> doubling = stackalloc long[8];
        Span<long> expected = stackalloc long[4];

        RigidToDoublingLanes(
            doubling: doubling,
            rigid: lanes
        );
        Oracles.RigidTranslation(
            result: expected,
            shift: FixedQ4816.FractionBitCount,
            value: doubling
        );

        if (translation.X.Value != expected[0]) { return $"the X translation is {translation.X.Value}, expected {expected[0]}"; }
        if (translation.Y.Value != expected[1]) { return $"the Y translation is {translation.Y.Value}, expected {expected[1]}"; }
        if (translation.Z.Value != expected[2]) { return $"the Z translation is {translation.Z.Value}, expected {expected[2]}"; }

        return null;
    }
    // The DISCARDED scalar lane, measured rather than assumed away: on the exact unit ladder dual·conj(real) has scalar
    // part exactly zero, which is the orthogonality constraint real·dual = 0 observed through the accessor that drops
    // it — and the translation read back is the encoded one, bit for bit.
    private static string? RigidOrthogonalResidualLadderFailure() {
        Span<long> lanes = stackalloc long[8];
        Span<long> doubling = stackalloc long[8];
        Span<long> expected = stackalloc long[4];

        foreach (var rotor in RigidUnitRotors) {
            foreach (var translation in RigidTranslationLadder) {
                var value = FixedRigidTransform.FromRotationTranslation(
                    rotation: QuaternionOf(lanes: rotor.Lanes),
                    translation: Space(
                        x: translation[0],
                        y: translation[1],
                        z: translation[2]
                    )
                );

                RigidLanes(
                    lanes: lanes,
                    value: value
                );
                RigidToDoublingLanes(
                    doubling: doubling,
                    rigid: lanes
                );
                Oracles.RigidTranslation(
                    result: expected,
                    shift: FixedQ4816.FractionBitCount,
                    value: doubling
                );

                if (0L != expected[3]) { return $"the discarded scalar lane is {expected[3]} on the unit ladder"; }
                if (value.Translation != Space(
                    x: translation[0],
                    y: translation[1],
                    z: translation[2]
                )) { return "the unit ladder's translation did not read back bit for bit"; }
            }
        }

        return null;
    }

    /// <summary>Proves <see cref="FixedRigidTransform.FromRotationTranslation"/>: its dual block is ONE ties-to-even
    /// rounding at shift 17 of the exact leaf sums of the pure translation quaternion times the RETURNED rotation —
    /// the halving fused into the narrowing — the encoding round-trips exactly through both accessors
    /// on the unit ladder, the zero rotation follows the quaternion normalizer's identity convention, and a
    /// power-of-two rescaling of the rotor leaves the transform where it was.</summary>
    /// <param name="left">The rotation quaternion's four lanes, raw.</param>
    /// <param name="right">The translation's three raws in its first three lanes.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RigidFromRotationTranslation(long[] left, long[] right) {
        var rotation = QuaternionOf(lanes: left);
        var translation = Space(
            x: right[0],
            y: right[1],
            z: right[2]
        );
        var value = FixedRigidTransform.FromRotationTranslation(
            rotation: rotation,
            translation: translation
        );
        Span<long> unit = stackalloc long[4];
        Span<long> translationLanes = [right[0], right[1], right[2], 0L];
        Span<long> unitDoubling = stackalloc long[4];
        Span<long> translationDoubling = stackalloc long[4];
        Span<long> product = stackalloc long[4];
        Span<long> expected = stackalloc long[4];
        Span<long> dual = stackalloc long[4];

        WriteQuaternionLanes(
            value: value.Rotation,
            result: unit
        );
        QuaternionToDoublingLanes(
            doubling: translationDoubling,
            quaternion: translationLanes
        );
        QuaternionToDoublingLanes(
            doubling: unitDoubling,
            quaternion: unit
        );
        // Shift 17, not 16 then a halving: the subject fuses the ½ into its single narrowing, so the reference is one
        // ties-to-even rounding of each exact leaf sum at 2¹⁷ — the two-step model double-rounds, one raw off on
        // about a quarter of components.
        Oracles.CayleyDicksonProduct(
            floors: 2,
            left: translationDoubling,
            result: product,
            right: unitDoubling,
            shift: (FixedQ4816.FractionBitCount + 1)
        );
        DoublingToQuaternionLanes(
            doubling: product,
            quaternion: expected
        );
        WriteQuaternionLanes(
            value: value.Value.Dual,
            result: dual
        );

        for (var lane = 0; (lane < 4); ++lane) {
            if (dual[lane] != expected[lane]) { return $"dual lane {lane} is {dual[lane]}, expected {expected[lane]}"; }
        }

        if (value.Rotation != rotation.Normalize()) { return "the encoded rotation is not the normalized input"; }
        if (FixedRigidTransform.FromRotationTranslation(
            rotation: default,
            translation: translation
        ) != FixedRigidTransform.FromRotationTranslation(
            rotation: FixedQuaternion.Identity,
            translation: translation
        )) {
            return "the zero rotation does not follow the quaternion normalizer's identity convention";
        }

        return (RigidRotationTranslationLadderFailure() ?? RigidRotationScaleFreedomFailure());
    }

    // T2 × T3 — the exact ladder. Every rotor is exactly unit and every translation raw is even, so the whole chain is
    // exact integer arithmetic and the expectations carry no tolerance.
    private static string? RigidRotationTranslationLadderFailure() {
        Span<long> dual = stackalloc long[4];

        foreach (var rotor in RigidUnitRotors) {
            var rotation = QuaternionOf(lanes: rotor.Lanes);

            foreach (var translation in RigidTranslationLadder) {
                var value = FixedRigidTransform.FromRotationTranslation(
                    rotation: rotation,
                    translation: Space(
                        x: translation[0],
                        y: translation[1],
                        z: translation[2]
                    )
                );

                if (value.Rotation != rotation) { return "an exactly-unit rotor is not a bit-for-bit fixed point of the encoding's normalization"; }

                WriteQuaternionLanes(
                    value: value.Value.Dual,
                    result: dual
                );

                for (var lane = 0; (lane < 4); ++lane) {
                    var source = rotor.Source[lane];
                    var halved = ((source < 0)
                        ? 0L
                        : ((rotor.Sign[lane] * translation[source]) / 2L)
                    );

                    if (dual[lane] != halved) { return $"dual lane {lane} is {dual[lane]}, expected {halved} at rotor [{rotor.Lanes[0]},{rotor.Lanes[1]},{rotor.Lanes[2]},{rotor.Lanes[3]}]"; }
                }

                if (value.Translation != Space(
                    x: translation[0],
                    y: translation[1],
                    z: translation[2]
                )) { return "the encoding and the translation accessor do not close exactly"; }
            }
        }

        return null;
    }
    private static string? RigidRotationScaleFreedomFailure() {
        var translation = Space(
            x: 131072L,
            y: -65536L,
            z: 65536L
        );
        Span<long> baseLanes = stackalloc long[8];
        Span<long> scaledLanes = stackalloc long[8];

        foreach (var rotor in RigidUnitRotors) {
            RigidLanes(
                value: FixedRigidTransform.FromRotationTranslation(
                    rotation: QuaternionOf(lanes: rotor.Lanes),
                    translation: translation
                ),
                lanes: baseLanes
            );

            foreach (var shift in RigidRotationScaleShifts) {
                var scaled = FixedRigidTransform.FromRotationTranslation(
                    rotation: new FixedQuaternion(
                        X: Raw(value: (rotor.Lanes[0] << shift)),
                        Y: Raw(value: (rotor.Lanes[1] << shift)),
                        Z: Raw(value: (rotor.Lanes[2] << shift)),
                        W: Raw(value: (rotor.Lanes[3] << shift))
                    ),
                    translation: translation
                );

                RigidLanes(
                    lanes: scaledLanes,
                    value: scaled
                );

                for (var lane = 0; (lane < 8); ++lane) {
                    if (Math.Abs(value: (scaledLanes[lane] - baseLanes[lane])) > 2L) { return $"scaling the rotor by 2^{shift} moved lane {lane} from {baseLanes[lane]} to {scaledLanes[lane]}"; }
                }
            }
        }

        return null;
    }

    /// <summary>Proves <see cref="FixedRigidTransform.Normalize"/>: the returned real block is the exact Q16 unit
    /// direction of the input real block, the two unit constraints a rigid dual quaternion must satisfy hold, the
    /// re-orthogonalization is observed doing work, the two documented poles are exact, and normalization is idempotent
    /// and commutes with negating both blocks.</summary>
    /// <param name="left">The first transform's eight lanes, raw.</param>
    /// <param name="right">The second transform's eight lanes, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RigidNormalizeUnitConstraints(long[] left, long[] right) =>
        (RigidNormalizeFailure(lanes: left) ?? (RigidNormalizeFailure(lanes: right) ?? (RigidNormalizeProjectionFailure() ?? RigidNormalizePolesFailure())));

    private static string? RigidNormalizeFailure(long[] lanes) {
        var value = RigidOf(lanes: lanes);
        var unit = value.Normalize();

        if (0L == (lanes[0] | lanes[1] | lanes[2] | lanes[3])) {
            return ((unit == FixedRigidTransform.Identity)
                ? null
                : "an all-zero real block did not normalize to Identity"
            );
        }

        Span<long> unitLanes = stackalloc long[8];

        RigidLanes(
            lanes: unitLanes,
            value: unit
        );

        var components = new BigInteger[4];
        var returned = new long[4];
        var squares = BigInteger.Zero;

        for (var lane = 0; (lane < 4); ++lane) {
            components[lane] = new BigInteger(value: lanes[lane]);
            returned[lane] = unitLanes[lane];
            squares += (new BigInteger(value: unitLanes[lane]) * unitLanes[lane]);
        }

        var offending = Oracles.FirstNonUnitLane(
            components: components,
            tolerance: 1L,
            unit: returned
        );

        if (offending >= 0) { return $"lane {offending} of the normalized real block is farther than one raw from the exact unit direction"; }
        if (BigInteger.Abs(value: (Oracles.NearestIntegerRoot(value: squares) - OneRaw)) > QuaternionUnitTolerance) { return "the normalized real block's four-square norm left the declared band of 2¹⁶"; }

        // The residual band, DERIVED from the kernel's own three roundings rather than read off the answer. Writing S₀
        // for the SCALED dual's dot against the returned real block, NormalizeCore leaves
        //     ⟨real′, dual′⟩/2¹⁶ = (S₀/2¹⁶)·(1 − N/2³²) − ε·N/2³² + Σ real′ᵢ·fᵢ/2¹⁶,
        // where N is the returned real block's exact four-square sum, |ε| ≤ ½ is FixedQuaternion.Dot's single rounding
        // and |fᵢ| ≤ ½ is each lane of the scalar multiply's — the two laws quaternion.dot-vs-oracle and
        // quaternion.scale-vs-oracle pin exactly those, and the subtraction between them is exact. The last two terms
        // together stay under three raws, which the floor absorbs. The FIRST term is the one that grows, and it grows
        // with the scaled INPUT dual — NOT with what survives the projection. Reading the band off the returned dual,
        // as this case once did, therefore collapses exactly where the dual is nearly parallel to the real block and
        // the projection annihilates almost all of it: at [2147483647, −1, 2147483648, −65536 | −2⁴⁷, 1, −2⁴⁷, 256] the
        // surviving dual is 65536 while the residual is 9267 — which is, to the raw, the norm defect N − 2³² itself,
        // because there |S₀|/2¹⁶ is 2³². The band below reproduces that number from the operands instead of missing it.
        var inputSquares = BigInteger.Zero;
        var largestInputDual = BigInteger.Zero;
        var laneSum = BigInteger.Zero;

        for (var lane = 0; (lane < 4); ++lane) {
            inputSquares += (components[lane] * components[lane]);
            laneSum += BigInteger.Abs(value: new BigInteger(value: unitLanes[lane]));
        }

        for (var lane = 4; (lane < 8); ++lane) {
            largestInputDual = BigInteger.Max(
            left: largestInputDual,
            right: BigInteger.Abs(value: new BigInteger(value: lanes[lane]))
        );
        }

        // The scaled dual's largest lane, bounded from the EXACT ratio |dual|·2¹⁶/‖real‖ with a deliberately generous
        // factor of two and 64 raws of slack for FixedVectorMath.TryCreateNormalizationScale's own quantization, which
        // this case does not otherwise pin. |S₀|/2¹⁶ is then at most laneSum·scaledDual/2¹⁶.
        var scaledDual = (((largestInputDual << (FixedQ4816.FractionBitCount + 1)) / Oracles.NearestIntegerRoot(value: inputSquares)) + 64);
        var normDefect = BigInteger.Abs(value: (squares - RigidUnitNorm));
        var dualBand = ((((laneSum * scaledDual) * normDefect) >> ((FixedQ4816.FractionBitCount * 2) + 16)) + RigidDualBandFloor);

        // The dual half of the statement is read only where the scaled dual has not WRAPPED the carrier, because outside
        // that band the returned dual denotes no screw at all — the same envelope, applied to the residual and to the
        // idempotence alike. The real half is read unconditionally: the real block is unit and can never wrap.
        var dualDenotes = RigidScaledDualInBand(
            real: components,
            dual: ((ReadOnlySpan<long>)lanes)[4..8]
        );

        if (dualDenotes) {
            var residual = new BigInteger(value: Oracles.LaneDotProduct(
                left: unitLanes[..4],
                right: unitLanes[4..],
                shift: FixedQ4816.FractionBitCount
            ));

            if (BigInteger.Abs(value: residual) > dualBand) { return $"the orthogonality residual is {residual}, outside the derived band {dualBand}"; }
        }

        Span<long> twice = stackalloc long[8];

        RigidLanes(
            value: unit.Normalize(),
            lanes: twice
        );

        for (var lane = 0; (lane < 4); ++lane) {
            if (Math.Abs(value: (twice[lane] - unitLanes[lane])) > 1L) { return $"normalization is not idempotent at real lane {lane}: {unitLanes[lane]} became {twice[lane]}"; }
        }

        if (dualDenotes) {
            for (var lane = 4; (lane < 8); ++lane) {
                if (BigInteger.Abs(value: (new BigInteger(value: twice[lane]) - unitLanes[lane])) > dualBand) { return $"normalization is not idempotent at dual lane {lane}: {unitLanes[lane]} became {twice[lane]}"; }
            }
        }

        var negatable = true;

        foreach (var lane in lanes) { negatable &= (long.MinValue != lane); }

        if (negatable) {
            Span<long> negated = stackalloc long[8];
            Span<long> negatedUnit = stackalloc long[8];

            for (var lane = 0; (lane < 8); ++lane) { negated[lane] = -lanes[lane]; }

            RigidLanes(
                value: RigidOf(lanes: negated).Normalize(),
                lanes: negatedUnit
            );

            for (var lane = 0; (lane < 8); ++lane) {
                var expected = Oracles.WrapToRaw(value: -new BigInteger(value: unitLanes[lane]));

                if (negatedUnit[lane] != expected) { return $"normalization does not commute with negating both blocks at lane {lane}"; }
            }
        }

        return null;
    }
    // Whether the normalization ratio 2¹⁶/‖real‖ applied to the largest dual lane stays inside the signed carrier: the
    // exact test (|dual|·2¹⁶)² < (2⁶²)²·S, squared once so no root is taken. Outside it the scaled dual WRAPS and the
    // orthogonality residual stops denoting anything, which is an ENVELOPE the case states rather than hides.
    private static bool RigidScaledDualInBand(ReadOnlySpan<BigInteger> real, ReadOnlySpan<long> dual) {
        var squaredSum = BigInteger.Zero;
        var largest = BigInteger.Zero;

        foreach (var component in real) { squaredSum += (component * component); }

        foreach (var lane in dual) {
            largest = BigInteger.Max(
            left: largest,
            right: BigInteger.Abs(value: new BigInteger(value: lane))
        );
        }

        return (((largest * largest) << 32) < (squaredSum << 124));
    }
    // The projection is not dead code: this transform's dual block is exactly TWICE its real block, so its exact
    // orthogonality residual is as large as the encoding admits, and the normalized result's residual must be smaller
    // by at least the declared factor.
    private static string? RigidNormalizeProjectionFailure() {
        Span<long> lanes = [32768L, 16384L, -8192L, 49152L, 65536L, 32768L, -16384L, 98304L];
        Span<long> unitLanes = stackalloc long[8];

        RigidLanes(
            value: RigidOf(lanes: lanes).Normalize(),
            lanes: unitLanes
        );

        var before = BigInteger.Abs(value: new BigInteger(value: Oracles.LaneDotProduct(
            left: lanes[..4],
            right: lanes[4..],
            shift: FixedQ4816.FractionBitCount
        )));
        var after = BigInteger.Abs(value: new BigInteger(value: Oracles.LaneDotProduct(
            left: unitLanes[..4],
            right: unitLanes[4..],
            shift: FixedQ4816.FractionBitCount
        )));

        if ((after * RigidProjectionFactor) > before) { return $"the re-orthogonalization reduced the residual from {before} only to {after}"; }

        return null;
    }
    private static string? RigidNormalizePolesFailure() {
        foreach (var rotor in RigidUnitRotors) {
            var value = new FixedRigidTransform(Value: new(
                Real: QuaternionOf(lanes: rotor.Lanes),
                Dual: FixedQuaternion.AdditiveIdentity
            ));

            if (value.Normalize() != value) { return $"the exactly-unit rotor [{rotor.Lanes[0]},{rotor.Lanes[1]},{rotor.Lanes[2]},{rotor.Lanes[3]}] is not a bit-for-bit fixed point of Normalize"; }
        }

        var degenerate = new FixedRigidTransform(Value: new(
            Real: FixedQuaternion.AdditiveIdentity,
            Dual: new FixedQuaternion(
                X: FixedQ4816.MaxValue,
                Y: FixedQ4816.MinValue,
                Z: FixedQ4816.One,
                W: FixedQ4816.MaxValue
            )
        ));

        if (degenerate.Normalize() != FixedRigidTransform.Identity) { return "an all-zero real block with a large dual block did not normalize to Identity"; }

        return null;
    }

    /// <summary>Proves <see cref="FixedRigidTransform.TryFromDualQuaternion"/>'s refusal predicate, the Identity it
    /// leaves behind, the parameter its throwing sibling names, that both entry points agree wherever they succeed, and
    /// that a nondegenerate rotation-scale dual quaternion is genuinely repaired.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RigidFromDualQuaternionRefusals() {
        foreach (var dual in RigidDegenerateDualLadder) {
            var value = new FixedDual<FixedQuaternion>(
                Real: FixedQuaternion.AdditiveIdentity,
                Dual: QuaternionOf(lanes: dual)
            );

            if (FixedRigidTransform.TryFromDualQuaternion(
                result: out var refused,
                value: value
            )) { return $"a zero real block with dual [{dual[0]},{dual[1]},{dual[2]},{dual[3]}] was accepted"; }
            if (refused != FixedRigidTransform.Identity) { return "a refused TryFromDualQuaternion did not leave Identity behind"; }
            if (!Throws<ArgumentException>(
                action: () => _ = FixedRigidTransform.FromDualQuaternion(value: value),
                paramName: "value"
            )) { return "FromDualQuaternion did not refuse naming the 'value' parameter"; }
        }

        foreach (var lanes in RigidNondegenerateLadder) {
            var value = new FixedDual<FixedQuaternion>(
                Real: QuaternionOf(lanes: lanes.AsSpan(
                    length: 4,
                    start: 0
                )),
                Dual: QuaternionOf(lanes: lanes.AsSpan(
                    length: 4,
                    start: 4
                ))
            );

            if (!FixedRigidTransform.TryFromDualQuaternion(
                result: out var accepted,
                value: value
            )) { return $"a non-zero real block [{lanes[0]},{lanes[1]},{lanes[2]},{lanes[3]}] was refused"; }
            if (accepted != new FixedRigidTransform(Value: value).Normalize()) { return "TryFromDualQuaternion disagrees with the positional constructor followed by Normalize"; }
            if (FixedRigidTransform.FromDualQuaternion(value: value) != accepted) { return "FromDualQuaternion disagrees with TryFromDualQuaternion where both succeed"; }
        }

        var repaired = FixedRigidTransform.FromRotationTranslation(
            rotation: FixedQuaternion.Identity,
            translation: Space(
                x: 131072L,
                y: 0L,
                z: 0L
            )
        );
        Span<long> expected = stackalloc long[8];
        Span<long> actual = stackalloc long[8];

        RigidLanes(
            lanes: expected,
            value: repaired
        );

        foreach (var scaled in RigidRotationScaleLadder) {
            var value = new FixedDual<FixedQuaternion>(
                Real: QuaternionOf(lanes: scaled.AsSpan(
                    length: 4,
                    start: 0
                )),
                Dual: QuaternionOf(lanes: scaled.AsSpan(
                    length: 4,
                    start: 4
                ))
            );

            RigidLanes(
                value: FixedRigidTransform.FromDualQuaternion(value: value),
                lanes: actual
            );

            for (var lane = 0; (lane < 8); ++lane) {
                if (Math.Abs(value: (actual[lane] - expected[lane])) > 2L) { return $"the rotation-scale row [{scaled[3]},{scaled[4]}] repaired lane {lane} to {actual[lane]}, expected {expected[lane]}"; }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="FixedRigidTransform.ComposeNormalized"/> is exactly the composition followed by the
    /// repair, that composing with Identity returns the NORMALIZED operand rather than the operand, and that the
    /// member's reason is real: a chain of unrepaired compositions drifts off the unit norm while the repaired chain
    /// cannot.</summary>
    /// <param name="left">The multiplicand's eight lanes, raw.</param>
    /// <param name="right">The multiplier's eight lanes, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RigidComposeNormalizedTwin(long[] left, long[] right) {
        var a = RigidOf(lanes: left);
        var b = RigidOf(lanes: right);

        if (FixedRigidTransform.ComposeNormalized(
            left: a,
            right: b
        ) != (a * b).Normalize()) { return "ComposeNormalized is not the composition followed by Normalize"; }
        if (FixedRigidTransform.ComposeNormalized(
            left: a,
            right: FixedRigidTransform.Identity
        ) != a.Normalize()) { return "composing with Identity through ComposeNormalized is not the normalized operand"; }

        return RigidComposeChainFailure();
    }

    private static string? RigidComposeChainFailure() {
        // A deliberately off-unit transform: its real four-square norm is 65537 rather than 2¹⁶, so an UNREPAIRED chain
        // multiplies that ratio in at every step while a repaired one cannot.
        var drifting = new FixedRigidTransform(Value: new(
            Real: new FixedQuaternion(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero,
                W: Raw(value: 65537L)
            ),
            Dual: new FixedQuaternion(
                X: Raw(value: 65537L),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero,
                W: FixedQ4816.Zero
            )
        ));
        var raw = drifting;
        var repaired = drifting;

        for (var step = 0; (step < 16); ++step) {
            raw = (raw * drifting);
            repaired = FixedRigidTransform.ComposeNormalized(
                left: repaired,
                right: drifting
            );
        }

        var rawNorm = RigidRealNorm(value: raw);
        var repairedNorm = RigidRealNorm(value: repaired);

        if (BigInteger.Abs(value: (rawNorm - OneRaw)) <= RigidChainDriftFloor) { return $"the unrepaired composition chain did not drift: its real norm is {rawNorm}"; }
        if (BigInteger.Abs(value: (repairedNorm - OneRaw)) > QuaternionUnitTolerance) { return $"the repaired composition chain drifted: its real norm is {repairedNorm}"; }

        return null;
    }
    private static BigInteger RigidRealNorm(FixedRigidTransform value) {
        Span<long> lanes = stackalloc long[8];
        var squares = BigInteger.Zero;

        RigidLanes(
            lanes: lanes,
            value: value
        );

        for (var lane = 0; (lane < 4); ++lane) { squares += (new BigInteger(value: lanes[lane]) * lanes[lane]); }

        return Oracles.NearestIntegerRoot(value: squares);
    }

    /// <summary>Proves <see cref="FixedRigidTransform.TransformPoint"/> against the independently derived
    /// sandwich-then-translate action at every swept transform and point, that Identity fixes every point exactly, that
    /// a pure translation maps <c>p</c> to <c>p + t</c> exactly, the hand-derived geometric ladder, and that composition
    /// agrees with the action.</summary>
    /// <param name="left">The transform's eight lanes, raw.</param>
    /// <param name="right">The point's three raws in its first three lanes.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RigidTransformPointExact(long[] left, long[] right) {
        var value = RigidOf(lanes: left);
        var point = Space(
            x: right[0],
            y: right[1],
            z: right[2]
        );
        var image = value.TransformPoint(point: point);
        Span<long> doubling = stackalloc long[8];
        Span<long> expected = stackalloc long[3];

        RigidToDoublingLanes(
            doubling: doubling,
            rigid: left
        );
        Oracles.RigidPointAction(
            value: doubling,
            point: ((ReadOnlySpan<long>)right)[..3],
            shift: FixedQ4816.FractionBitCount,
            result: expected
        );

        if (image.X.Value != expected[0]) { return $"the transformed X lane is {image.X.Value}, expected {expected[0]}"; }
        if (image.Y.Value != expected[1]) { return $"the transformed Y lane is {image.Y.Value}, expected {expected[1]}"; }
        if (image.Z.Value != expected[2]) { return $"the transformed Z lane is {image.Z.Value}, expected {expected[2]}"; }
        if (FixedRigidTransform.Identity.TransformPoint(point: point) != point) { return "Identity did not fix the point exactly"; }

        foreach (var translation in RigidTranslationLadder) {
            var offset = Space(
                x: translation[0],
                y: translation[1],
                z: translation[2]
            );
            var pure = FixedRigidTransform.FromRotationTranslation(
                rotation: FixedQuaternion.Identity,
                translation: offset
            );

            if (pure.TransformPoint(point: point) != (point + offset)) { return $"a pure translation by [{translation[0]},{translation[1]},{translation[2]}] did not map the point to p + t exactly"; }
        }

        return RigidPointLadderFailure();
    }

    private static string? RigidPointLadderFailure() {
        foreach (var (rotor, translation, point, expected) in RigidPointLadder) {
            var image = FixedRigidTransform
                .FromRotationTranslation(
                rotation: QuaternionOf(lanes: rotor),
                translation: Space(
                    x: translation[0],
                    y: translation[1],
                    z: translation[2]
                )
            )
                .TransformPoint(point: Space(
                x: point[0],
                y: point[1],
                z: point[2]
            ));

            if (image.X.Value != expected[0]) { return $"the ladder image's X lane is {image.X.Value}, expected {expected[0]}"; }
            if (image.Y.Value != expected[1]) { return $"the ladder image's Y lane is {image.Y.Value}, expected {expected[1]}"; }
            if (image.Z.Value != expected[2]) { return $"the ladder image's Z lane is {image.Z.Value}, expected {expected[2]}"; }
        }

        var about = FixedRigidTransform.FromRotationTranslation(
            rotation: QuaternionOf(lanes: RigidUnitRotors[3].Lanes),
            translation: Space(
                x: 131072L,
                y: 0L,
                z: 0L
            )
        );
        var then = FixedRigidTransform.FromRotationTranslation(
            rotation: QuaternionOf(lanes: RigidUnitRotors[1].Lanes),
            translation: Space(
                x: 0L,
                y: 131072L,
                z: 0L
            )
        );
        var probe = Space(
            x: 65536L,
            y: -32768L,
            z: 16384L
        );
        var composed = (about * then).TransformPoint(point: probe);
        var staged = about.TransformPoint(point: then.TransformPoint(point: probe));

        if (Math.Abs(value: (composed.X.Value - staged.X.Value)) > 4L) { return $"composition and action disagree on X: {composed.X.Value} against {staged.X.Value}"; }
        if (Math.Abs(value: (composed.Y.Value - staged.Y.Value)) > 4L) { return $"composition and action disagree on Y: {composed.Y.Value} against {staged.Y.Value}"; }
        if (Math.Abs(value: (composed.Z.Value - staged.Z.Value)) > 4L) { return $"composition and action disagree on Z: {composed.Z.Value} against {staged.Z.Value}"; }

        return null;
    }

    /// <summary>Proves the screw exponential and logarithm seam: both exact branches against inline expectations, the
    /// two hand-derived closed-form ladders, all three poles, the round trip the doc claims, and that the real part of
    /// the rigid exponential is bit-for-bit the quaternion exponential of the same bivector.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RigidExpLogSeam() {
        Span<long> lanes = stackalloc long[8];

        // The pure-translation branch: exact on both sides, no transcendental, no rounding, no wrap.
        foreach (var dual in RigidPureTranslationLadder) {
            var value = FixedRigidTransform.Exp(
                real: FixedVector3.Zero,
                dual: Space(
                    x: dual[0],
                    y: dual[1],
                    z: dual[2]
                )
            );
            var expected = new FixedRigidTransform(Value: new(
                Real: FixedQuaternion.Identity,
                Dual: new FixedQuaternion(
                    X: Raw(value: dual[0]),
                    Y: Raw(value: dual[1]),
                    Z: Raw(value: dual[2]),
                    W: FixedQ4816.Zero
                )
            ));

            if (value != expected) { return $"the pure-translation branch at [{dual[0]},{dual[1]},{dual[2]}] is not (Identity, dual)"; }

            // Log's rotation-free branch inverts it exactly, so the two close as EXACT mutual inverses on the
            // pure-translation submanifold.
            var (logReal, logDual) = value.Log();

            if (logReal != FixedVector3.Zero) { return "the rotation-free logarithm's real part is not zero"; }
            if (logDual != Space(
                x: dual[0],
                y: dual[1],
                z: dual[2]
            )) { return "the rotation-free logarithm's dual part is not the dual quaternion's vector part"; }
        }

        foreach (var (real, dual, expected, tolerance) in RigidScrewLadder) {
            var value = FixedRigidTransform.Exp(
                real: Space(
                    x: real[0],
                    y: real[1],
                    z: real[2]
                ),
                dual: Space(
                    x: dual[0],
                    y: dual[1],
                    z: dual[2]
                )
            );

            RigidLanes(
                lanes: lanes,
                value: value
            );

            for (var lane = 0; (lane < 8); ++lane) {
                if (Math.Abs(value: (lanes[lane] - expected[lane])) > tolerance) { return $"lane {lane} of Exp([{real[0]},{real[1]},{real[2]}], [{dual[0]},{dual[1]},{dual[2]}]) is {lanes[lane]}, expected {expected[lane]} within {tolerance}"; }
            }

            // The real part is written out twice in the tree; the two must agree BIT-FOR-BIT.
            if (value.Rotation != FixedQuaternion.Exp(bivector: Space(
                x: real[0],
                y: real[1],
                z: real[2]
            ))) { return "the rigid exponential's real part is not the quaternion exponential of the same bivector"; }
        }

        foreach (var (transform, expectedReal, expectedDual) in RigidLogLadder) {
            var value = RigidOf(lanes: transform);

            var (logReal, logDual) = value.Log();

            if (Math.Abs(value: (logReal.X.Value - expectedReal[0])) > RigidLogTolerance) { return $"the logarithm's real X is {logReal.X.Value}, expected {expectedReal[0]}"; }
            if (Math.Abs(value: (logReal.Y.Value - expectedReal[1])) > RigidLogTolerance) { return $"the logarithm's real Y is {logReal.Y.Value}, expected {expectedReal[1]}"; }
            if (Math.Abs(value: (logReal.Z.Value - expectedReal[2])) > RigidLogTolerance) { return $"the logarithm's real Z is {logReal.Z.Value}, expected {expectedReal[2]}"; }
            if (Math.Abs(value: (logDual.X.Value - expectedDual[0])) > RigidLogTolerance) { return $"the logarithm's dual X is {logDual.X.Value}, expected {expectedDual[0]}"; }
            if (Math.Abs(value: (logDual.Y.Value - expectedDual[1])) > RigidLogTolerance) { return $"the logarithm's dual Y is {logDual.Y.Value}, expected {expectedDual[1]}"; }
            if (Math.Abs(value: (logDual.Z.Value - expectedDual[2])) > RigidLogTolerance) { return $"the logarithm's dual Z is {logDual.Z.Value}, expected {expectedDual[2]}"; }

            RigidLanes(
                value: FixedRigidTransform.Exp(
                    dual: logDual,
                    real: logReal
                ),
                lanes: lanes
            );

            for (var lane = 0; (lane < 8); ++lane) {
                if (Math.Abs(value: (lanes[lane] - transform[lane])) > RigidRoundTripTolerance) { return $"lane {lane} of Exp(T.Log()) is {lanes[lane]}, expected {transform[lane]}"; }
            }
        }

        // Two exact pins on the fused lanes, hand-picked where the fused and the chained formulations answer one raw
        // apart: the axial-slide row's dual Z is the exactly-representable 32768 (the chained quotients answered
        // 32767) and the pure-moment row's dual X is 32769, the single correct rounding of its quantized-input
        // rational (the real closed form's 32768 sits across the input quantization). A reintroduced quotient chain
        // fails here by name.
        {
            var (_, slideDual) = RigidOf(lanes: RigidLogLadder[1].Transform).Log();
            var (_, momentDual) = RigidOf(lanes: RigidLogLadder[2].Transform).Log();

            if (slideDual.Z.Value != 32768L) { return $"the axial-slide row's fused dual Z is {slideDual.Z.Value}, expected exactly 32768"; }
            if (momentDual.X.Value != 32769L) { return $"the pure-moment row's fused dual X is {momentDual.X.Value}, expected exactly 32769"; }
        }

        if (FixedRigidTransform.Exp(
            real: FixedVector3.Zero,
            dual: FixedVector3.Zero
        ) != FixedRigidTransform.Identity) { return "the zero screw did not exponentiate to Identity"; }

        var (identityReal, identityDual) = FixedRigidTransform.Identity.Log();

        if (
            (identityReal != FixedVector3.Zero) ||
            (identityDual != FixedVector3.Zero)
        ) { return "Identity did not log to the zero screw"; }

        // The vector-free W < 0 pole: the returned dual is the dual quaternion's own vector part, which for a transform
        // whose Translation reads back as t is MINUS t/2.
        var pole = new FixedRigidTransform(Value: new(
            Real: new FixedQuaternion(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero,
                W: Raw(value: -65536L)
            ),
            Dual: new FixedQuaternion(
                X: Raw(value: 65536L),
                Y: Raw(value: -32768L),
                Z: Raw(value: 131072L),
                W: FixedQ4816.Zero
            )
        ));

        var (poleReal, poleDual) = pole.Log();
        var poleTranslation = pole.Translation;

        if (poleReal != FixedVector3.Zero) { return "the vector-free pole's logarithm has a non-zero real part"; }
        if (poleDual != Space(
            x: 65536L,
            y: -32768L,
            z: 131072L
        )) { return "the vector-free pole's logarithm is not the dual quaternion's vector part"; }
        if (poleTranslation != Space(
            x: -131072L,
            y: 65536L,
            z: -262144L
        )) { return "the vector-free pole's translation is not minus twice its dual vector part"; }

        return null;
    }
    /// <summary>Proves the screw interpolation's endpoints, its shape against a hand-derived ladder, its monotone
    /// traversal, both threshold witnesses, the pure-translation blend, the shortest-path flip, and that the screw arm
    /// is the documented identity assembled from the public members.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RigidScLerpEndpointsAndScrew() {
        var target = FixedRigidTransform.FromRotationTranslation(
            rotation: QuaternionOf(lanes: RigidUnitRotors[3].Lanes),
            translation: Space(
                x: 131072L,
                y: 0L,
                z: 0L
            )
        );
        var identity = FixedRigidTransform.Identity;

        if (FixedRigidTransform.ScLerp(
            from: identity,
            to: target,
            amount: FixedQ4816.Zero
        ) != identity.Normalize()) { return "the interpolation at zero is not the normalized start"; }

        var previousToStart = long.MaxValue;
        var previousToEnd = long.MinValue;
        var previousChord = long.MinValue;
        Span<long> lanes = stackalloc long[8];

        foreach (var (amountRaw, expected, expectedChord) in RigidScLerpLadder) {
            var amount = Raw(value: amountRaw);
            var value = FixedRigidTransform.ScLerp(
                amount: amount,
                from: identity,
                to: target
            );

            RigidLanes(
                lanes: lanes,
                value: value
            );

            for (var lane = 0; (lane < 8); ++lane) {
                if (Math.Abs(value: (lanes[lane] - expected[lane])) > RigidScLerpTolerance) { return $"lane {lane} at amount {amountRaw} is {lanes[lane]}, expected {expected[lane]} within {RigidScLerpTolerance}"; }
            }

            var toStart = FixedQuaternion.Dot(
                left: value.Rotation,
                right: identity.Rotation
            ).Value;
            var toEnd = FixedQuaternion.Dot(
                left: value.Rotation,
                right: target.Rotation
            ).Value;
            var chord = value.Translation.X.Value;

            if (toStart > previousToStart) { return $"the dot against the start rose at amount {amountRaw}"; }
            if (toEnd < previousToEnd) { return $"the dot against the end fell at amount {amountRaw}"; }
            if (chord < previousChord) { return $"the swept chord fell at amount {amountRaw}"; }
            if (Math.Abs(value: (chord - expectedChord)) > RigidScLerpTolerance) { return $"the swept chord at amount {amountRaw} is {chord}, expected {expectedChord}"; }

            previousToStart = toStart;
            previousToEnd = toEnd;
            previousChord = chord;

            // The screw arm IS from · Exp(amount · Log(from⁻¹ · to)), normalized: assembled here from the public
            // members on the same operands.
            var (logReal, logDual) = (identity.Inverse() * target).Log();
            var assembled = (identity * FixedRigidTransform.Exp(
                dual: (logDual * amount),
                real: (logReal * amount)
            )).Normalize();

            if (value != assembled) { return $"the screw arm at amount {amountRaw} is not the documented identity assembled from the public members"; }
        }

        var endpoint = FixedRigidTransform.ScLerp(
            from: identity,
            to: target,
            amount: FixedQ4816.One
        );

        RigidLanes(
            lanes: lanes,
            value: endpoint
        );

        Span<long> normalizedTarget = stackalloc long[8];

        RigidLanes(
            value: target.Normalize(),
            lanes: normalizedTarget
        );

        for (var lane = 0; (lane < 8); ++lane) {
            if (Math.Abs(value: (lanes[lane] - normalizedTarget[lane])) > RigidScLerpTolerance) { return $"the interpolation at one is {lanes[lane]} on lane {lane}, not the normalized end {normalizedTarget[lane]}"; }
        }

        // Both gate arms, supplied at the threshold the source itself computes. Which arm ran is not observable through
        // the public surface; what IS pinned is the two dots, exactly, and that both arms return unit transforms.
        var screwWitness = new FixedRigidTransform(Value: new(
            Real: new FixedQuaternion(
                X: Raw(value: 512L),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero,
                W: Raw(value: 65534L)
            ),
            Dual: new FixedQuaternion(
                X: Raw(value: 32768L),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero,
                W: FixedQ4816.Zero
            )
        ));
        var blendWitness = new FixedRigidTransform(Value: new(
            Real: new FixedQuaternion(
                X: Raw(value: 512L),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero,
                W: Raw(value: 65535L)
            ),
            Dual: new FixedQuaternion(
                X: Raw(value: 32768L),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero,
                W: FixedQ4816.Zero
            )
        ));

        if (65534L != FixedQuaternion.Dot(
            left: identity.Rotation,
            right: screwWitness.Rotation
        ).Value) { return "the screw-arm witness does not sit exactly at the threshold"; }
        if (65535L != FixedQuaternion.Dot(
            left: identity.Rotation,
            right: blendWitness.Rotation
        ).Value) { return "the blend-arm witness does not sit exactly one raw above the threshold"; }

        var half = Raw(value: 32768L);

        if (BigInteger.Abs(value: (RigidRealNorm(value: FixedRigidTransform.ScLerp(
            amount: half,
            from: identity,
            to: screwWitness
        )) - OneRaw)) > QuaternionUnitTolerance) { return "the screw arm did not return a unit transform"; }
        if (BigInteger.Abs(value: (RigidRealNorm(value: FixedRigidTransform.ScLerp(
            amount: half,
            from: identity,
            to: blendWitness
        )) - OneRaw)) > QuaternionUnitTolerance) { return "the blend arm did not return a unit transform"; }

        // The blend arm's own claim: two transforms sharing one rotation interpolate to the componentwise Lerp of their
        // translations, which is what "exact for pure translations" means.
        var fromPure = FixedRigidTransform.FromRotationTranslation(
            rotation: FixedQuaternion.Identity,
            translation: Space(
                x: 65536L,
                y: 0L,
                z: 0L
            )
        );
        var toPure = FixedRigidTransform.FromRotationTranslation(
            rotation: FixedQuaternion.Identity,
            translation: Space(
                x: 262144L,
                y: 131072L,
                z: 0L
            )
        );

        // The shortest-path flip names ONE rigid motion: a rotation dot below zero and its negation reach the same
        // transform.
        var quarter = FixedRigidTransform.FromRotationTranslation(
            rotation: new FixedQuaternion(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.Zero,
                Z: Raw(value: 46341L),
                W: Raw(value: 46341L)
            ),
            translation: Space(
                x: 131072L,
                y: 0L,
                z: 0L
            )
        );
        var negated = new FixedRigidTransform(Value: new(
            Real: -quarter.Value.Real,
            Dual: -quarter.Value.Dual
        ));

        if (FixedQuaternion.Dot(
            left: identity.Rotation,
            right: negated.Rotation
        ).Value >= 0L) { return "the flip witness does not reach the shortest-path branch"; }

        Span<long> flipped = stackalloc long[8];

        foreach (var (amountRaw, _, _) in RigidScLerpLadder) {
            var amount = Raw(value: amountRaw);
            var blended = FixedRigidTransform.ScLerp(
                amount: amount,
                from: fromPure,
                to: toPure
            ).Translation;
            var expectedBlend = FixedVector3.Lerp(
                from: fromPure.Translation,
                to: toPure.Translation,
                amount: amount
            );

            if (Math.Abs(value: (blended.X.Value - expectedBlend.X.Value)) > RigidBlendTolerance) { return $"the pure-translation blend's X at amount {amountRaw} is {blended.X.Value}, expected {expectedBlend.X.Value}"; }
            if (Math.Abs(value: (blended.Y.Value - expectedBlend.Y.Value)) > RigidBlendTolerance) { return $"the pure-translation blend's Y at amount {amountRaw} is {blended.Y.Value}, expected {expectedBlend.Y.Value}"; }
            if (Math.Abs(value: (blended.Z.Value - expectedBlend.Z.Value)) > RigidBlendTolerance) { return $"the pure-translation blend's Z at amount {amountRaw} is {blended.Z.Value}, expected {expectedBlend.Z.Value}"; }

            RigidLanes(
                value: FixedRigidTransform.ScLerp(
                    amount: amount,
                    from: identity,
                    to: quarter
                ),
                lanes: lanes
            );
            RigidLanes(
                value: FixedRigidTransform.ScLerp(
                    amount: amount,
                    from: identity,
                    to: negated
                ),
                lanes: flipped
            );

            for (var lane = 0; (lane < 8); ++lane) {
                if (Math.Abs(value: (lanes[lane] - flipped[lane])) > RigidScLerpTolerance) { return $"a dual quaternion and its negation named different motions at lane {lane}, amount {amountRaw}"; }
            }
        }

        return null;
    }

    // T2 — the exactly-unit rotors, and the ONLY ones the exact rigid ladders use. Each carries a single non-zero lane
    // of magnitude 2¹⁶, which is what makes it a bit-for-bit fixed point of normalization: DirectionShift(2¹⁶) is 29,
    // the preconditioned lane is 2⁴⁵, the squared sum is 2⁹⁰, the denominator is √(2⁹⁰·2³²) = 2⁶¹ exactly, and
    // 2⁴⁵·2³²/2⁶¹ is 2¹⁶ with zero remainder. A quarter turn's (0, 0, 46341, 46341) has squared sum 4294976562 ≠ 2³²
    // and so moves under normalization; no other rotor is claimed exact anywhere in this family.
    //
    // Source and Sign carry the hand-derived map of t·q̂ for each rotor, from the Hamilton table i² = j² = k² = ijk = −1:
    //   q = 1: (tx, ty, tz, 0)        q = i: (0, tz, −ty, −tx)
    //   q = j: (−tz, 0, tx, −ty)      q = k: (ty, −tx, 0, −tz)
    // and the four negations flip every sign. A Source of −1 names the lane that is identically zero.
    private static readonly (long[] Lanes, int[] Source, int[] Sign)[] RigidUnitRotors = [
        ([0L, 0L, 0L, 65536L], [0, 1, 2, -1], [1, 1, 1, 0]),
        ([65536L, 0L, 0L, 0L], [-1, 2, 1, 0], [0, 1, -1, -1]),
        ([0L, 65536L, 0L, 0L], [2, -1, 0, 1], [-1, 0, 1, -1]),
        ([0L, 0L, 65536L, 0L], [1, 0, -1, 2], [1, -1, 0, -1]),
        ([0L, 0L, 0L, -65536L], [0, 1, 2, -1], [-1, -1, -1, 0]),
        ([-65536L, 0L, 0L, 0L], [-1, 2, 1, 0], [0, -1, 1, 1]),
        ([0L, -65536L, 0L, 0L], [2, -1, 0, 1], [1, 0, -1, 1]),
        ([0L, 0L, -65536L, 0L], [1, 0, -1, 2], [-1, 1, 0, 1]),
    ];
    // T3 — the translation ladder. EVEN raws only, so the encoding's halving by ½ is exact and the whole chain stays
    // exact integer arithmetic: the zero, one raw-scale pair, a whole unit on one axis, a mixed sign triple, and one
    // pair at 2⁴⁶ where the doubled translation approaches the carrier.
    private static readonly long[][] RigidTranslationLadder = [
        [0L, 0L, 0L],
        [2L, 0L, 0L],
        [0L, 131072L, 0L],
        [-65536L, 65536L, 131072L],
        [(1L << 46), -(1L << 46), 2L],
    ];
    // The power-of-two rescalings the rotor's scale freedom is measured against; the largest keeps 2¹⁶ << 10 well
    // inside the normalizer's preconditioning band.
    private static readonly int[] RigidRotationScaleShifts = [1, 3, 7, 10];
    // The degenerate inputs FromDualQuaternion must refuse: an all-zero real block against a zero dual, a saturated
    // dual, and each single dual lane in turn — so a predicate that also read the dual block would fail here.
    private static readonly long[][] RigidDegenerateDualLadder = [
        [0L, 0L, 0L, 0L],
        [long.MaxValue, long.MinValue, long.MaxValue, long.MinValue],
        [65536L, 0L, 0L, 0L],
        [0L, 65536L, 0L, 0L],
        [0L, 0L, 65536L, 0L],
        [0L, 0L, 0L, 65536L],
    ];
    // Nondegenerate rotation-scale dual quaternions: the unit rotor, a quarter turn carrying a translation, an
    // off-unit rotor, and one whose real block has a single tiny lane.
    private static readonly long[][] RigidNondegenerateLadder = [
        [0L, 0L, 0L, 65536L, 65536L, 0L, 0L, 0L],
        [0L, 0L, 46341L, 46341L, 0L, -46341L, 0L, 0L],
        [16384L, -8192L, 32768L, 49152L, 1024L, 2048L, -4096L, 512L],
        [0L, 0L, 0L, 1L, 65536L, 65536L, 65536L, 0L],
    ];
    // The same unit transform (Real (0,0,0,2¹⁶), Dual (2¹⁶,0,0,0)) scaled by three, by 2⁻⁴ and by 2¹⁵. Each scale is a
    // ratio the normalizer's power-of-two preconditioner divides straight back out, so all three repair to the base
    // transform.
    private static readonly long[][] RigidRotationScaleLadder = [
        [0L, 0L, 0L, 196608L, 196608L, 0L, 0L, 0L],
        [0L, 0L, 0L, 4096L, 4096L, 0L, 0L, 0L],
        [0L, 0L, 0L, 2147483648L, 2147483648L, 0L, 0L, 0L],
    ];
    // The pure-translation generators Exp's exact branch is measured at: the zero, a unit slide, a mixed triple, and one
    // pair at the carrier's extremes, where the branch still copies rather than computes.
    private static readonly long[][] RigidPureTranslationLadder = [
        [0L, 0L, 0L],
        [65536L, 0L, 0L],
        [-131072L, 65536L, 98304L],
        [long.MaxValue, long.MinValue, 1L],
    ];
    // T4 — the screw ladder, hand-derived from the closed form over the reals at the value each raw denotes:
    //   Real = (û·sin θ, cos θ),  Dual = (v·(sin θ/θ) + û·(d/2)·(cos θ − sin θ/θ),  −(d/2)·sin θ)
    // with θ = ‖real‖, û the unit axis and d/2 = û·v. Rows 1-3 take the exact pure-translation branch and carry NO
    // tolerance. The rest carry twelve, derived rather than fudged: ≤1 raw from the axis normalization, 0.51 ULP from
    // the sine/cosine table, ≤1 each from the sin θ/θ and û·v quotients and one fused rounding — four for the real
    // lanes, and twelve for the dual lanes, where the slide difference carries two of those factors.
    private static readonly (long[] Real, long[] Dual, long[] Expected, long Tolerance)[] RigidScrewLadder = [
        ([0L, 0L, 0L], [0L, 0L, 0L], [0L, 0L, 0L, 65536L, 0L, 0L, 0L, 0L], 0L),
        ([0L, 0L, 0L], [65536L, 0L, 0L], [0L, 0L, 0L, 65536L, 65536L, 0L, 0L, 0L], 0L),
        ([0L, 0L, 0L], [-131072L, 65536L, 98304L], [0L, 0L, 0L, 65536L, -131072L, 65536L, 98304L, 0L], 0L),
        ([0L, 0L, 51472L], [0L, 0L, 0L], [0L, 0L, 46341L, 46341L, 0L, 0L, 0L, 0L], 12L),
        ([0L, 0L, 51472L], [0L, 0L, 32768L], [0L, 0L, 46341L, 46341L, 0L, 0L, 23170L, -23171L], 12L),
        ([0L, 0L, 51472L], [32768L, 0L, 0L], [0L, 0L, 46341L, 46341L, 29502L, 0L, 0L, 0L], 12L),
        ([102944L, 0L, 0L], [0L, 65536L, 0L], [65536L, 0L, 0L, 0L, 0L, 41721L, 0L, 0L], 12L),
        ([29712L, 29712L, 29712L], [1024L, -2048L, 1024L], [26751L, 26751L, 26751L, 46347L, 922L, -1844L, 922L, 0L], 12L),
        ([0L, 0L, 411774L], [0L, 0L, 65536L], [0L, 0L, -1L, 65536L, 0L, 0L, 65536L, 1L], 12L),
    ];
    // T5 — the logarithm ladder: the screw ladder's rows 4-8 replayed backwards. The input is the transform the closed
    // form produces for that row, taken as a literal eight-raw constant, and the expectation is that row's own screw.
    // Every row keeps ‖Real.vector‖ at or above 4096 raw — the floor below which the arctangent-over-norm division stops
    // carrying information.
    private static readonly (long[] Transform, long[] ExpectedReal, long[] ExpectedDual)[] RigidLogLadder = [
        ([0L, 0L, 46341L, 46341L, 0L, 0L, 0L, 0L], [0L, 0L, 51472L], [0L, 0L, 0L]),
        ([0L, 0L, 46341L, 46341L, 0L, 0L, 23170L, -23171L], [0L, 0L, 51472L], [0L, 0L, 32768L]),
        ([0L, 0L, 46341L, 46341L, 29502L, 0L, 0L, 0L], [0L, 0L, 51472L], [32768L, 0L, 0L]),
        ([65536L, 0L, 0L, 0L, 0L, 41721L, 0L, 0L], [102944L, 0L, 0L], [0L, 65536L, 0L]),
        ([26751L, 26751L, 26751L, 46347L, 922L, -1844L, 922L, 0L], [29712L, 29712L, 29712L], [1024L, -2048L, 1024L]),
    ];
    // T6 — the screw interpolation ladder, from Identity to the half turn about z carrying the translation (2, 0, 0) in
    // world units. Hand-derived from the real screw: at parameter t the rotation is the half-angle t·π/2, so the
    // transform is ((0, 0, sin(tπ/2), cos(tπ/2)), (0, −sin(tπ/2), 0, 0)) and the translation the half turn sweeps is
    // (1 − cos(tπ), −sin(tπ), 0) in world units — the chord, which is what the monotone leg reads.
    private static readonly (long AmountRaw, long[] Expected, long Chord)[] RigidScLerpLadder = [
        (0L, [0L, 0L, 0L, 65536L, 0L, 0L, 0L, 0L], 0L),
        (16384L, [0L, 0L, 25080L, 60547L, 0L, -25080L, 0L, 0L], 19195L),
        (32768L, [0L, 0L, 46341L, 46341L, 0L, -46341L, 0L, 0L], 65536L),
        (49152L, [0L, 0L, 60547L, 25080L, 0L, -60547L, 0L, 0L], 111877L),
        (65536L, [0L, 0L, 65536L, 0L, 0L, -65536L, 0L, 0L], 131072L),
    ];
    // The geometric point ladder, hand-derived from the rigid motion each row denotes in real three-space. Every rotor
    // is a half turn — a pure sign map on the sandwich, with no rounding — and every translation is even, so every
    // expectation is an exact integer.
    private static readonly (long[] Rotor, long[] Translation, long[] Point, long[] Expected)[] RigidPointLadder = [
        ([65536L, 0L, 0L, 0L], [0L, 0L, 0L], [65536L, 0L, 0L], [65536L, 0L, 0L]),
        ([65536L, 0L, 0L, 0L], [2L, 0L, 0L], [0L, 65536L, 0L], [2L, -65536L, 0L]),
        ([0L, 65536L, 0L, 0L], [0L, 131072L, 0L], [0L, 0L, 65536L], [0L, 131072L, -65536L]),
        ([0L, 0L, 65536L, 0L], [-65536L, 65536L, 131072L], [131072L, -65536L, 32768L], [-196608L, 131072L, 163840L]),
        ([0L, 65536L, 0L, 0L], [(1L << 46), -(1L << 46), 2L], [65536L, 0L, 0L], [((1L << 46) - 65536L), -(1L << 46), 2L]),
        ([0L, 0L, 65536L, 0L], [0L, 0L, 0L], [131072L, -65536L, 32768L], [-131072L, 65536L, 32768L]),
    ];

    // The declared bands. The dual band is DERIVED per operand at its use site from the returned real block's exact norm
    // defect and the scaled input dual — see the derivation there — and this floor is only what absorbs the three
    // roundings that do not grow with the operands. The projection factor is what "observed doing work" means: the
    // residual must fall by at least this much.
    private const long RigidDualBandFloor = 1024L;

    // The four-square sum of an exactly unit real block, against which the returned block's defect is measured.
    private static readonly BigInteger RigidUnitNorm = (BigInteger.One << (FixedQ4816.FractionBitCount * 2));

    private const long RigidProjectionFactor = 1024L;
    private const long RigidChainDriftFloor = 8L;
    // Log's lanes are single-rounded: the bound is that closing 0.5 plus the arctangent's half-raw carried through
    // the ladder's theta-sensitivity, at most 65536/s <= 16 at the 4096-raw sine floor, so 8.5 worst-case; every
    // ladder row measures within 1.
    private const long RigidLogTolerance = 8L;
    // The trip's budget is Exp's own: the screw ladder derives 12 for its dual lanes, and the fused Log adds one
    // rounding; every ladder row measures within 1.
    private const long RigidRoundTripTolerance = 24L;
    private const long RigidScLerpTolerance = 96L;
    private const long RigidBlendTolerance = 8L;

}
