using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly BigInteger RawLongMin = long.MinValue;
    private static readonly BigInteger RawLongMax = long.MaxValue;

    private static bool FitsRaw(BigInteger value) =>
        ((value >= RawLongMin) && (value <= RawLongMax));
    // Runs a vector operation that may refuse, returning the raws it produced or null when it threw the expected
    // exception type. Any other exception type propagates and fails the sweep by name.
    private static long[]? RawsOrRefusal<TException>(Func<FixedVector3> operation) where TException : Exception {
        try {
            var result = operation();

            return [result.X.Value, result.Y.Value, result.Z.Value];
        } catch (TException) {
            return null;
        }
    }
    // Compares an operation that must refuse exactly when some lane's exact answer leaves the carrier.
    private static string? CheckedLanes<TException>(string name, Func<FixedVector3> operation, BigInteger[] exact) where TException : Exception {
        var refuses = !(FitsRaw(value: exact[0]) && FitsRaw(value: exact[1]) && FitsRaw(value: exact[2]));
        var actual = RawsOrRefusal<TException>(operation: operation);

        if (refuses) {
            return ((actual is null)
                ? null
                : $"{name} answered [{Lanes(raws: actual)}] where a lane's exact value [{string.Join(separator: ", ", values: exact)}] leaves the carrier and must throw {typeof(TException).Name}"
            );
        }

        if (actual is null) {
            return $"{name} threw {typeof(TException).Name} where every lane's exact value [{string.Join(separator: ", ", values: exact)}] is representable";
        }

        for (var lane = 0; (lane < 3); ++lane) {
            if (actual[lane] != ((long)exact[lane])) {
                return $"{name} lane {lane} is {actual[lane]}, expected {exact[lane]}";
            }
        }

        return null;
    }

    /// <summary>Proves every componentwise helper and checked operator is the carrier's own rule applied lane by lane,
    /// against arbitrary-width arithmetic: the checked sum, difference, negation and scalar product refuse exactly when
    /// some lane's exact value leaves the carrier and are exact otherwise; the Hadamard product and quotient are one
    /// ties-to-even rounding per lane, wrapped; the per-lane maximum, greatest component and clamp are exact
    /// comparisons; the rounding to whole numbers is ties-to-even and refuses past the carrier's maximum; and the
    /// absolute value refuses the minimum raw.</summary>
    /// <param name="left">The first operand's four lanes: the first vector and the scalar.</param>
    /// <param name="right">The second operand's four lanes: the second vector and the clamp's second bound.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorComponentwiseHelpersMatchOracle(long[] left, long[] right) {
        var a = Space(
            x: left[0],
            y: left[1],
            z: left[2]
        );
        var b = Space(
            x: right[0],
            y: right[1],
            z: right[2]
        );
        var scaleRaw = left[3];
        var boundRaw = right[3];
        var scale = Raw(value: scaleRaw);
        var divisor = Space(
            x: NonZeroDivisor(b: right[0]),
            y: NonZeroDivisor(b: right[1]),
            z: NonZeroDivisor(b: right[2])
        );
        var lower = new long[3];
        var upper = new long[3];
        var sum = new BigInteger[3];
        var difference = new BigInteger[3];
        var negation = new BigInteger[3];
        var scaled = new BigInteger[3];
        var absolute = new BigInteger[3];
        var rounded = new BigInteger[3];

        for (var lane = 0; (lane < 3); ++lane) {
            var x = left[lane];
            var y = right[lane];

            lower[lane] = ((y < boundRaw) ? y : boundRaw);
            upper[lane] = ((y < boundRaw) ? boundRaw : y);
            sum[lane] = (((BigInteger)x) + y);
            difference[lane] = (((BigInteger)x) - y);
            negation[lane] = -((BigInteger)x);
            scaled[lane] = Oracles.ExactRoundedProduct(
                a: x,
                b: scaleRaw
            );
            absolute[lane] = BigInteger.Abs(value: x);
            rounded[lane] = (Oracles.RoundToEvenUnits(
                magnitude: x,
                shift: FixedQ4816.FractionBitCount
            ) << FixedQ4816.FractionBitCount);
        }

        var minimum = Space(
            x: lower[0],
            y: lower[1],
            z: lower[2]
        );
        var maximum = Space(
            x: upper[0],
            y: upper[1],
            z: upper[2]
        );
        var product = FixedVector3.Multiply(
            left: a,
            right: b
        );
        var quotient = FixedVector3.Divide(
            left: a,
            right: divisor
        );
        var greater = FixedVector3.Max(
            left: a,
            right: b
        );
        var clamped = FixedVector3.Clamp(
            maximum: maximum,
            minimum: minimum,
            value: a
        );

        for (var lane = 0; (lane < 3); ++lane) {
            var x = left[lane];
            var y = right[lane];
            var expectedProduct = Oracles.RoundDyadic(
                exact: (((BigInteger)x) * y),
                shift: FixedQ4816.FractionBitCount
            );
            var expectedQuotient = Oracles.RoundDyadicRatio(
                denominator: NonZeroDivisor(b: y),
                numerator: x,
                shift: FixedQ4816.FractionBitCount
            );
            var expectedGreater = ((x < y) ? y : x);
            var expectedClamped = ((x < lower[lane])
                ? lower[lane]
                : ((x > upper[lane])
                    ? upper[lane]
                    : x
            ));

            if (SpaceComponent(
                lane: lane,
                vector: product
            ) != expectedProduct) { return $"Multiply lane {lane} is wrong for ({x}, {y}), expected {expectedProduct}"; }
            if (SpaceComponent(
                lane: lane,
                vector: quotient
            ) != expectedQuotient) { return $"Divide lane {lane} is wrong for ({x}, {y}), expected {expectedQuotient}"; }
            if (SpaceComponent(
                lane: lane,
                vector: greater
            ) != expectedGreater) { return $"Max lane {lane} is wrong for ({x}, {y})"; }
            if (SpaceComponent(
                lane: lane,
                vector: clamped
            ) != expectedClamped) { return $"Clamp lane {lane} is wrong for {x} in [{lower[lane]}, {upper[lane]}]"; }
        }

        var greatest = ((left[0] < left[1]) ? left[1] : left[0]);

        greatest = ((greatest < left[2]) ? left[2] : greatest);

        if (FixedVector3.MaxComponent(value: a).Value != greatest) { return $"MaxComponent is not the greatest lane {greatest}"; }

        // Swapped bounds refuse whenever some lane's range is not a single point, and are the identity clamp to that
        // point otherwise.
        var degenerate = ((lower[0] == upper[0]) && (lower[1] == upper[1]) && (lower[2] == upper[2]));
        var swapped = RawsOrRefusal<ArgumentException>(operation: () => FixedVector3.Clamp(
            maximum: minimum,
            minimum: maximum,
            value: a
        ));

        if (degenerate == (swapped is null)) {
            return (degenerate
                ? "Clamp refused bounds that are equal in every lane"
                : "Clamp accepted a minimum greater than its maximum"
            );
        }

        return (CheckedLanes<OverflowException>(
            exact: sum,
            name: "checked +",
            operation: () => checked((a + b))
        ) ?? (CheckedLanes<OverflowException>(
            exact: difference,
            name: "checked -",
            operation: () => checked((a - b))
        ) ?? (CheckedLanes<OverflowException>(
            exact: negation,
            name: "checked unary -",
            operation: () => checked(-a)
        ) ?? (CheckedLanes<OverflowException>(
            exact: scaled,
            name: "checked * scalar",
            operation: () => checked((a * scale))
        ) ?? (CheckedLanes<OverflowException>(
            exact: absolute,
            name: "Abs",
            operation: () => FixedVector3.Abs(value: a)
        ) ?? CheckedLanes<OverflowException>(
            exact: rounded,
            name: "Round",
            operation: () => FixedVector3.Round(value: a)
        ))))));
    }
    /// <summary>Proves the three unit axes are exactly one raw unit of <see cref="FixedQ4816.One"/> on their own lane
    /// and zero elsewhere, and that they form a right-handed orthonormal frame under the library's own exact products:
    /// each dots itself to one and the others to zero, and the cross product of X and Y is Z, cyclically.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorUnitAxesAreExact() {
        var one = (1L << FixedQ4816.FractionBitCount);
        FixedVector3[] axes = [FixedVector3.UnitX, FixedVector3.UnitY, FixedVector3.UnitZ];

        for (var axis = 0; (axis < 3); ++axis) {
            for (var lane = 0; (lane < 3); ++lane) {
                var expected = ((axis == lane) ? one : 0L);

                if (SpaceComponent(
                    lane: lane,
                    vector: axes[axis]
                ) != expected) { return $"axis {axis} lane {lane} is not {expected}"; }
            }

            for (var other = 0; (other < 3); ++other) {
                var dot = FixedVector3.Dot(
                    left: axes[axis],
                    right: axes[other]
                ).Value;

                if (dot != ((axis == other) ? one : 0L)) { return $"axis {axis} dotted with axis {other} is raw {dot}"; }
            }

            var cross = FixedVector3.Cross(
                left: axes[axis],
                right: axes[((axis + 1) % 3)]
            );

            if (cross != axes[((axis + 2) % 3)]) { return $"axis {axis} crossed with its successor is not the third axis"; }
        }

        return null;
    }
}
