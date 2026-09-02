using System.Buffers;
using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- phase 3: the declared second kernel, and the integral homology it makes readable ----

    // The minimal triangulation of the real projective plane — six vertices, fifteen edges, ten triangles, every edge
    // in exactly two triangles. It is the smallest simplicial complex whose integral homology carries torsion, and
    // that torsion (H_1 is Z/2) is a fact of topology that nothing in this file computes: it is the oracle.
    private static readonly int[][] ProjectivePlaneFaces = [
        [0, 1, 2], [0, 2, 3], [0, 3, 4], [0, 4, 5], [0, 5, 1],
        [1, 2, 4], [2, 3, 5], [3, 4, 1], [4, 5, 2], [5, 1, 3],
    ];
    // Elementary-divisor forms a reader can check by hand, each one for a different reason: the gcd of the entries is
    // the first divisor, the product of all of them is the determinant's magnitude, and a diagonal matrix does NOT
    // already carry its own Smith form unless its entries divide one another.
    private static readonly (string Name, int Rows, int Columns, long[] Entries, long[] Divisors)[] KnownSmithForms = [
        ("the two-by-two identity", 2, 2, [1, 0, 0, 1], [1, 1]),
        ("diag(2, 3), coprime, so the chain starts at one", 2, 2, [2, 0, 0, 3], [1, 6]),
        ("diag(6, 10), whose entries share a two", 2, 2, [6, 0, 0, 10], [2, 30]),
        ("a two-by-two of determinant minus two", 2, 2, [1, 2, 3, 4], [1, 2]),
        ("a rank-one matrix of twos", 2, 2, [2, 2, 2, 2], [2]),
        ("a coprime row vector", 1, 3, [6, 10, 15], [1]),
        ("diag(4, 6, 9), a three-term chain", 3, 3, [4, 0, 0, 0, 6, 0, 0, 0, 9], [1, 6, 36]),
        ("a three-by-three of determinant minus 144", 3, 3, [2, 4, 4, -6, 6, 12, 10, -4, -16], [2, 6, 12]),
        ("a symmetric three-by-three of determinant 2160", 3, 3, [9, -36, 30, -36, 192, -180, 30, -180, 180], [3, 12, 60]),
        ("the zero matrix, of rank zero", 2, 3, [0, 0, 0, 0, 0, 0], []),
    ];

    // The magnitude ceiling the growth family is reduced under, and the one it is refused under. The first is the
    // largest the entry admits; the second is well below the peak every member of the family reaches.
    private const int SmithWideCeiling = 65536;
    private const int SmithTightCeiling = 256;

    /// <summary>Proves the elementary-divisor certificate on every swept draw: the triple re-multiplies to the
    /// diagonal, both transforms invert over the integers on both sides, the diagonal is a divisibility chain, and the
    /// divisors themselves match the classical gcd-of-minors invariants — an oracle that shares no step with the
    /// reduction.</summary>
    /// <returns>The swept claim.</returns>
    public static Func<long[], long[], string?> SmithCertificateRemultiplies() =>
        static (left, right) => (SmithDraw(
            name: "left",
            raws: left
        ) ?? SmithDraw(
            name: "right",
            raws: right
        ));
    /// <summary>Proves the hand-checkable elementary-divisor forms, each against the gcd-of-minors oracle as well as
    /// against its written-out answer.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SmithKnownForms() {
        foreach (var (name, rows, columns, raw, expected) in KnownSmithForms) {
            var entries = new BigInteger[raw.Length];

            for (var index = 0; (index < raw.Length); ++index) { entries[index] = raw[index]; }

            if (!SmithNormalForm.TryReduce(
                columnCount: columns,
                entries: entries,
                form: out var form,
                magnitudeBits: SmithWideCeiling,
                obstruction: out var refusal,
                rowCount: rows
            )) {
                return $"{name}: refused at stage {refusal.Stage} with {refusal.MagnitudeBits} bits after {refusal.StepsTaken} step(s)";
            }

            if (form.Rank != expected.Length) { return $"{name}: rank {form.Rank} where {expected.Length} was written out"; }

            for (var index = 0; (index < expected.Length); ++index) {
                if (form.Divisors[index] != expected[index]) { return $"{name}: divisor {index} is {form.Divisors[index]} where {expected[index]} was written out"; }
            }

            if (
                (form.RowCount != rows) ||
                (form.ColumnCount != columns)
            ) { return $"{name}: the reduction reports {form.RowCount} by {form.ColumnCount}"; }

            if (SmithCertificate(
                columns: columns,
                entries: entries,
                form: form,
                name: name,
                rows: rows
            ) is { } broken) { return broken; }

            if (SmithDivisorOracle(
                columns: columns,
                entries: entries,
                form: form,
                maximumSize: int.MaxValue,
                name: name,
                rows: rows
            ) is { } disagreed) { return disagreed; }
        }

        return null;
    }
    /// <summary>Proves the magnitude ceiling is load-bearing rather than decorative: a ten-by-ten matrix of single-digit
    /// entries drives intermediate coefficients into the thousands of bits, the ceiling refuses that reduction where it
    /// is set low and answers where it is set high, and the smallest-pivot rule stays under a first-nonzero rule on
    /// nearly every member of the family.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The comparison is deliberately conservative: the shipped peak covers the working matrix AND all four
    /// transforms, while the foil watches only its own working matrix, so a win here is a win against a handicap.</remarks>
    public static string? SmithGrowthBounded() {
        var largest = 0;
        var wins = 0;

        for (var seed = 1; (seed <= 8); ++seed) {
            var entries = GrowthFamily(
                order: 10,
                seed: seed
            );

            if (!SmithNormalForm.TryReduce(
                columnCount: 10,
                entries: entries,
                form: out var form,
                magnitudeBits: SmithWideCeiling,
                obstruction: out var refusal,
                rowCount: 10
            )) {
                return $"growth seed {seed}: refused at stage {refusal.Stage} with {refusal.MagnitudeBits} bits";
            }

            // The surface's own certificate is re-run here; the shared-nothing re-multiplication is left to the swept
            // case and the known forms, because at kilobit entries it buys nothing the ten-by-ten peak does not already
            // cost, and this case is about the BOUND rather than about the triple.
            if (!form.Verify()) { return $"growth seed {seed}: the reduction's certificate does not re-verify"; }

            largest = Math.Max(
                val1: largest,
                val2: form.PeakMagnitudeBits
            );

            if (form.PeakMagnitudeBits < NaivePivotPeak(
                order: 10,
                source: entries
            )) { ++wins; }
        }

        // A floor a third below the SMALLEST peak the family has been observed at (2076 bits; the largest is 47435), so
        // a real regression fails the case and sampling drift cannot: the entries are bounded by nine, and the
        // reduction still needs kilobit integers. The floor was 1024, which is half below that smallest peak rather
        // than a third, so the comment and the number disagreed.
        if (largest < 1384) { return $"the growth family peaked at {largest} bits, so coefficient growth is not being exercised and the ceiling is decorative"; }

        if (wins < 5) { return $"the smallest-pivot rule stayed under the first-nonzero rule on only {wins} of 8 members"; }

        // The ceiling refuses the same matrix it answers at a wider one: the guarantee shrinks, the answer never
        // changes, and the refusal names a magnitude actually above the bound.
        var tight = GrowthFamily(
            order: 10,
            seed: 1
        );

        if (SmithNormalForm.TryReduce(
            columnCount: 10,
            entries: tight,
            form: out _,
            magnitudeBits: SmithTightCeiling,
            obstruction: out var stopped,
            rowCount: 10
        )) {
            return "a reduction whose peak is thousands of bits was admitted under a 256-bit ceiling";
        }

        if (stopped.MagnitudeBits <= SmithTightCeiling) { return $"the refusal reports {stopped.MagnitudeBits} bits, which is not above the {SmithTightCeiling}-bit ceiling it broke"; }

        if (stopped.StepsTaken <= 0L) { return "the refusal reports no steps taken, so it did not reach the ceiling by working"; }

        if (
            (stopped.Stage < 0) ||
            (stopped.Stage > 10)
        ) { return $"the refusal reports stage {stopped.Stage}, which names no diagonal position of a ten-by-ten matrix"; }

        // A declared entry above the ceiling is refused before any work at all, which is the same bound applied to the
        // input rather than only to what the reduction makes of it.
        BigInteger[] oversized = [(BigInteger.One << 300), BigInteger.One, BigInteger.One, BigInteger.One];

        if (SmithNormalForm.TryReduce(
            columnCount: 2,
            entries: oversized,
            form: out _,
            magnitudeBits: 64,
            obstruction: out var declared,
            rowCount: 2
        )) {
            return "a matrix whose declared entry needs 301 bits was admitted under a 64-bit ceiling";
        }

        if (
            (0 != declared.Stage) ||
            (0L != declared.StepsTaken) ||
            (301 != declared.MagnitudeBits)
        ) {
            return $"the declared-entry refusal reports stage {declared.Stage}, {declared.StepsTaken} step(s) and {declared.MagnitudeBits} bits";
        }

        return SmithLimitsRefuse();
    }
    /// <summary>Proves the integral homology of a complex: the Betti numbers and the torsion coefficients read off the
    /// elementary divisors, against a torsion the machinery does not compute, against the Euler characteristic taken
    /// three independent ways, and against the field homology of the same complex over two field materials.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? HomologyTorsionAndBetti() =>
        (ProjectivePlaneHomology() ?? (TorsionFreeHomology() ?? HomologyLimitsRefuse()));

    // The real projective plane: the row's whole point, since it is where the free part and the torsion part disagree
    // and where a field material and the integers give different answers for a reason.
    private static string? ProjectivePlaneHomology() {
        var (dimensions, incidences) = SimplicialComplex(topFaces: ProjectivePlaneFaces);
        var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
            dimensions: dimensions,
            incidences: incidences,
            material: default
        );

        if (
            (6 != calculus.CellsOfDegree(degree: 0).Length) ||
            (15 != calculus.CellsOfDegree(degree: 1).Length) ||
            (10 != calculus.CellsOfDegree(degree: 2).Length)
        ) {
            return "the projective plane's triangulation is not six vertices, fifteen edges and ten triangles";
        }

        if (2 != calculus.Dimension) { return $"the projective plane's triangulation reports dimension {calculus.Dimension}"; }

        foreach (var cell in calculus.CellsOfDegree(degree: 2)) {
            if (2 != calculus.CellDimension(cell: cell)) { return $"cell {cell} is graded two but declares dimension {calculus.CellDimension(cell: cell)}"; }
        }

        if (!IntegerHomology.TryCompute(
            calculus: calculus,
            magnitudeBits: SmithWideCeiling,
            out var homology,
            out var refusal
        )) {
            return $"the projective plane refused: stage {refusal.Stage}, {refusal.MagnitudeBits} bits";
        }

        int[] betti = [1, 0, 0];

        for (var degree = 0; (degree <= 2); ++degree) {
            if (homology.BettiNumber(degree: degree) != betti[degree]) { return $"the projective plane's Betti number in degree {degree} is {homology.BettiNumber(degree: degree)} where topology says {betti[degree]}"; }
        }

        if (
            (0 != homology.Torsion(degree: 0).Length) ||
            (0 != homology.Torsion(degree: 2).Length)
        ) { return "the projective plane carries torsion outside degree one"; }

        var torsion = homology.Torsion(degree: 1);

        if (
            (1 != torsion.Length) ||
            (2 != torsion[0])
        ) {
            return $"the projective plane's first homology carries torsion [{string.Join(
            separator: ", ",
            values: torsion.ToArray()
        )}] where topology says a single two";
        }

        if (1 != homology.EulerCharacteristic) { return $"the projective plane's Euler characteristic from the Betti numbers is {homology.EulerCharacteristic}"; }

        if (
            !calculus.TryEulerCharacteristic(
            characteristic: out var mass,
            obstruction: out _
        ) ||
            (BigInteger.One != mass)
        ) { return $"the Möbius mass reads {mass} where the Betti numbers read one"; }

        if (
            (0 != homology.BoundaryRank(degree: 0)) ||
            (5 != homology.BoundaryRank(degree: 1)) ||
            (10 != homology.BoundaryRank(degree: 2)) ||
            (0 != homology.BoundaryRank(degree: 3))
        ) {
            return $"the boundary ranks are {homology.BoundaryRank(degree: 0)}, {homology.BoundaryRank(degree: 1)}, {homology.BoundaryRank(degree: 2)}, {homology.BoundaryRank(degree: 3)}";
        }

        if (homology.TryBoundary(
            degree: 0,
            form: out _
        )) { return "degree zero carries a reduction, though its boundary operator has no rows"; }

        if (homology.TryBoundary(
            degree: 3,
            form: out _
        )) { return "one degree past the top carries a reduction, though its boundary operator has no columns"; }

        if (!homology.TryBoundary(
            degree: 2,
            form: out var top
        )) { return "the top boundary operator carries no reduction"; }

        if (!top.Verify()) { return "the top boundary operator's certificate does not re-verify"; }

        if (
            (15 != top.RowCount) ||
            (10 != top.ColumnCount) ||
            (10 != top.Rank)
        ) { return $"the top boundary operator reduces to {top.RowCount} by {top.ColumnCount} of rank {top.Rank}"; }

        for (var index = 0; (index < 9); ++index) {
            if (!top.Divisors[index].IsOne) { return $"the top boundary operator's divisor {index} is {top.Divisors[index]} where one was expected"; }
        }

        if (2 != top.Divisors[9]) { return $"the top boundary operator's last divisor is {top.Divisors[9]} where the two of the projective plane's torsion was expected"; }

        // The torsion is load-bearing, measured rather than asserted: over GF(2) the same boundary operator loses
        // exactly one of its rank, so the mod-two Betti numbers are one in every degree where the integral ones are one
        // and zero and zero. The elimination is a plain bit sweep in this file and shares no step with the reduction.
        var lowerEntries = new BigInteger[(6 * 15)];
        var topEntries = new BigInteger[(15 * 10)];

        calculus.BoundaryMatrix(
            degree: 1,
            entries: lowerEntries
        );
        calculus.BoundaryMatrix(
            degree: 2,
            entries: topEntries
        );

        var lowerParity = ParityRank(
            columns: 15,
            entries: lowerEntries,
            rows: 6
        );
        var topParity = ParityRank(
            columns: 10,
            entries: topEntries,
            rows: 15
        );

        if (
            (5 != lowerParity) ||
            (9 != topParity)
        ) { return $"the mod-two ranks are {lowerParity} and {topParity} where five and nine were expected"; }

        int[] parityBetti = [((6 - 0) - lowerParity), ((15 - lowerParity) - topParity), ((10 - topParity) - 0)];

        if (
            (1 != parityBetti[0]) ||
            (1 != parityBetti[1]) ||
            (1 != parityBetti[2])
        ) {
            return $"the mod-two Betti numbers are [{parityBetti[0]}, {parityBetti[1]}, {parityBetti[2]}] where the torsion says one in every degree";
        }

        if (1 != ((parityBetti[0] - parityBetti[1]) + parityBetti[2])) { return "the mod-two Betti numbers do not alternate to the Euler characteristic"; }

        return (FieldTwin(
            dimensions: dimensions,
            homology: homology,
            incidences: incidences,
            name: "the projective plane"
        ) ?? RefusesTightHomology(calculus: calculus));
    }
    // The complexes whose homology is free: the torsion list must be empty in every degree, and the free ranks must
    // alternate to the Euler characteristic the Möbius mass and the cell count already agree on.
    private static string? TorsionFreeHomology() {
        (string Name, int[][] TopFaces, int[] Betti)[] worlds = [
            ("a filled triangle", [[0, 1, 2]], [1, 0, 0]),
            ("the boundary of a tetrahedron", [[0, 1, 2], [0, 1, 3], [0, 2, 3], [1, 2, 3]], [1, 0, 1]),
            ("a circle", [[0, 1], [1, 2], [0, 2]], [1, 1]),
            ("a solid tetrahedron", [[0, 1, 2, 3]], [1, 0, 0, 0]),
            ("two points", [[0], [1]], [2]),
        ];

        foreach (var (name, topFaces, betti) in worlds) {
            var (dimensions, incidences) = SimplicialComplex(topFaces: topFaces);
            var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: dimensions,
                incidences: incidences,
                material: default
            );

            if (!IntegerHomology.TryCompute(
                calculus: calculus,
                magnitudeBits: SmithWideCeiling,
                out var homology,
                out var refusal
            )) {
                return $"{name}: refused at stage {refusal.Stage} with {refusal.MagnitudeBits} bits";
            }

            var alternating = 0;

            for (var degree = 0; (degree <= homology.Dimension); ++degree) {
                if (homology.BettiNumber(degree: degree) != betti[degree]) { return $"{name}: the Betti number in degree {degree} is {homology.BettiNumber(degree: degree)} where {betti[degree]} was expected"; }

                if (0 != homology.Torsion(degree: degree).Length) { return $"{name}: degree {degree} carries torsion, though this complex's homology is free"; }

                var cells = calculus.CellsOfDegree(degree: degree).Length;

                alternating += ((0 == (degree & 1))
                    ? cells
                    : -cells
                );
            }

            if (homology.EulerCharacteristic != alternating) { return $"{name}: the Betti numbers alternate to {homology.EulerCharacteristic} where the cells alternate to {alternating}"; }

            if (
                !calculus.TryEulerCharacteristic(
                characteristic: out var mass,
                obstruction: out _
            ) ||
                (mass != alternating)
            ) { return $"{name}: the Möbius mass reads {mass} where the cells alternate to {alternating}"; }

            if (FieldTwin(
                dimensions: dimensions,
                homology: homology,
                incidences: incidences,
                name: name
            ) is { } split) { return split; }
        }

        return null;
    }
    // The Betti numbers a field material reads through the echelon path, against the ones the elementary divisors give.
    // They agree away from the torsion's characteristic, which is the whole content of reading homology over a field.
    private static string? FieldTwin(string name, int[] dimensions, (int Face, int Coface, int Sign)[] incidences, IntegerHomology homology) {
        var rational = FieldHomology<RealQuadratic, RationalMaterial>.Create(calculus: ExteriorCalculus<RealQuadratic, RationalMaterial>.Create(
            dimensions: dimensions,
            incidences: incidences,
            material: default
        ));
        var prime = FieldHomology<ulong, PrimeFieldMaterial>.Create(calculus: ExteriorCalculus<ulong, PrimeFieldMaterial>.Create(
            dimensions: dimensions,
            incidences: incidences,
            material: new PrimeFieldMaterial(field: PrimeField64.Create(modulus: PrimeFieldModulus))
        ));

        if (
            (rational.Dimension != homology.Dimension) ||
            (prime.Dimension != homology.Dimension)
        ) { return $"{name}: the field readouts disagree with the integral one about the top dimension"; }

        for (var degree = 0; (degree <= homology.Dimension); ++degree) {
            if (rational.BettiNumber(degree: degree) != homology.BettiNumber(degree: degree)) { return $"{name}: the rational Betti number in degree {degree} is {rational.BettiNumber(degree: degree)} against the integral {homology.BettiNumber(degree: degree)}"; }

            if (prime.BettiNumber(degree: degree) != homology.BettiNumber(degree: degree)) { return $"{name}: the prime-field Betti number in degree {degree} is {prime.BettiNumber(degree: degree)} against the integral {homology.BettiNumber(degree: degree)}"; }
        }

        for (var degree = 0; (degree <= (homology.Dimension + 1)); ++degree) {
            if (rational.BoundaryRank(degree: degree) != homology.BoundaryRank(degree: degree)) { return $"{name}: the rational rank in degree {degree} is {rational.BoundaryRank(degree: degree)} against the integral {homology.BoundaryRank(degree: degree)}"; }

            if (prime.BoundaryRank(degree: degree) != homology.BoundaryRank(degree: degree)) { return $"{name}: the prime-field rank in degree {degree} is {prime.BoundaryRank(degree: degree)} against the integral {homology.BoundaryRank(degree: degree)}"; }
        }

        if (
            (rational.EulerCharacteristic != homology.EulerCharacteristic) ||
            (prime.EulerCharacteristic != homology.EulerCharacteristic)
        ) {
            return $"{name}: the field readouts alternate to {rational.EulerCharacteristic} and {prime.EulerCharacteristic} against the integral {homology.EulerCharacteristic}";
        }

        return null;
    }
    // The integral readout's own refusal, reachable through the ceiling: every incidence number is a sign, so the
    // reduction needs two bits and a one-bit ceiling turns it away.
    private static string? RefusesTightHomology(ExteriorCalculus<BigInteger, IntegerMaterial> calculus) {
        if (IntegerHomology.TryCompute(
            calculus: calculus,
            magnitudeBits: 1,
            out _,
            out var refusal
        )) { return "a one-bit ceiling admitted a reduction whose intermediates need two"; }

        return ((refusal.MagnitudeBits > 1)
            ? null
            : $"the tight-ceiling refusal reports {refusal.MagnitudeBits} bits, which is not above the ceiling it broke"
        );
    }
    // One swept draw: a three-by-three matrix from the lane raws, reduced, re-multiplied here, and answered to by the
    // gcd-of-minors invariants.
    private static string? SmithDraw(string name, long[] raws) {
        var entries = new BigInteger[9];

        for (var index = 0; (index < 9); ++index) { entries[index] = raws[index]; }

        if (!SmithNormalForm.TryReduce(
            columnCount: 3,
            entries: entries,
            form: out var form,
            magnitudeBits: SmithWideCeiling,
            obstruction: out var refusal,
            rowCount: 3
        )) {
            return $"{name}: refused at stage {refusal.Stage} with {refusal.MagnitudeBits} bits after {refusal.StepsTaken} step(s)";
        }

        return (SmithCertificate(
            columns: 3,
            entries: entries,
            form: form,
            name: name,
            rows: 3
        ) ?? SmithDivisorOracle(
            columns: 3,
            entries: entries,
            form: form,
            maximumSize: int.MaxValue,
            name: name,
            rows: 3
        ));
    }
    // The certificate, recomputed here rather than taken: both transforms invert on both sides over the integers, the
    // diagonal is a positive divisibility chain, and U times A times V is that diagonal entry for entry.
    private static string? SmithCertificate(string name, BigInteger[] entries, int rows, int columns, SmithNormalForm form) {
        if (!form.Verify()) { return $"{name}: the reduction's own certificate does not re-verify"; }

        for (var index = 0; (index < form.Rank); ++index) {
            if (form.Divisors[index] <= BigInteger.Zero) { return $"{name}: divisor {index} is {form.Divisors[index]}, which is not positive"; }

            if (
                (0 != index) &&
                !BigInteger.Remainder(
                dividend: form.Divisors[index],
                divisor: form.Divisors[(index - 1)]
            ).IsZero
            ) {
                return $"{name}: divisor {(index - 1)} is {form.Divisors[(index - 1)]} and does not divide divisor {index}, {form.Divisors[index]}";
            }
        }

        if (!IsUnimodular(
            inverse: form.LeftInverse,
            order: rows,
            read: form.Left
        )) { return $"{name}: the left transform and its inverse do not multiply to the identity both ways"; }

        if (!IsUnimodular(
            inverse: form.RightInverse,
            order: columns,
            read: form.Right
        )) { return $"{name}: the right transform and its inverse do not multiply to the identity both ways"; }

        var staged = new BigInteger[(rows * columns)];

        for (var row = 0; (row < rows); ++row) {
            for (var column = 0; (column < columns); ++column) {
                var total = BigInteger.Zero;

                for (var middle = 0; (middle < rows); ++middle) {
                    total += (form.Left(
                    column: middle,
                    row: row
                ) * entries[((middle * columns) + column)]);
                }

                staged[((row * columns) + column)] = total;
            }
        }

        for (var row = 0; (row < rows); ++row) {
            for (var column = 0; (column < columns); ++column) {
                var total = BigInteger.Zero;

                for (var middle = 0; (middle < columns); ++middle) {
                    total += (staged[((row * columns) + middle)] * form.Right(
                    column: column,
                    row: middle
                ));
                }

                var expected = (((row == column) && (row < form.Rank))
                    ? form.Divisors[row]
                    : BigInteger.Zero
                );

                if (total != expected) { return $"{name}: U·A·V carries {total} at ({row}, {column}) where the diagonal carries {expected}"; }
            }
        }

        return null;
    }
    // The classical invariants: the product of the first k elementary divisors is the greatest common divisor of all
    // k-by-k minors, and the rank is the largest k whose minors do not all vanish. Neither statement runs any step the
    // reduction runs. Enumerating every subset of a size is exponential in the order, so a caller may bound the sizes
    // it pays for; the rank claim is then withheld rather than asserted from a truncated walk.
    private static string? SmithDivisorOracle(string name, BigInteger[] entries, int rows, int columns, SmithNormalForm form, int maximumSize) {
        var order = Math.Min(
            val1: Math.Min(
                val1: rows,
                val2: columns
            ),
            val2: maximumSize
        );
        var product = BigInteger.One;
        var rank = 0;

        for (var size = 1; (size <= order); ++size) {
            var mass = MinorGreatestCommonDivisor(
                columns: columns,
                entries: entries,
                rows: rows,
                size: size
            );

            if (mass.IsZero) { break; }

            ++rank;

            if (size > form.Rank) { return $"{name}: the {size}-by-{size} minors do not all vanish, though the reduction reports rank {form.Rank}"; }

            product *= form.Divisors[(size - 1)];

            if (product != mass) { return $"{name}: the first {size} divisors multiply to {product} where the {size}-by-{size} minors have greatest common divisor {mass}"; }
        }

        // A walk that stopped at the bound saw every size it paid for and none above, so it makes NO rank claim: the
        // loop above already returns on the first size whose minors survive past the reported rank, so `rank` cannot
        // exceed it here and any comparison written at this point is vacuous. The bound withholds the claim rather
        // than weakening it.
        if (order < Math.Min(
            val1: rows,
            val2: columns
        )) { return null; }

        return ((rank == form.Rank)
            ? null
            : $"{name}: the minors give rank {rank} where the reduction reports {form.Rank}"
        );
    }
    private static bool IsUnimodular(Func<int, int, BigInteger> read, Func<int, int, BigInteger> inverse, int order) {
        for (var row = 0; (row < order); ++row) {
            for (var column = 0; (column < order); ++column) {
                var forward = BigInteger.Zero;
                var backward = BigInteger.Zero;

                for (var middle = 0; (middle < order); ++middle) {
                    backward += (inverse(
                        row,
                        middle
                    ) * read(
                        middle,
                        column
                    ));
                    forward += (read(
                        row,
                        middle
                    ) * inverse(
                        middle,
                        column
                    ));
                }

                var expected = ((row == column)
                    ? BigInteger.One
                    : BigInteger.Zero
                );

                if (
                    (forward != expected) ||
                    (backward != expected)
                ) { return false; }
            }
        }

        return true;
    }
    // The greatest common divisor of every minor of one size, by explicit subset enumeration and cofactor expansion.
    private static BigInteger MinorGreatestCommonDivisor(BigInteger[] entries, int rows, int columns, int size) {
        var mass = BigInteger.Zero;
        var rowChoice = new int[size];
        var columnChoice = new int[size];

        for (var rowMask = 0; (rowMask < (1 << rows)); ++rowMask) {
            if (BitOperations.PopCount(value: ((uint)rowMask)) != size) { continue; }

            Choose(
                count: rows,
                destination: rowChoice,
                mask: rowMask
            );

            for (var columnMask = 0; (columnMask < (1 << columns)); ++columnMask) {
                if (BitOperations.PopCount(value: ((uint)columnMask)) != size) { continue; }

                Choose(
                    count: columns,
                    destination: columnChoice,
                    mask: columnMask
                );

                var minor = new BigInteger[(size * size)];

                for (var row = 0; (row < size); ++row) {
                    for (var column = 0; (column < size); ++column) { minor[((row * size) + column)] = entries[((rowChoice[row] * columns) + columnChoice[column])]; }
                }

                mass = BigInteger.GreatestCommonDivisor(
                    left: mass,
                    right: Determinant(
                        matrix: minor,
                        order: size
                    )
                );
            }
        }

        return mass;
    }
    private static void Choose(int mask, int count, int[] destination) {
        var slot = 0;

        for (var index = 0; (index < count); ++index) {
            if (0 != (mask & (1 << index))) { destination[slot++] = index; }
        }
    }
    private static BigInteger Determinant(BigInteger[] matrix, int order) {
        if (1 == order) { return matrix[0]; }

        var total = BigInteger.Zero;
        var minor = new BigInteger[((order - 1) * (order - 1))];

        for (var drop = 0; (drop < order); ++drop) {
            for (var row = 1; (row < order); ++row) {
                var slot = 0;

                for (var column = 0; (column < order); ++column) {
                    if (column != drop) { minor[(((row - 1) * (order - 1)) + slot++)] = matrix[((row * order) + column)]; }
                }
            }

            var term = (matrix[drop] * Determinant(
                matrix: minor,
                order: (order - 1)
            ));

            total += ((0 == (drop & 1))
                ? term
                : -term
            );
        }

        return total;
    }
    // A dense family with no structure to exploit: single-digit entries from a fixed recurrence, so the same eight
    // matrices are built on every run and every machine.
    private static BigInteger[] GrowthFamily(int order, int seed) {
        var entries = new BigInteger[(order * order)];
        var state = ((ulong)((seed * 2654435761L) + 12345L));

        for (var index = 0; (index < entries.Length); ++index) {
            state = ((state * 6364136223846793005UL) + 1442695040888963407UL);
            entries[index] = ((((long)(state >> 40)) % 19L) - 9L);
        }

        return entries;
    }
    // The foil: the same reduction with the FIRST nonzero entry taken as the pivot instead of the smallest one. It
    // computes no answer, only the largest magnitude its own working matrix reaches.
    private static int NaivePivotPeak(BigInteger[] source, int order) {
        var matrix = source.ToArray();
        var peak = 0;

        void Observe(BigInteger value) {
            var bits = ((int)BigInteger.Abs(value: value).GetBitLength());

            if (bits > peak) { peak = bits; }
        }

        foreach (var entry in matrix) { Observe(value: entry); }

        for (var stage = 0; (stage < order); ++stage) {
            if (!FirstNonzero(
                matrix: matrix,
                order: order,
                pivotColumn: out var pivotColumn,
                pivotRow: out var pivotRow,
                stage: stage
            )) { break; }

            SwapPlainRows(
                first: stage,
                matrix: matrix,
                order: order,
                second: pivotRow
            );
            SwapPlainColumns(
                first: stage,
                matrix: matrix,
                order: order,
                second: pivotColumn
            );

            while (true) {
                var clean = true;

                for (var row = (stage + 1); (row < order); ++row) {
                    if (matrix[((row * order) + stage)].IsZero) { continue; }

                    var quotient = BigInteger.Divide(
                        dividend: matrix[((row * order) + stage)],
                        divisor: matrix[((stage * order) + stage)]
                    );

                    for (var column = 0; (column < order); ++column) {
                        matrix[((row * order) + column)] -= (quotient * matrix[((stage * order) + column)]);

                        Observe(value: matrix[((row * order) + column)]);
                    }

                    if (!matrix[((row * order) + stage)].IsZero) {
                        SwapPlainRows(
                            first: stage,
                            matrix: matrix,
                            order: order,
                            second: row
                        );

                        clean = false;
                    }
                }

                if (!clean) { continue; }

                for (var column = (stage + 1); (column < order); ++column) {
                    if (matrix[((stage * order) + column)].IsZero) { continue; }

                    var quotient = BigInteger.Divide(
                        dividend: matrix[((stage * order) + column)],
                        divisor: matrix[((stage * order) + stage)]
                    );

                    for (var row = 0; (row < order); ++row) {
                        matrix[((row * order) + column)] -= (quotient * matrix[((row * order) + stage)]);

                        Observe(value: matrix[((row * order) + column)]);
                    }

                    if (!matrix[((stage * order) + column)].IsZero) {
                        SwapPlainColumns(
                            first: stage,
                            matrix: matrix,
                            order: order,
                            second: column
                        );

                        clean = false;
                    }
                }

                if (clean) { break; }
            }
        }

        return peak;
    }
    private static bool FirstNonzero(BigInteger[] matrix, int order, int stage, out int pivotRow, out int pivotColumn) {
        for (var row = stage; (row < order); ++row) {
            for (var column = stage; (column < order); ++column) {
                if (!matrix[((row * order) + column)].IsZero) {
                    pivotColumn = column;
                    pivotRow = row;

                    return true;
                }
            }
        }

        pivotColumn = -1;
        pivotRow = -1;

        return false;
    }
    private static void SwapPlainColumns(BigInteger[] matrix, int order, int first, int second) {
        if (first == second) { return; }

        for (var row = 0; (row < order); ++row) {
            (matrix[((row * order) + first)], matrix[((row * order) + second)]) = (matrix[((row * order) + second)], matrix[((row * order) + first)]);
        }
    }
    private static void SwapPlainRows(BigInteger[] matrix, int order, int first, int second) {
        if (first == second) { return; }

        for (var column = 0; (column < order); ++column) {
            (matrix[((first * order) + column)], matrix[((second * order) + column)]) = (matrix[((second * order) + column)], matrix[((first * order) + column)]);
        }
    }
    // The rank of an integer matrix over GF(2), by a plain bit sweep. It answers the question the field materials
    // cannot: no house material presents GF(2) as a field, and the mod-two rank is what makes the torsion visible.
    private static int ParityRank(ReadOnlySpan<BigInteger> entries, int rows, int columns) {
        var bits = new bool[(rows * columns)];

        for (var index = 0; (index < entries.Length); ++index) {
            bits[index] = !BigInteger.Remainder(
            dividend: entries[index],
            divisor: 2
        ).IsZero;
        }

        var rank = 0;

        for (var column = 0; ((column < columns) && (rank < rows)); ++column) {
            var pivot = -1;

            for (var row = rank; ((row < rows) && (pivot < 0)); ++row) {
                if (bits[((row * columns) + column)]) { pivot = row; }
            }

            if (pivot < 0) { continue; }

            for (var index = 0; (index < columns); ++index) {
                (bits[((rank * columns) + index)], bits[((pivot * columns) + index)]) = (bits[((pivot * columns) + index)], bits[((rank * columns) + index)]);
            }

            for (var row = 0; (row < rows); ++row) {
                if (
                    (row == rank) ||
                    !bits[((row * columns) + column)]
                ) { continue; }

                for (var index = 0; (index < columns); ++index) { bits[((row * columns) + index)] ^= bits[((rank * columns) + index)]; }
            }

            ++rank;
        }

        return rank;
    }
    // The declarations the second kernel and the two readouts turn away.
    private static string? SmithLimitsRefuse() =>
        (RefusesDeclaration(
            name: "a matrix with no rows",
            build: static () => _ = SmithNormalForm.TryReduce(
                columnCount: 1,
                entries: [],
                form: out _,
                magnitudeBits: 64,
                obstruction: out _,
                rowCount: 0
            )
        )
            ?? (RefusesDeclaration(
            name: "a matrix past the order cap",
            build: static () => _ = SmithNormalForm.TryReduce(
                entries: new BigInteger[(SmithNormalForm.MaximumOrder + 1)],
                rowCount: (SmithNormalForm.MaximumOrder + 1),
                columnCount: 1,
                magnitudeBits: 64,
                form: out _,
                obstruction: out _
            )
        )
            ?? (RefusesDeclaration(
            name: "a matrix with no columns",
            build: static () => _ = SmithNormalForm.TryReduce(
                columnCount: 0,
                entries: [],
                form: out _,
                magnitudeBits: 64,
                obstruction: out _,
                rowCount: 1
            )
        )
            ?? (RefusesDeclaration(
            name: "a ceiling of no bits",
            build: static () => _ = SmithNormalForm.TryReduce(
                entries: [BigInteger.One],
                rowCount: 1,
                columnCount: 1,
                magnitudeBits: 0,
                form: out _,
                obstruction: out _
            )
        )
            ?? (RefusesDeclaration(
            name: "a ceiling past the cap",
            build: static () => _ = SmithNormalForm.TryReduce(
                entries: [BigInteger.One],
                rowCount: 1,
                columnCount: 1,
                magnitudeBits: 65537,
                form: out _,
                obstruction: out _
            )
        )
            ?? (RefusesDeclaration(
            name: "a matrix whose entries do not fill its shape",
            build: static () => _ = SmithNormalForm.TryReduce(
                entries: [BigInteger.One, BigInteger.One],
                rowCount: 1,
                columnCount: 3,
                magnitudeBits: 64,
                form: out _,
                obstruction: out _
            )
        )
            ?? (RefusesDeclaration(
            name: "a transform coordinate outside the left transform",
            build: static () => {
                _ = SmithNormalForm.TryReduce(
                    entries: [BigInteger.One, BigInteger.One, BigInteger.One, BigInteger.One],
                    rowCount: 2,
                    columnCount: 2,
                    magnitudeBits: 64,
                    form: out var form,
                    obstruction: out _
                );
                _ = form.Left(
                    column: 0,
                    row: 2
                );
            }
        )
            ?? (RefusesDeclaration(
            name: "a transform coordinate outside the inverse of the left transform",
            build: static () => {
                _ = SmithNormalForm.TryReduce(
                    entries: [BigInteger.One, BigInteger.One, BigInteger.One, BigInteger.One],
                    rowCount: 2,
                    columnCount: 2,
                    magnitudeBits: 64,
                    form: out var form,
                    obstruction: out _
                );
                _ = form.LeftInverse(
                    column: -1,
                    row: 0
                );
            }
        )
            ?? (RefusesDeclaration(
            name: "a transform coordinate outside the right transform",
            build: static () => {
                _ = SmithNormalForm.TryReduce(
                    entries: [BigInteger.One, BigInteger.One, BigInteger.One, BigInteger.One],
                    rowCount: 2,
                    columnCount: 2,
                    magnitudeBits: 64,
                    form: out var form,
                    obstruction: out _
                );
                _ = form.Right(
                    column: 2,
                    row: 0
                );
            }
        )
            ?? RefusesDeclaration(
            name: "a transform coordinate outside the inverse of the right transform",
            build: static () => {
                _ = SmithNormalForm.TryReduce(
                    entries: [BigInteger.One, BigInteger.One, BigInteger.One, BigInteger.One],
                    rowCount: 2,
                    columnCount: 2,
                    magnitudeBits: 64,
                    form: out var form,
                    obstruction: out _
                );
                _ = form.RightInverse(
                    column: 0,
                    row: -1
                );
            }
        ))))))))));
    private static string? HomologyLimitsRefuse() =>
        (RefusesDeclaration(
            name: "Betti numbers over a material with no inverses",
            build: static () => _ = FieldHomology<BigInteger, IntegerMaterial>.Create(calculus: ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: [0, 0, 1],
                incidences: [(0, 2, 1), (1, 2, -1)],
                material: default
            ))
        )
            ?? (RefusesDeclaration(
            name: "Betti numbers of no complex at all",
            build: static () => _ = FieldHomology<RealQuadratic, RationalMaterial>.Create(calculus: null!)
        )
            ?? (RefusesDeclaration(
            name: "the integral homology of no complex at all",
            build: static () => _ = IntegerHomology.TryCompute(
                calculus: null!,
                homology: out _,
                magnitudeBits: 64,
                obstruction: out _
            )
        )
            ?? (RefusesDeclaration(
            name: "a boundary matrix written into a span of the wrong size",
            build: static () => {
                var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                    dimensions: [0, 0, 1],
                    incidences: [(0, 2, 1), (1, 2, -1)],
                    material: default
                );

                calculus.BoundaryMatrix(
                    degree: 1,
                    entries: new BigInteger[3]
                );
            }
        )
            ?? (RefusesDeclaration(
            name: "the dimension of a cell outside the complex",
            build: static () => {
                var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                    dimensions: [0, 0, 1],
                    incidences: [(0, 2, 1), (1, 2, -1)],
                    material: default
                );

                _ = calculus.CellDimension(cell: 3);
            }
        )
            ?? (RefusesDeclaration(
            name: "a Betti number of a degree past the top",
            build: static () => {
                _ = IntegerHomology.TryCompute(
                    calculus: ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                        dimensions: [0, 0, 1],
                        incidences: [(0, 2, 1), (1, 2, -1)],
                        material: default
                    ),
                    magnitudeBits: 64,
                    homology: out var homology,
                    obstruction: out _
                );
                _ = homology.BettiNumber(degree: 2);
            }
        )
            ?? (RefusesDeclaration(
            name: "a torsion list of a negative degree",
            build: static () => {
                _ = IntegerHomology.TryCompute(
                    calculus: ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                        dimensions: [0, 0, 1],
                        incidences: [(0, 2, 1), (1, 2, -1)],
                        material: default
                    ),
                    magnitudeBits: 64,
                    homology: out var homology,
                    obstruction: out _
                );
                _ = homology.Torsion(degree: -1);
            }
        )
            ?? (RefusesDeclaration(
            name: "a boundary rank two degrees past the top",
            build: static () => {
                _ = IntegerHomology.TryCompute(
                    calculus: ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                        dimensions: [0, 0, 1],
                        incidences: [(0, 2, 1), (1, 2, -1)],
                        material: default
                    ),
                    magnitudeBits: 64,
                    homology: out var homology,
                    obstruction: out _
                );
                _ = homology.BoundaryRank(degree: 3);
            }
        )
            ?? (RefusesDeclaration(
            name: "a certified reduction two degrees past the top",
            build: static () => {
                _ = IntegerHomology.TryCompute(
                    calculus: ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                        dimensions: [0, 0, 1],
                        incidences: [(0, 2, 1), (1, 2, -1)],
                        material: default
                    ),
                    magnitudeBits: 64,
                    homology: out var homology,
                    obstruction: out _
                );
                _ = homology.TryBoundary(
                    degree: 3,
                    form: out _
                );
            }
        )
            ?? (RefusesDeclaration(
            name: "a field Betti number of a negative degree",
            build: static () => {
                var homology = FieldHomology<RealQuadratic, RationalMaterial>.Create(calculus: ExteriorCalculus<RealQuadratic, RationalMaterial>.Create(
                    dimensions: [0, 0, 1],
                    incidences: [(0, 2, 1), (1, 2, -1)],
                    material: default
                ));

                _ = homology.BettiNumber(degree: -1);
            }
        )
            ?? (RefusesDeclaration(
            name: "a field boundary rank two degrees past the top",
            build: static () => {
                var homology = FieldHomology<RealQuadratic, RationalMaterial>.Create(calculus: ExteriorCalculus<RealQuadratic, RationalMaterial>.Create(
                    dimensions: [0, 0, 1],
                    incidences: [(0, 2, 1), (1, 2, -1)],
                    material: default
                ));

                _ = homology.BoundaryRank(degree: 3);
            }
        )
            // The ceiling is validated whether or not the complex has a boundary operator to run it against: a
            // zero-dimensional complex reduces nothing, and used to accept any number in silence while a complex one
            // dimension up refused the same number.
            ?? (RefusesDeclaration(
            name: "a magnitude ceiling of zero bits at a complex with no boundary operator",
            build: static () => _ = IntegerHomology.TryCompute(
                calculus: ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                    dimensions: [0, 0],
                    incidences: [],
                    material: default
                ),
                magnitudeBits: 0,
                homology: out _,
                obstruction: out _
            )
        )
            ?? (RefusesDeclaration(
            name: "a magnitude ceiling past the widest the reduction admits",
            build: static () => _ = IntegerHomology.TryCompute(
                calculus: ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                    dimensions: [0, 0],
                    incidences: [],
                    material: default
                ),
                magnitudeBits: 65537,
                homology: out _,
                obstruction: out _
            )
        )
            ?? EmptyGradesAnswer())))))))))))));
    // The cap is a number a caller can actually reach: a complex of exactly MaximumCellCount cells and no incidences
    // is built, and its bounded face order carries the 3n + 3 intervals that put it one below the interval cap.
    private static string? AdmitsTheWholeCellCap() {
        var cellCount = ExteriorCalculus<BigInteger, IntegerMaterial>.MaximumCellCount;
        var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
            dimensions: new int[cellCount],
            incidences: [],
            material: default
        );

        return (((((3 * cellCount) + 3) == calculus.Poset.IntervalCount) && (cellCount == calculus.CellCount))
            ? null
            : $"a complex of {cellCount} cells presents {calculus.Poset.IntervalCount} intervals where a bounded face order carries {((3 * cellCount) + 3)}"
        );
    }
    // The two ends of the grading are answered rather than refused: the chain groups below zero and above the top are
    // genuinely zero, and a caller walking a complex should not have to special-case them.
    private static string? EmptyGradesAnswer() {
        var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
            dimensions: [0, 0, 1],
            incidences: [(0, 2, 1), (1, 2, -1)],
            material: default
        );

        if (0 != calculus.CellsOfDegree(degree: -1).Length) { return "the cells below dimension zero are not none"; }

        if (0 != calculus.CellsOfDegree(degree: 2).Length) { return "the cells above the top dimension are not none"; }

        calculus.BoundaryMatrix(
            degree: 0,
            entries: []
        );

        return null;
    }
    // The comparability relation of a complex's bounded face order, closed here rather than taken from the entry that
    // builds the presentation: the bottom is below every cell, every cell is below the top, and a face is below its
    // cofaces.
    private static bool[] ComparabilityClosure(int elementCount, ReadOnlySpan<int> dimensions, ReadOnlySpan<(int Face, int Coface, int Sign)> incidences) {
        var cellCount = dimensions.Length;
        var order = new bool[(elementCount * elementCount)];

        for (var index = 0; (index < elementCount); ++index) { order[((index * elementCount) + index)] = true; }

        for (var cell = 0; (cell < cellCount); ++cell) {
            order[((cellCount * elementCount) + cell)] = true;
            order[(((cell * elementCount) + cellCount) + 1)] = true;
        }

        order[(((cellCount * elementCount) + cellCount) + 1)] = true;

        foreach (var (face, coface, _) in incidences) { order[((face * elementCount) + coface)] = true; }

        for (var middle = 0; (middle < elementCount); ++middle) {
            for (var lower = 0; (lower < elementCount); ++lower) {
                if (!order[((lower * elementCount) + middle)]) { continue; }

                for (var upper = 0; (upper < elementCount); ++upper) {
                    if (order[((middle * elementCount) + upper)]) { order[((lower * elementCount) + upper)] = true; }
                }
            }
        }

        return order;
    }
    // The simplicial complex generated by a list of top faces: every nonempty subset is a cell, cells are ordered by
    // dimension then lexicographically, and a facet enters its coface's boundary with the sign of the position it
    // drops. Nothing here reads the presented algebra — it is the input data, and the alternating sign rule is what
    // makes the chain-complex condition a fact to be measured rather than one to be assumed.
    private static (int[] Dimensions, (int Face, int Coface, int Sign)[] Incidences) SimplicialComplex(int[][] topFaces) {
        var collected = new List<int[]>();

        void Collect(int[] face) {
            collected.Add(item: face);

            if (face.Length < 2) { return; }

            for (var drop = 0; (drop < face.Length); ++drop) {
                Collect(face: Facet(
                drop: drop,
                face: face
            ));
            }
        }

        foreach (var face in topFaces) {
            var sorted = face.ToArray();

            Array.Sort(array: sorted);
            Collect(face: sorted);
        }

        collected.Sort(comparison: CompareFaces);

        var cells = new List<int[]>();

        foreach (var face in collected) {
            if (
                (0 == cells.Count) ||
                (0 != CompareFaces(
                left: cells[(cells.Count - 1)],
                right: face
            ))
            ) { cells.Add(item: face); }
        }

        var dimensions = new int[cells.Count];
        var incidences = new List<(int Face, int Coface, int Sign)>();

        for (var cell = 0; (cell < cells.Count); ++cell) { dimensions[cell] = (cells[cell].Length - 1); }

        for (var coface = 0; (coface < cells.Count); ++coface) {
            var vertices = cells[coface];

            if (vertices.Length < 2) { continue; }

            for (var drop = 0; (drop < vertices.Length); ++drop) {
                incidences.Add(item: (
                    IndexOfFace(
                    cells: cells,
                    face: Facet(
                        drop: drop,
                        face: vertices
                    )
                ),
                    coface,
                    ((0 == (drop & 1))
                    ? 1
                    : -1)
                ));
            }
        }

        return (dimensions, incidences.ToArray());
    }
    private static int CompareFaces(int[] left, int[] right) {
        if (left.Length != right.Length) { return left.Length.CompareTo(value: right.Length); }

        return left.AsSpan().SequenceCompareTo(other: right.AsSpan());
    }
    private static int[] Facet(int[] face, int drop) {
        var smaller = new int[(face.Length - 1)];
        var cursor = 0;

        for (var index = 0; (index < face.Length); ++index) {
            if (index != drop) { smaller[cursor++] = face[index]; }
        }

        return smaller;
    }
    private static int IndexOfFace(List<int[]> cells, int[] face) {
        var low = 0;
        var high = cells.Count;

        while (low < high) {
            var middle = ((low + high) >> 1);
            var comparison = CompareFaces(
                left: cells[middle],
                right: face
            );

            if (0 == comparison) { return middle; }

            if (comparison < 0) { low = (middle + 1); } else { high = middle; }
        }

        return -1;
    }

}
