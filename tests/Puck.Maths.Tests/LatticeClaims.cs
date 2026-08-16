using System.Collections.Immutable;
using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims over <see cref="SymmetryLattice"/>, <see cref="HilbertCurve"/> and <see cref="HexagonalCoordinate"/>.
/// </summary>
/// <remarks>
/// Everything here is exact, including the projection geometry: the eight projected ring radii against the
/// closed-form E8/Ising mass spectrum, their golden-ratio pairing, the twelve-degree turn per
/// <see cref="SymmetryLattice.Cycle(int)"/> step, and ring concentricity are all derived as EXACT
/// <see cref="BigInteger"/> brackets — a Machin-series π and an alternating trigonometric series
/// (<see cref="Oracles.Pi"/>, <see cref="Oracles.EncloseSinCos"/>) for the transcendental parts, and an independent
/// Newton-descent integer square root (<see cref="Oracles.IntegerSquareRoot"/>) for the golden ratio and for
/// concentricity's radii — never a <see langword="double"/>. <see cref="SymmetryLattice.Project(int)"/> itself has no
/// floating point to begin with: its basis vectors are FIXED baked Q16 constants (<c>SymmetryLattice.cs:47-48</c>),
/// so every output is already an exact integer function of them; what these claims add is an equally exact statement
/// of what those outputs are SUPPOSED to be. <see cref="HexagonalCoordinate.Round(FixedQ4816, FixedQ4816)"/>'s
/// nearest-cell statement is exact too — Euclidean distance over the hex lattice collapses algebraically to the exact
/// Eisenstein norm form <c>u² − u·v + v²</c> (see <see cref="HexagonalCoordinateRoundIsNearestCellSurface"/>).
/// </remarks>
internal static class LatticeClaims {
    // ---- SymmetryLattice (exact-only) ----

    /// <summary>The exact-arithmetic half of the SymmetryLattice surface: the baked index maps' internal
    /// consistency (involution, transitivity, ring partition, central inversion, argument refusal) and the
    /// <see cref="SymmetryLattice.RayCycleFactor(int)"/> wiring. Does NOT touch the projection geometry — see the type's
    /// remarks.</summary>
    public static string? SymmetryLatticeExactStructureSurface() {
        Assert.Equal(actual: SymmetryLattice.RingSize, expected: CyclicRotation.Period);
        Assert.Equal(actual: SymmetryLattice.RayCount, expected: 120);
        Assert.Equal(actual: SymmetryLattice.RayCycleOrder, expected: 15);

        // The ray-cycle factors are the STORED result of one BinaryPolynomial.FactorOddCycle(15) call (SymmetryLattice.cs:56);
        // re-multiplying it here is a WIRING check (did SymmetryLattice thread the right order through and keep the
        // right field), not independent algorithm evidence — FactorOddCycle's own multiply-back property is pinned at
        // polynomial.factor-odd-cycle-vs-cyclotomic-cosets and its Deep mirror.
        var rayCycleOrder = SymmetryLattice.RayCycleOrder;
        var rayFactorProduct = new BinaryPolynomial(bits: 1UL);

        for (var index = 0; (index < SymmetryLattice.RayCycleFactorCount); ++index) {
            rayFactorProduct = checked((rayFactorProduct * SymmetryLattice.RayCycleFactor(index: index)));
        }

        Assert.Equal(expected: (1UL << rayCycleOrder) | 1UL, actual: rayFactorProduct.Bits);

        foreach (var invalidNode in new[] { -1, SymmetryLattice.NodeCount, 268_435_456 }) {
            Assert.Equal(expected: "node", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.Reflect(mirror: 0, node: invalidNode)).ParamName);
            Assert.Equal(expected: "mirror", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.Reflect(mirror: invalidNode, node: 0)).ParamName);
            Assert.Equal(expected: "node", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.Cycle(node: invalidNode)).ParamName);
            Assert.Equal(expected: "node", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.Ring(node: invalidNode)).ParamName);
            Assert.Equal(expected: "node", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.Project(node: invalidNode)).ParamName);
            Assert.Equal(expected: "node", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.Antipode(node: invalidNode)).ParamName);
            Assert.Equal(expected: "node", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.CanonicalRay(node: invalidNode)).ParamName);
            Assert.Equal(expected: "first", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.AreOrthogonal(first: invalidNode, second: 0)).ParamName);
            Assert.Equal(expected: "second", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.AreOrthogonal(first: 0, second: invalidNode)).ParamName);
        }

        var ringSizes = new int[SymmetryLattice.RingCount];

        for (var node = 0; (node < SymmetryLattice.NodeCount); ++node) {
            Assert.Equal(expected: SymmetryLattice.Ring(node: node), actual: SymmetryLattice.Ring(node: SymmetryLattice.Cycle(node: node)));

            ringSizes[SymmetryLattice.Ring(node: node)]++;

            for (var mirror = 0; (mirror < SymmetryLattice.NodeCount); ++mirror) {
                Assert.Equal(expected: node, actual: SymmetryLattice.Reflect(node: SymmetryLattice.Reflect(mirror: mirror, node: node), mirror: mirror));
                Assert.Equal(expected: (SymmetryLattice.Reflect(mirror: mirror, node: node) == node), actual: SymmetryLattice.AreOrthogonal(first: node, second: mirror));
            }

            // Every E8 exponent is odd, so the fifteenth cycle power is the central inversion — reflecting a root
            // through its own hyperplane is the same negation, a coordinate-free exact oracle for the half-cycle that
            // is independent of both Reflect(node, node) and Antipode(node): it is derived by fifteen applications of
            // Cycle alone.
            var opposite = node;

            for (var step = 0; (step < (SymmetryLattice.RingSize / 2)); ++step) { opposite = SymmetryLattice.Cycle(node: opposite); }

            Assert.Equal(expected: opposite, actual: SymmetryLattice.Reflect(mirror: node, node: node));
            Assert.Equal(expected: opposite, actual: SymmetryLattice.Antipode(node: node));
            Assert.Equal(expected: SymmetryLattice.CanonicalRay(node: opposite), actual: SymmetryLattice.CanonicalRay(node: node));
        }

        for (var ring = 0; (ring < SymmetryLattice.RingCount); ++ring) {
            Assert.Equal(expected: SymmetryLattice.RingSize, actual: ringSizes[ring]);
        }

        var cycleOrbit = 0;

        for (var cursor = SymmetryLattice.Cycle(node: 0); (cursor != 0); cursor = SymmetryLattice.Cycle(node: cursor)) {
            ++cycleOrbit;

            Assert.True(condition: (cycleOrbit <= SymmetryLattice.RingSize), userMessage: "the cycle from node 0 did not close within RingSize steps");
        }

        // Reflections act transitively: a breadth-first closure from node 0 reaches every node, so the reflection
        // group is the FULL symmetry group the composed reflections generate — not merely the order-30 cycle.
        var reached = new bool[SymmetryLattice.NodeCount];
        var worklist = new int[SymmetryLattice.NodeCount];

        reached[0] = true;
        worklist[0] = 0;

        var pending = 1;
        var reachedCount = 1;

        while (pending > 0) {
            var node = worklist[--pending];

            for (var mirror = 0; (mirror < SymmetryLattice.NodeCount); ++mirror) {
                var image = SymmetryLattice.Reflect(mirror: mirror, node: node);

                if (!reached[image]) {
                    reached[image] = true;
                    worklist[pending++] = image;
                    ++reachedCount;
                }
            }
        }

        Assert.Equal(actual: reachedCount, expected: SymmetryLattice.NodeCount);

        // ---- exact projection geometry: the E8/Ising mass spectrum, the golden-ratio pairing, the twelve-degree
        // turn, and ring concentricity — all four derived as EXACT BigInteger brackets, never `double` geometry.
        // Project's basis vectors are FIXED baked constants (SymmetryLattice.cs:47-48), so every
        // Project output is itself an exact integer function of them; nothing here rounds at runtime.
        var turn = Oracles.EncloseSinCos(raw: PiFractionRaw(denominator: 30, numerator: 2), guardBitCount: Oracles.GuardBitCount);
        var cosPi30 = Oracles.EncloseSinCos(raw: PiFractionRaw(denominator: 30, numerator: 1), guardBitCount: Oracles.GuardBitCount).Cos;
        var cos7Pi30 = Oracles.EncloseSinCos(raw: PiFractionRaw(denominator: 30, numerator: 7), guardBitCount: Oracles.GuardBitCount).Cos;
        var cos2Pi15 = Oracles.EncloseSinCos(raw: PiFractionRaw(denominator: 30, numerator: 4), guardBitCount: Oracles.GuardBitCount).Cos;
        var cosPi5 = Oracles.EncloseSinCos(raw: PiFractionRaw(denominator: 30, numerator: 6), guardBitCount: Oracles.GuardBitCount).Cos;
        var golden = SurdEnclosure(rationalNumerator: BigInteger.One, surdNumerator: BigInteger.One, radicand: 5, denominator: 2);
        var goldenSquared = MultiplyPositive(left: golden, right: golden);
        var identity = new Oracles.Enclosure(Low: (BigInteger.One << GeometryScaleBitCount), High: (BigInteger.One << GeometryScaleBitCount));

        // The closed-form E8/Ising mass spectrum, normalized to the innermost ring.
        Oracles.Enclosure[] massSpectrum = [
            identity,
            golden,
            ScaleByInteger(factor: 2, value: cosPi30),
            ScaleByInteger(value: MultiplyPositive(left: golden, right: cos7Pi30), factor: 2),
            ScaleByInteger(value: MultiplyPositive(left: golden, right: cos2Pi15), factor: 2),
            ScaleByInteger(value: MultiplyPositive(left: golden, right: cosPi30), factor: 2),
            ScaleByInteger(value: MultiplyPositive(left: MultiplyPositive(left: golden, right: cosPi5), right: cos7Pi30), factor: 4),
            ScaleByInteger(value: MultiplyPositive(left: MultiplyPositive(left: golden, right: cosPi5), right: cos2Pi15), factor: 4),
        ];

        // Relative slack on every closed-form ratio comparison below: the projection basis is baked to Q16 (a
        // relative precision near 2⁻¹⁶ ≈ 1.5e-5), and the measured deviation across all eight ring ratios tops out
        // near 1.1e-5 relative — under a hundredth of the 1/1024 (≈9.8e-4) band this uses.
        const int relativeToleranceDenominator = 1024;
        // The per-cycle-step turn's own slack, in raw Q16 ticks: measured at most ~2.2 raw ticks across all 240
        // nodes; four times that margin.
        const int turnToleranceRawTicks = 8;
        // Ring concentricity's own slack, in raw Q16 ticks of RADIUS (not squared radius): measured at most 3 raw
        // ticks across all eight rings, so four raw ULP is the bound.
        const int concentricityToleranceRawTicks = 4;

        var ringMinimumSquaredRadius = new BigInteger[SymmetryLattice.RingCount];
        var ringMaximumSquaredRadius = new BigInteger[SymmetryLattice.RingCount];

        Array.Fill(array: ringMinimumSquaredRadius, value: (BigInteger.One << 200));
        Array.Fill(array: ringMaximumSquaredRadius, value: BigInteger.MinusOne);

        for (var node = 0; (node < SymmetryLattice.NodeCount); ++node) {
            var point = SymmetryLattice.Project(node: node);
            var ring = SymmetryLattice.Ring(node: node);
            var x = new BigInteger(value: point.X.Value);
            var y = new BigInteger(value: point.Y.Value);
            var squaredRadius = ((x * x) + (y * y));

            if (squaredRadius < ringMinimumSquaredRadius[ring]) { ringMinimumSquaredRadius[ring] = squaredRadius; }
            if (squaredRadius > ringMaximumSquaredRadius[ring]) { ringMaximumSquaredRadius[ring] = squaredRadius; }

            // The per-cycle-step turn: Project(Cycle(node)) is Project(node) rotated by twelve degrees CLOCKWISE —
            // confirmed the single consistent direction over all 240 nodes before this was written, so this pins the
            // SIGN of the turn and not only its magnitude.
            var after = SymmetryLattice.Project(node: SymmetryLattice.Cycle(node: node));
            var predictedX = AddEnclosures(left: MultiplyByEnclosure(positive: turn.Cos, scalar: x), right: MultiplyByEnclosure(positive: turn.Sin, scalar: y));
            var predictedY = SubtractEnclosures(left: MultiplyByEnclosure(positive: turn.Cos, scalar: y), right: MultiplyByEnclosure(positive: turn.Sin, scalar: x));
            var actualX = (new BigInteger(value: after.X.Value) << Oracles.GuardBitCount);
            var actualY = (new BigInteger(value: after.Y.Value) << Oracles.GuardBitCount);
            var turnToleranceScaled = (new BigInteger(value: turnToleranceRawTicks) << Oracles.GuardBitCount);

            Assert.True(condition: ((actualX >= (predictedX.Low - turnToleranceScaled)) && (actualX <= (predictedX.High + turnToleranceScaled))), userMessage: $"SYMMETRY LATTICE CYCLE STEP AT NODE {node} DID NOT TURN TWELVE DEGREES (real part)");
            Assert.True(condition: ((actualY >= (predictedY.Low - turnToleranceScaled)) && (actualY <= (predictedY.High + turnToleranceScaled))), userMessage: $"SYMMETRY LATTICE CYCLE STEP AT NODE {node} DID NOT TURN TWELVE DEGREES (imaginary part)");
        }

        // Ring concentricity: an EXACT comparison of squared radii, via an independent Newton-descent floor square
        // root (Oracles.IntegerSquareRoot) rather than a `double` Math.Sqrt — no floating point anywhere.
        for (var ring = 0; (ring < SymmetryLattice.RingCount); ++ring) {
            var minimumRadius = Oracles.IntegerSquareRoot(value: ringMinimumSquaredRadius[ring]);
            var maximumRadius = Oracles.IntegerSquareRoot(value: ringMaximumSquaredRadius[ring]);

            Assert.True(condition: ((maximumRadius - minimumRadius) <= concentricityToleranceRawTicks), userMessage: $"ring {ring} is not concentric within {concentricityToleranceRawTicks} raw ticks (floor radii {minimumRadius} to {maximumRadius})");
        }

        // Sort the rings by radius, ascending, so ringOrder[i] is the ring the mass spectrum's i'th entry names.
        var ringOrder = new int[SymmetryLattice.RingCount];

        for (var i = 0; (i < SymmetryLattice.RingCount); ++i) { ringOrder[i] = i; }

        for (var i = 1; (i < SymmetryLattice.RingCount); ++i) {
            var key = ringOrder[i];
            var j = (i - 1);

            while ((j >= 0) && (ringMinimumSquaredRadius[ringOrder[j]] > ringMinimumSquaredRadius[key])) {
                ringOrder[(j + 1)] = ringOrder[j];
                --j;
            }

            ringOrder[(j + 1)] = key;
        }

        // The closed-form E8/Ising mass spectrum: ring i's squared radius, over the innermost ring's, matches
        // massSpectrum[i] squared — comparing SQUARES avoids taking a square root of the closed form at all.
        var baseSquaredRadius = ringMinimumSquaredRadius[ringOrder[0]];

        for (var i = 0; (i < SymmetryLattice.RingCount); ++i) {
            var ring = ringOrder[i];
            var expectedRatioSquared = MultiplyPositive(left: massSpectrum[i], right: massSpectrum[i]);

            Assert.True(
                condition: WithinRelativeBand(actualNumerator: ringMinimumSquaredRadius[ring], actualDenominator: baseSquaredRadius, expected: expectedRatioSquared, toleranceDenominator: relativeToleranceDenominator),
                userMessage: $"ring {ring} (position {i} by radius) does not match the E8/Ising mass spectrum entry {i}"
            );
        }

        // The golden-ratio pairing: exactly four of the C(8,2) sorted-radius pairs are in the golden ratio.
        var goldenPairs = 0;

        for (var inner = 0; (inner < SymmetryLattice.RingCount); ++inner) {
            for (var outer = (inner + 1); (outer < SymmetryLattice.RingCount); ++outer) {
                if (WithinRelativeBand(actualNumerator: ringMinimumSquaredRadius[ringOrder[outer]], actualDenominator: ringMinimumSquaredRadius[ringOrder[inner]], expected: goldenSquared, toleranceDenominator: relativeToleranceDenominator)) {
                    ++goldenPairs;
                }
            }
        }

        Assert.Equal(actual: goldenPairs, expected: (SymmetryLattice.RingCount / 2));

        return null;
    }

    // ---- exact geometry helpers: shared-nothing with the subject, calling only Oracles' own public primitives.
    // SymmetryLattice touches no transcendental subject kernel, so there is no risk of checking the tree against
    // itself here — this is the same Classical evidence route the scalar transcendental laws already use. ----

    private const int GeometryScaleBitCount = (16 + Oracles.GuardBitCount);

    /// <summary>round(numerator/denominator · π · 2¹⁶) — the raw Q16 angle <see cref="Oracles.EncloseSinCos"/>
    /// wants, computed EXACTLY from <see cref="Oracles.Pi"/>'s Machin-series bracket rather than
    /// <see cref="Math.PI"/>.</summary>
    private static long PiFractionRaw(long numerator, long denominator) {
        const int piBitCount = 64;
        var pi = Oracles.Pi(bitCount: piBitCount);
        var midpoint = ((pi.Low + pi.High) / 2);

        return ((long)Oracles.RoundRationalTiesToEven(
            numerator: (midpoint * numerator),
            denominator: ((BigInteger.One << (piBitCount - 16)) * denominator)
        ));
    }
    /// <summary>Brackets <c>(rationalNumerator + surdNumerator·√radicand) / denominator</c> at
    /// <see cref="GeometryScaleBitCount"/>, all non-negative, by an independent Newton-descent integer square root
    /// at sixty-four extra bits — <see cref="Oracles.IntegerSquareRoot"/> directly.</summary>
    private static Oracles.Enclosure SurdEnclosure(BigInteger rationalNumerator, BigInteger surdNumerator, BigInteger radicand, BigInteger denominator) {
        const int extraBits = 64;
        var fine = (BigInteger.One << extraBits);
        var root = Oracles.IntegerSquareRoot(value: ((radicand * fine) * fine));
        var scale = (BigInteger.One << GeometryScaleBitCount);
        var rationalTerm = ((rationalNumerator * scale) * fine);
        var lowNumerator = (rationalTerm + ((surdNumerator * scale) * root));
        var highNumerator = (rationalTerm + ((surdNumerator * scale) * (root + BigInteger.One)));
        var divisor = (denominator * fine);

        return new Oracles.Enclosure(Low: (lowNumerator / divisor), High: (((highNumerator + divisor) - BigInteger.One) / divisor));
    }
    // The product of two NON-NEGATIVE enclosures, both at GeometryScaleBitCount, rescaled back to it.
    private static Oracles.Enclosure MultiplyPositive(Oracles.Enclosure left, Oracles.Enclosure right) =>
        Oracles.Rescale(
            value: new Oracles.Enclosure(Low: (left.Low * right.Low), High: (left.High * right.High)),
            fromBitCount: (GeometryScaleBitCount * 2),
            toBitCount: GeometryScaleBitCount
        );
    // An exact non-negative integer constant times an enclosure at GeometryScaleBitCount.
    private static Oracles.Enclosure ScaleByInteger(Oracles.Enclosure value, BigInteger factor) =>
        new(Low: (value.Low * factor), High: (value.High * factor));
    // An exact (possibly negative) RAW Q16 integer times a NON-NEGATIVE enclosure at GeometryScaleBitCount: the raw
    // integer scale (Q16) makes the product Q16 wider than GeometryScaleBitCount, so this narrows it back down with
    // directed rounding (Oracles.Rescale) rather than leaving it at the wrong scale.
    private static Oracles.Enclosure MultiplyByEnclosure(BigInteger scalar, Oracles.Enclosure positive) {
        var low = ((scalar.Sign >= 0) ? (scalar * positive.Low) : (scalar * positive.High));
        var high = ((scalar.Sign >= 0) ? (scalar * positive.High) : (scalar * positive.Low));

        return Oracles.Rescale(value: new Oracles.Enclosure(High: high, Low: low), fromBitCount: (16 + GeometryScaleBitCount), toBitCount: GeometryScaleBitCount);
    }
    private static Oracles.Enclosure AddEnclosures(Oracles.Enclosure left, Oracles.Enclosure right) =>
        new(Low: (left.Low + right.Low), High: (left.High + right.High));
    private static Oracles.Enclosure SubtractEnclosures(Oracles.Enclosure left, Oracles.Enclosure right) =>
        new(Low: (left.Low - right.High), High: (left.High - right.Low));
    /// <summary>Whether the exact rational <c>actualNumerator / actualDenominator</c> lies within <c>expected</c>'s
    /// bracket, widened by a <c>1/toleranceDenominator</c> relative band on each side — a cross-multiplied
    /// comparison, so no division and no floating point anywhere.</summary>
    private static bool WithinRelativeBand(BigInteger actualNumerator, BigInteger actualDenominator, Oracles.Enclosure expected, int toleranceDenominator) {
        var scaledActual = ((actualNumerator << GeometryScaleBitCount) * toleranceDenominator);
        var lowBound = ((expected.Low * actualDenominator) * (toleranceDenominator - 1));
        var highBound = ((expected.High * actualDenominator) * (toleranceDenominator + 1));

        return ((scaledActual >= lowBound) && (scaledActual <= highBound));
    }

    // ---- HilbertCurve ----

    /// <summary>The full statement at orders 1-9: <see cref="HilbertCurve.Decode(int, ulong)"/> is a bijection
    /// onto the <c>2^order</c> grid, <see cref="HilbertCurve.Encode(int, uint, uint)"/> is its exact inverse, and
    /// consecutive curve distances land on grid neighbours (Manhattan distance one) — the defining locality property.
    /// Every one of the <c>4^order</c> cells at each order is visited, so this is genuinely exhaustive over that band.</summary>
    public static string? HilbertCurveExhaustiveBijectionSurface() {
        for (var order = 1; (order <= 9); ++order) {
            var side = (1U << order);
            var cells = (((ulong)side) * side);
            var seen = new bool[cells];
            var previous = (X: 0U, Y: 0U);

            for (var distance = 0UL; (distance < cells); ++distance) {
                var point = HilbertCurve.Decode(distance: distance, order: order);
                var cell = ((((ulong)point.Y) * side) + point.X);

                Assert.False(condition: seen[cell], userMessage: $"order {order}: distance {distance} decodes to cell ({point.X},{point.Y}), already visited — Decode is not a bijection");

                seen[cell] = true;

                Assert.Equal(expected: distance, actual: HilbertCurve.Encode(order: order, x: point.X, y: point.Y));

                if (distance > 0UL) {
                    var manhattan = (Math.Abs(value: (((int)point.X) - ((int)previous.X))) + Math.Abs(value: (((int)point.Y) - ((int)previous.Y))));

                    Assert.Equal(actual: manhattan, expected: 1);
                }

                previous = point;
            }
        }

        return null;
    }
    /// <summary>The high-order companion, WITHOUT a hand-rolled generator: a fixed, deterministic set
    /// of bit patterns (zero, the full mask, both alternating patterns, the lowest bit, the highest bit, and the half
    /// mask) at every order 10-31, checked as an Encode/Decode round trip. A SAMPLE of each order's grid, not an
    /// exhaustive one — orders above 9 are too large to visit every cell.</summary>
    public static string? HilbertCurveHighOrderRoundTripSurface() {
        for (var order = 10; (order <= 31); ++order) {
            foreach (var x in HilbertOrderPatterns(order: order)) {
                foreach (var y in HilbertOrderPatterns(order: order)) {
                    var distance = HilbertCurve.Encode(order: order, x: x, y: y);
                    var point = HilbertCurve.Decode(distance: distance, order: order);

                    Assert.Equal(actual: point, expected: (x, y));
                }
            }
        }

        return null;
    }

    private static IEnumerable<uint> HilbertOrderPatterns(int order) {
        var mask = ((order >= 32) ? uint.MaxValue : ((1U << order) - 1U));

        yield return 0U;
        yield return mask;
        yield return (mask & 0x5555_5555U);
        yield return (mask & 0xAAAA_AAAAU);
        yield return 1U;
        yield return (mask >> 1);

        if (order > 1) { yield return (1U << (order - 1)); }
    }

    // ---- HexagonalCoordinate ----

    /// <summary>The ring-algebra statement: the six unit directions are distinct and each at
    /// <see cref="HexagonalCoordinate.Length"/> one, the Eisenstein relation <c>ω² + ω + 1 = 0</c> holds exactly,
    /// <see cref="HexagonalCoordinate.RotatedLeft"/> has order six and is the exact inverse of
    /// <see cref="HexagonalCoordinate.RotatedRight"/> while preserving both <see cref="HexagonalCoordinate.Length"/>
    /// and <see cref="HexagonalCoordinate.Norm"/>, and the ring product is associative — all over a 49×49
    /// coordinate box.</summary>
    public static string? HexagonalCoordinateAlgebraicStructureSurface() {
        for (var direction = 0; (direction < HexagonalCoordinate.NeighborCount); ++direction) {
            Assert.Equal(expected: 1, actual: HexagonalCoordinate.Direction(direction: direction).Length);

            for (var other = (direction + 1); (other < HexagonalCoordinate.NeighborCount); ++other) {
                Assert.NotEqual(expected: HexagonalCoordinate.Direction(direction: direction), actual: HexagonalCoordinate.Direction(direction: other));
            }
        }

        var omega = new HexagonalCoordinate(Q: 0, R: 1);

        Assert.Equal(expected: HexagonalCoordinate.AdditiveIdentity, actual: (((omega * omega) + omega) + HexagonalCoordinate.MultiplicativeIdentity));

        for (var q = -24; (q <= 24); ++q) {
            for (var r = -24; (r <= 24); ++r) {
                var hex = new HexagonalCoordinate(Q: q, R: r);
                var rotatedLeft = hex.RotatedLeft();

                Assert.Equal(expected: hex.Length, actual: rotatedLeft.Length);
                Assert.Equal(expected: hex.Norm, actual: rotatedLeft.Norm);
                Assert.Equal(expected: hex, actual: rotatedLeft.RotatedRight());

                var spun = hex;

                for (var i = 0; (i < 6); ++i) { spun = spun.RotatedLeft(); }

                Assert.Equal(actual: spun, expected: hex);

                var scaled = new HexagonalCoordinate(Q: (q % 6), R: (r % 6));

                Assert.Equal(actual: ((hex * omega) * scaled), expected: (hex * (omega * scaled)));
            }
        }

        return null;
    }
    /// <summary>The graph-distance statement: <see cref="HexagonalCoordinate.Length"/> equals the true
    /// breadth-first hex-grid distance out to radius 12, computed here as a self-contained array BFS that shares no
    /// line with <see cref="HexagonalCoordinate.Length"/>'s own norm-based formula.</summary>
    public static string? HexagonalCoordinateLengthMatchesGraphDistanceSurface() {
        const int radius = 12;
        const int offset = (radius + 1);
        const int span = ((2 * offset) + 1);

        var distance = new int[(span * span)];
        var frontier = new int[(span * span)];

        Array.Fill(array: distance, value: -1);

        int[] directionQ = [1, 1, 0, -1, -1, 0];
        int[] directionR = [0, 1, 1, 0, -1, -1];
        var origin = ((offset * span) + offset);

        distance[origin] = 0;

        var head = 0;
        var tail = 0;

        frontier[tail++] = origin;

        while (head < tail) {
            var packed = frontier[head++];
            var cellQ = ((packed / span) - offset);
            var cellR = ((packed % span) - offset);
            var cellDistance = distance[packed];

            if (cellDistance >= radius) { continue; }

            for (var k = 0; (k < 6); ++k) {
                var stepQ = (cellQ + directionQ[k]);
                var stepR = (cellR + directionR[k]);

                if ((stepQ < -offset) || (stepQ > offset) || (stepR < -offset) || (stepR > offset)) { continue; }

                var stepPacked = (((stepQ + offset) * span) + (stepR + offset));

                if (distance[stepPacked] < 0) {
                    distance[stepPacked] = (cellDistance + 1);
                    frontier[tail++] = stepPacked;
                }
            }
        }

        for (var q = -radius; (q <= radius); ++q) {
            for (var r = -radius; (r <= radius); ++r) {
                var packed = (((q + offset) * span) + (r + offset));

                if (distance[packed] >= 0) {
                    Assert.Equal(expected: distance[packed], actual: new HexagonalCoordinate(Q: q, R: r).Length);
                }
            }
        }

        return null;
    }
    /// <summary>Round against a brute-force Euclidean nearest-cell search, stated EXACTLY. The natural Euclidean form
    /// <c>dx = u − 0.5v</c>, <c>dy = (√3/2)v</c> would need a floating-point tolerance, but it collapses
    /// algebraically to <c>dx² + dy² = u² − u·v + v²</c> — the Eisenstein norm form, and exactly rational — so the
    /// oracle is computed here in <see cref="Int128"/> with NO floating point and NO tolerance: the rounded cell's
    /// exact scaled distance must equal the window minimum exactly.</summary>
    public static string? HexagonalCoordinateRoundIsNearestCellSurface() {
        const int windowRadius = 14;
        const long qMultiplier = 9829L;
        const long rMultiplier = 9829L;
        const long unit = (1L << FixedQ4816.FractionBitCount);

        for (var stepQ = -80; (stepQ <= 80); ++stepQ) {
            var qRaw = (stepQ * qMultiplier);

            for (var stepR = -80; (stepR <= 80); ++stepR) {
                var rRaw = (stepR * rMultiplier);
                var best = Int128.MaxValue;

                for (var a = -windowRadius; (a <= windowRadius); ++a) {
                    for (var b = -windowRadius; (b <= windowRadius); ++b) {
                        var candidate = HexagonalNearestCellMetric(candidateQ: a, candidateR: b, qRaw: qRaw, rRaw: rRaw, unit: unit);

                        if (candidate < best) { best = candidate; }
                    }
                }

                var rounded = HexagonalCoordinate.Round(q: FixedQ4816.FromRawBits(value: qRaw), r: FixedQ4816.FromRawBits(value: rRaw));
                var roundedDistance = HexagonalNearestCellMetric(candidateQ: rounded.Q, candidateR: rounded.R, qRaw: qRaw, rRaw: rRaw, unit: unit);

                Assert.Equal(actual: roundedDistance, expected: best);
            }
        }

        return null;
    }

    // The Euclidean nearest-cell distance in the hex plane, in raw Q48.16 scaled units squared: u = candidate·unit −
    // qRaw, v = candidate·unit − rRaw, distance = u² − u·v + v². No floating point anywhere — Int128 has ample
    // headroom for these magnitudes (unit = 2^16, |candidate| <= 14, |qRaw|,|rRaw| < 2^20).
    private static Int128 HexagonalNearestCellMetric(int candidateQ, int candidateR, long qRaw, long rRaw, long unit) {
        var u = checked(((((Int128)candidateQ) * unit) - qRaw));
        var v = checked(((((Int128)candidateR) * unit) - rRaw));

        return checked((((u * u) - (u * v)) + (v * v)));
    }

    /// <summary>Proves the curve refuses every argument outside its documented domain instead of aliasing it onto a
    /// value inside: the order outside <c>[1, 31]</c>, a coordinate at or past <c>2^order</c>, a distance at or past
    /// <c>4^order</c>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when every case refused.</returns>
    /// <remarks>
    /// Each pair below used to COLLIDE rather than refuse, because C# masks a shift count and nothing validated the
    /// arguments: <c>Decode(32, 12345)</c>, <c>Decode(1, 4)</c> and <c>Decode(1, 0)</c> all returned <c>(0, 0)</c>, and
    /// <c>Encode(1, 2, 0)</c> matched <c>Encode(1, 0, 0)</c>. A bijective spatial key that silently maps distinct
    /// inputs together is worse than one that throws, because the caller's own corruption is absorbed rather than
    /// surfaced. The in-range controls at the end keep the refusals from being a wall.
    /// </remarks>
    public static string? HilbertCurveRefusesOutsideItsDomain() {
        (string Name, Action Build)[] refusals = [
            ("an order of zero", static () => _ = HilbertCurve.Encode(order: 0, x: 0, y: 0)),
            ("a negative order", static () => _ = HilbertCurve.Encode(order: -1, x: 0, y: 0)),
            ("an order past thirty-one, where the shift count masks", static () => _ = HilbertCurve.Decode(distance: 12345, order: 32)),
            ("an order of sixty-four, which masks to zero", static () => _ = HilbertCurve.Decode(distance: 1, order: 64)),
            ("an x coordinate at the grid side", static () => _ = HilbertCurve.Encode(order: 1, x: 2, y: 0)),
            ("a y coordinate at the grid side", static () => _ = HilbertCurve.Encode(order: 1, x: 0, y: 2)),
            ("a coordinate far past the grid side", static () => _ = HilbertCurve.Encode(order: 4, x: uint.MaxValue, y: 0)),
            ("a distance at the curve length", static () => _ = HilbertCurve.Decode(distance: 4, order: 1)),
            ("a distance far past the curve length", static () => _ = HilbertCurve.Decode(distance: ulong.MaxValue, order: 2)),
        ];

        foreach (var (name, build) in refusals) {
            try {
                build();

                return $"{name} was admitted rather than refused";
            } catch (ArgumentOutOfRangeException) {
            }
        }

        // The domain is REACHABLE at both edges, so the refusals above bound it rather than shrink it.
        _ = HilbertCurve.Encode(order: 1, x: 1, y: 1);
        _ = HilbertCurve.Decode(distance: 3, order: 1);
        _ = HilbertCurve.Encode(order: 31, x: ((1U << 31) - 1U), y: 0);
        _ = HilbertCurve.Decode(distance: ((1UL << 62) - 1UL), order: 31);

        return null;
    }
    /// <summary>Proves a consumer cannot reach the verified ray-cycle factors' process-wide storage: the surface hands
    /// out a count and a value per position, and the two published marshal escape hatches find nothing to unwrap.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// This statement is made by ATTEMPTING the escape, not by inspecting the declared type — checking the type was the
    /// error that let the first repair look complete. Read-only is not ownership and neither is immutable: a
    /// <c>ReadOnlyMemory</c> yields its backing array to <c>MemoryMarshal.TryGetArray</c>, and an
    /// <c>ImmutableArray</c> yields the same array to <c>ImmutableCollectionsMarshal.AsArray</c>. Both are public and
    /// need neither reflection nor unsafe code, and either one let a consumer zero the lattice metadata for every later
    /// consumer in the process. The check below is that no member of the surface returns anything either marshal can
    /// unwrap, and that a write attempted through whatever a consumer CAN hold does not survive to the next read.
    /// </remarks>
    public static string? RayCycleFactorsAreNotWritableByConsumers() {
        var count = SymmetryLattice.RayCycleFactorCount;

        Assert.True(condition: (count > 0), userMessage: "the ray-cycle factor surface reports nothing to protect");

        // No public member hands back a collection either marshal can unwrap. A surface that returned one would be
        // reachable exactly as the two previous shapes were.
        foreach (var member in typeof(SymmetryLattice).GetProperties()) {
            var type = member.PropertyType;

            if (type.IsGenericType && ((type.GetGenericTypeDefinition() == typeof(ImmutableArray<>)) || (type.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>)) || (type.GetGenericTypeDefinition() == typeof(Memory<>)))) {
                return $"SymmetryLattice.{member.Name} returns {type}, which a published marshal unwraps into the process-wide backing array";
            }

            if (type.IsArray) {
                return $"SymmetryLattice.{member.Name} returns the array {type} directly";
            }
        }

        // What a consumer CAN hold is a copy of a value type. Writing to it must not reach the store.
        var snapshot = new ulong[count];

        for (var index = 0; (index < count); ++index) { snapshot[index] = SymmetryLattice.RayCycleFactor(index: index).Bits; }

        var held = SymmetryLattice.RayCycleFactor(index: 0);

        held = default;
        Assert.Equal(expected: 0UL, actual: held.Bits);

        for (var index = 0; (index < count); ++index) {
            var reread = SymmetryLattice.RayCycleFactor(index: index).Bits;

            if (snapshot[index] != reread) {
                return $"ray-cycle factor {index} changed from {snapshot[index]} to {reread} after a consumer wrote to what it was handed";
            }
        }

        // The bound is real at both ends rather than a wall.
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.RayCycleFactor(index: -1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SymmetryLattice.RayCycleFactor(index: count));
        _ = SymmetryLattice.RayCycleFactor(index: (count - 1));

        return null;
    }
}
