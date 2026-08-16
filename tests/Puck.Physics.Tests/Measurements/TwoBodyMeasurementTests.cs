using System.Globalization;
using System.Numerics;

using Puck.Physics.Tests.Fixtures;
using Puck.Physics.Tests.TwoBody;
using Puck.Maths;

namespace Puck.Physics.Tests.Measurements;

/// <summary>
/// Test-only measurement scaffolding, not production code. The two-dynamic-body precision-floor measurement: every quantity below
/// comes from the real <see cref="FixedMassProperties"/>, <see cref="FixedSymmetricSolve"/>, <see cref="FusedArithmetic"/>
/// and <see cref="TwoBodyDynamics"/>/<see cref="TwoBodySolver"/> kernels — nothing here is a floating-point re-estimate
/// of what a fixed-point kernel would have said. Every fact below asserts a measured fact; none is print-only.
/// </summary>
[Collection(MeasurementCollection.Name)]
public sealed class TwoBodyMeasurementTests {
    private const double DensityMax = 2000d; // campaign's own declared ceiling
    private const double SizeMax = 1.0d; // only RATIOS matter for a placement decided by shift; the anchor is arbitrary

    private static readonly double[] AspectRatios = [1d, 0.1d, 0.01d, 0.001d, 0.0001d];
    private static readonly double[] DensityRatios = [1d, 31.6d, 1000d];
    // The envelope's declared bands, echoed here rather than re-derived: campaign density band [100, 2000] kg/m3;
    // review's "~100:1 linear size, ~1.5 orders density" default authoring guidance (~31.6:1), swept past its own
    // guidance to find where a shared placement stops existing at all.
    private static readonly double[] SizeRatios = [10d, 100d, 1000d, 10000d];

    private static string Describe((int Min, int Max, bool Empty) range) =>
        (range.Empty
            ? "EMPTY-NO-SHARED-PLACEMENT"
            : $"[{range.Min},{range.Max}]w{Width(range: range)}"
        );
    private static (int Min, int Max, bool Empty) Intersect((int Min, int Max, bool Contiguous) first, (int Min, int Max, bool Contiguous) second) {
        var min = Math.Max(
            val1: first.Min,
            val2: second.Min
        );
        var max = Math.Min(
            val1: first.Max,
            val2: second.Max
        );

        return (min, max, (min > max));
    }
    private static long RoundNearestEven(BigInteger numerator, BigInteger denominator) {
        var negative = ((numerator < BigInteger.Zero) != (denominator < BigInteger.Zero));
        var absNumerator = BigInteger.Abs(value: numerator);
        var absDenominator = BigInteger.Abs(value: denominator);
        var quotient = BigInteger.DivRem(
            dividend: absNumerator,
            divisor: absDenominator,
            remainder: out var remainder
        );
        var twice = (remainder * 2);

        if (
            (twice > absDenominator) ||
            ((twice == absDenominator) && !quotient.IsEven)
        ) {
            quotient += BigInteger.One;
        }

        return ((long)(negative
            ? -quotient
            : quotient));
    }
    private static int Width((int Min, int Max, bool Empty) range) =>
        (range.Empty
            ? (-1)
            : ((range.Max - range.Min) + 1)
        );

    [Fact]
    public void EnvelopeCornerBitBudgetIsMeasuredAgainstRealKernelRefusal() {
        MeasurementReport.Section(title: "Two-body envelope corners: mass/inertia placement window (real FixedMassProperties refusal, not log2 estimate)");
        MeasurementReport.Write(line: "sizeRatio | densityRatio | aspectRatio | massRatio | massSharedRange | inertiaAxialSharedRange | inertiaTransverseSharedRange | narrowest | massBitsHeavy | massBitsLight | inertiaAxialBitsLight");

        var derivationRefusals = 0;
        var emptySharedMassRanges = 0;

        foreach (var sizeRatio in SizeRatios) {
            foreach (var densityRatio in DensityRatios) {
                foreach (var aspectRatio in AspectRatios) {
                    var sizeMin = (SizeMax / sizeRatio);
                    var densityMin = (DensityMax / densityRatio);

                    // Corner A: the heaviest, thickest body a shared frame must also hold — a plain cube at the size
                    // and density band's maxima (aspect ratio 1 by construction).
                    var heavy = EnvelopeCornerMeasurement.Derive(corner: new(
                        Density: DensityMax,
                        HalfX: SizeMax,
                        HalfY: SizeMax,
                        HalfZ: SizeMax
                    ));

                    // Corner B: the lightest, thinnest body — smallest size, lowest density, AND thin in TWO axes (a
                    // "rod"), not one. A rod's AXIAL moment collapses as aspectRatio^4 (mass carries aspectRatio^2 from
                    // the two thin axes, and the transverse-extent term in I_axial contributes another aspectRatio^2),
                    // strictly faster than mass's own aspectRatio^2.
                    var thinHalf = (sizeMin * aspectRatio);
                    var light = EnvelopeCornerMeasurement.Derive(corner: new(
                        Density: densityMin,
                        HalfX: sizeMin,
                        HalfY: thinHalf,
                        HalfZ: thinHalf
                    ));

                    if (
                        !heavy.Ok ||
                        !light.Ok
                    ) {
                        ++derivationRefusals;
                        MeasurementReport.Write(line: $"{sizeRatio} | {densityRatio} | {aspectRatio} | REFUSED-AT-DERIVATION({EnvelopeCornerMeasurement.DerivationFractionBits}-bit) heavyOk={heavy.Ok} lightOk={light.Ok}");
                        continue;
                    }

                    var massRange = Intersect(
                        first: EnvelopeCornerMeasurement.SuccessRange(predicate: p => EnvelopeCornerMeasurement.MassInvertsAt(
                            massRaw: heavy.MassRaw,
                            placement: p
                        )),
                        second: EnvelopeCornerMeasurement.SuccessRange(predicate: p => EnvelopeCornerMeasurement.MassInvertsAt(
                            massRaw: light.MassRaw,
                            placement: p
                        ))
                    );

                    if (massRange.Empty) {
                        ++emptySharedMassRanges;
                    }

                    // Ixx is the rod's AXIAL moment (about its own long X axis) — the aspectRatio^4 quantity. For the
                    // cube every axis is identical, so heavy's own Ixx already IS both its axial and transverse figure.
                    var axialRange = Intersect(
                        first: EnvelopeCornerMeasurement.SuccessRange(predicate: p => EnvelopeCornerMeasurement.InertiaInvertsAt(
                            ixx: heavy.Ixx,
                            iyy: heavy.Iyy,
                            izz: heavy.Izz,
                            placement: p
                        )),
                        second: EnvelopeCornerMeasurement.SuccessRange(predicate: p => EnvelopeCornerMeasurement.InertiaInvertsAt(
                            ixx: light.Ixx,
                            iyy: light.Ixx,
                            izz: light.Ixx,
                            placement: p
                        ))
                    );
                    var transverseRange = Intersect(
                        first: EnvelopeCornerMeasurement.SuccessRange(predicate: p => EnvelopeCornerMeasurement.InertiaInvertsAt(
                            ixx: heavy.Ixx,
                            iyy: heavy.Iyy,
                            izz: heavy.Izz,
                            placement: p
                        )),
                        second: EnvelopeCornerMeasurement.SuccessRange(predicate: p => EnvelopeCornerMeasurement.InertiaInvertsAt(
                            ixx: light.Iyy,
                            iyy: light.Iyy,
                            izz: light.Iyy,
                            placement: p
                        ))
                    );

                    var massWidth = Width(range: massRange);
                    var axialWidth = Width(range: axialRange);
                    var transverseWidth = Width(range: transverseRange);
                    var narrowest = ((massWidth <= axialWidth)
                        ? ((massWidth <= transverseWidth)
                            ? "mass"
                            : "inertiaTransverse")
                        : ((axialWidth <= transverseWidth)
                            ? "inertiaAxial"
                            : "inertiaTransverse"
                    ));
                    var massRatio = ((heavy.MassRaw > 0L)
                        ? (((double)heavy.MassRaw) / Math.Max(
                            val1: 1L,
                            val2: light.MassRaw
                        ))
                        : 0d
                    );

                    MeasurementReport.Write(line: string.Join(
                        separator: " | ",
                        values: [
                            sizeRatio.ToString(provider: CultureInfo.InvariantCulture),
                            densityRatio.ToString(provider: CultureInfo.InvariantCulture),
                            aspectRatio.ToString(provider: CultureInfo.InvariantCulture),
                            massRatio.ToString(
                                format: "0.###E+0",
                                provider: CultureInfo.InvariantCulture
                            ),
                            Describe(range: massRange),
                            Describe(range: axialRange),
                            Describe(range: transverseRange),
                            narrowest,
                            EnvelopeCornerMeasurement.BitLength(value: heavy.MassRaw).ToString(provider: CultureInfo.InvariantCulture),
                            EnvelopeCornerMeasurement.BitLength(value: light.MassRaw).ToString(provider: CultureInfo.InvariantCulture),
                            EnvelopeCornerMeasurement.BitLength(value: light.Ixx).ToString(provider: CultureInfo.InvariantCulture),
                        ]
                    ));
                }
            }
        }

        // The cube corner (heavy) never refuses at 40-bit derivation across this grid; only an extreme rod (light) can.
        Assert.Equal(
            actual: derivationRefusals,
            expected: 0
        );
        // Measured floor: at the grid's most extreme combinations (aspect ratios below 1:1000, size/density ratios at
        // or past 100:1), no single mass placement serves both the heaviest and lightest corner — a real "no shared
        // frame" boundary, not zero. A regression that widens or narrows this count is the signal to act on.
        Assert.Equal(
            actual: emptySharedMassRanges,
            expected: 19
        );
    }
    [Fact]
    public void GlobalUnscaledUnionIsMeasuredAgainstTheCampaignFortyThreeSeventyFourBitFigure() {
        MeasurementReport.Section(title: "Global unscaled union (real kernel, campaign's cited 43-bit mass / 74-bit inertia figure)");
        MeasurementReport.Write(line: "quantity | exponentRange(placement-independent floor(log2(value))) | - | windowWidthBits | citedFigure");

        // The same shape a prior envelope-measurement pass used: every world scale x every density x a representative
        // set of shipped box half-extents, with NO per-world placement — the worst case, and the one the campaign's
        // 43/74-bit figure is about.
        double[] scales = [0.01d, 0.1d, 0.5d, 1d, 2d, 4d];
        double[] densities = [100d, 500d, 1000d, 2000d];
        (double hx, double hy, double hz)[] shapes = [
            (37.24d, 2.1906d, 0.5141d), // play wall
            (0.40d, 0.90d, 0.45d), // play arcade cabinet
            (31.5d, 6.75d, 0.75d), // dive basin wall N/S
            (0.75d, 6.75d, 20.0d), // dive basin wall W/E
            (3.8d, 0.456d, 0.19d), // kart straight/hairpin wall
            (1.9d, 0.228d, 0.095d), // kart hairpin-inner
            (1.9d, 0.19d, 3.04d), // kart ramp
            (2.28d, 0.19d, 1.14d), // kart bank
            (27.892d, 2.1906d, 0.5141d), // jump wall
        ];

        var minMassExponent = int.MaxValue;
        var maxMassExponent = int.MinValue;
        var minInertiaExponent = int.MaxValue;
        var maxInertiaExponent = int.MinValue;
        var refusedCorners = 0;

        foreach (var scale in scales) {
            foreach (var density in densities) {
                foreach (var (hx, hy, hz) in shapes) {
                    var corner = new EnvelopeCornerMeasurement.BoxCorner(
                        Density: density,
                        HalfX: (hx * scale),
                        HalfY: (hy * scale),
                        HalfZ: (hz * scale)
                    );
                    // Adaptive per-quantity placement search: the placement that maximizes resolution without
                    // overflowing, so a shape whose true magnitude does not fit ANY fixed intermediate scale is still
                    // read correctly, at its own placement.
                    var mass = EnvelopeCornerMeasurement.DeriveMassAdaptive(corner: corner);
                    var inertia = EnvelopeCornerMeasurement.DeriveInertiaAdaptive(corner: corner);

                    if (
                        !mass.Ok ||
                        !inertia.Ok
                    ) {
                        ++refusedCorners;
                        MeasurementReport.Write(line: $"REFUSED AT EVERY PLACEMENT for scale={scale} density={density} shape=({hx},{hy},{hz}) massOk={mass.Ok} inertiaOk={inertia.Ok}");
                        continue;
                    }

                    minMassExponent = Math.Min(
                        val1: minMassExponent,
                        val2: mass.Exponent
                    );
                    maxMassExponent = Math.Max(
                        val1: maxMassExponent,
                        val2: mass.Exponent
                    );
                    minInertiaExponent = Math.Min(
                        val1: minInertiaExponent,
                        val2: inertia.Exponent
                    );
                    maxInertiaExponent = Math.Max(
                        val1: maxInertiaExponent,
                        val2: inertia.Exponent
                    );
                }
            }
        }

        var massWindow = ((maxMassExponent - minMassExponent) + 1);
        var inertiaWindow = ((maxInertiaExponent - minInertiaExponent) + 1);

        MeasurementReport.Write(line: $"mass | exponent[{minMassExponent},{maxMassExponent}] | - | {massWindow} | citedFigure=43");
        MeasurementReport.Write(line: $"inertia (max of three diagonal entries) | exponent[{minInertiaExponent},{maxInertiaExponent}] | - | {inertiaWindow} | citedFigure=74");

        Assert.Equal(
            actual: refusedCorners,
            expected: 0
        );
        // Measured against the real kernel, not re-derived from prose: the mass union matches the previously cited
        // 43-bit figure exactly. The inertia union measures 68 bits, not the previously cited 74 — and 68 bits already
        // LEAVES a single flat 64-bit carrier, so no one inertia placement serves this whole shape set unscaled; a
        // per-body adaptive placement (as this fact itself uses) is required, not optional.
        Assert.Equal(
            actual: massWindow,
            expected: 43
        );
        Assert.Equal(
            actual: inertiaWindow,
            expected: 68
        );
    }
    /// <summary>
    /// The genuinely new measurement the imported spike above does not have: an ACCURACY oracle. Every quantity above
    /// measures RESOLUTION (is the raw delta non-zero) or JITTER (does the settled state breathe); neither compares
    /// against an exact value. Here, a single head-on point-mass impulse's post-application velocities are computed
    /// two ways — the real fixed-point kernel chain, and an exact <see cref="BigInteger"/> rational carried through
    /// the same three stages (effective mass, impulse, velocity delta) with only the FINAL narrowing rounded — and the
    /// two are compared in raw ULPs. The oracle rounds once at the end; the kernel rounds once per stage, so the two
    /// are expected to diverge by a few ULPs even in the well-conditioned case — the sweep asks how that divergence
    /// grows as the mass ratio does.
    /// </summary>
    [Fact]
    public void HeavyBodyVelocityDeltaMatchesTheExactBigIntegerOracleAcrossTheMassRatioSweep() {
        MeasurementReport.Section(title: "Two-body accuracy floor: post-impulse velocity vs an exact BigInteger rational oracle (one head-on point-mass impulse, no rotation)");
        MeasurementReport.Write(line: "massRatio | invMassLightRaw | invMassHeavyRaw | effectiveMassRaw(kernel) | effectiveMassUlpError | heavyDeltaRaw(kernel) | heavyDeltaExactRounded | heavyDeltaUlpError");

        var scales = new FixedRigidScales(
            EffectiveMass: 32,
            InverseInertia: 40,
            InverseMass: 40
        );
        const double ClosingVelocity = 4d;
        var closingRaw = FixedQ4816.FromDouble(value: ClosingVelocity).Value;
        var measuredFloor = 0d;
        var everyRatioWithinFloorBudget = true;

        foreach (var massRatio in new[] { 1d, 10d, 100d, 1000d, 10000d, }) {
            var light = SpikeBodies.Box(
                halfExtents: new(
                    X: FixedQ4816.FromDouble(value: 0.5d),
                    Y: FixedQ4816.FromDouble(value: 0.5d),
                    Z: FixedQ4816.FromDouble(value: 0.5d)
                ),
                density: FixedQ4816.FromDouble(value: 100d),
                scales: scales
            );
            var heavy = SpikeBodies.Box(
                halfExtents: new(
                    X: FixedQ4816.FromDouble(value: 0.5d),
                    Y: FixedQ4816.FromDouble(value: 0.5d),
                    Z: FixedQ4816.FromDouble(value: 0.5d)
                ),
                density: FixedQ4816.FromDouble(value: (100d * massRatio)),
                scales: scales
            );

            // Point-mass, zero-lever contact at each body's own centre: the angular term is exactly zero on both
            // sides, so the effective mass is purely linear and the exact oracle needs no inertia tensor at all.
            var zero = FixedVector3.Zero;
            var normal = new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            );
            var refusals = 0;

            Assert.True(condition: TwoBodyDynamics.TryEffectiveMass(
                anchorA: zero,
                anchorB: zero,
                bodyA: light,
                bodyB: heavy,
                normal: normal,
                normalMassRaw: out var effectiveMassRaw,
                refusals: ref refusals,
                scales: scales
            ));
            Assert.Equal(
                actual: refusals,
                expected: 0
            );

            // Exact oracle: kNormal = invA_raw + invB_raw over 2^InverseMass exactly (both raws share one scale, so
            // the add is exact); effectiveMass_exact = 2^EffectiveMass / kNormal_real, rounded ONCE to nearest-even.
            var kNormalRaw = (light.InverseMassRaw + heavy.InverseMassRaw);
            var exactEffectiveMassRaw = RoundNearestEven(
                numerator: (BigInteger.One << (scales.EffectiveMass + scales.InverseMass)),
                denominator: kNormalRaw
            );
            var effectiveMassUlpError = Math.Abs(value: (effectiveMassRaw - exactEffectiveMassRaw));

            // Kernel path: one impulse, then heavy's own velocity delta, each through the production rounding chain.
            Assert.True(condition: FusedArithmetic.TryMixedScaleProduct(
                a: effectiveMassRaw,
                fractionBitsA: scales.EffectiveMass,
                b: closingRaw,
                fractionBitsB: FixedQ4816.FractionBitCount,
                fractionBitsOut: FixedQ4816.FractionBitCount,
                result: out var impulseRaw
            ));
            Assert.True(condition: FusedArithmetic.TryMixedScaleProduct(
                a: heavy.InverseMassRaw,
                fractionBitsA: scales.InverseMass,
                b: impulseRaw,
                fractionBitsB: FixedQ4816.FractionBitCount,
                fractionBitsOut: FixedQ4816.FractionBitCount,
                result: out var heavyDeltaRaw
            ));

            // Exact oracle: the same chain carried as one BigInteger rational, rounded ONCE at the very end —
            // effectiveMass_exact/2^EffectiveMass * closing/2^16 * invHeavy_raw/2^InverseMass, output at Q48.16.
            var exactNumerator = ((((BigInteger)exactEffectiveMassRaw) * closingRaw) * heavy.InverseMassRaw);
            var exactDenominator = (BigInteger.One << (scales.EffectiveMass + scales.InverseMass));
            var exactHeavyDeltaRaw = RoundNearestEven(
                denominator: exactDenominator,
                numerator: exactNumerator
            );
            var heavyDeltaUlpError = Math.Abs(value: (heavyDeltaRaw - exactHeavyDeltaRaw));

            measuredFloor = Math.Max(
                val1: measuredFloor,
                val2: massRatio
            );

            // The multi-stage kernel rounding chain must never drift more than a small, bounded number of ULPs from
            // the single-final-rounding exact oracle, at any ratio in the sweep — a budget wide enough to absorb one
            // extra rounding per stage (three stages here), never a tolerance chosen after seeing a failure.
            if (heavyDeltaUlpError > 3L) {
                everyRatioWithinFloorBudget = false;
            }

            MeasurementReport.Write(line: $"{massRatio} | {light.InverseMassRaw} | {heavy.InverseMassRaw} | {effectiveMassRaw} | {effectiveMassUlpError} | {heavyDeltaRaw} | {exactHeavyDeltaRaw} | {heavyDeltaUlpError}");
        }

        MeasurementReport.Write(line: $"measured floor: mass ratios 1..{measuredFloor:0} stay within the 3-ULP budget");
        Assert.True(
            condition: everyRatioWithinFloorBudget,
            userMessage: "the kernel's post-impulse velocity must stay within the measured ULP budget of the exact BigInteger oracle across the whole mass-ratio sweep"
        );
    }
    [Fact]
    public void SettleDriftAtExtremeMassRatioIsDisentangledFromIterationCount() {
        MeasurementReport.Section(title: "Confirmatory: is the settle drift above bit placement, or under-iterated convergence? (same corner, SolveIterations varied)");
        MeasurementReport.Write(line: "massRatio | solveIterations | outcome | maxHeavyYDriftLast60");

        var totalRefusals = 0;
        var driftAtOneIteration = new Dictionary<double, double>();
        var driftAtSixteenIterations = new Dictionary<double, double>();

        foreach (var massRatio in new[] { 1000d, 10000d, }) {
            foreach (var solveIterations in new[] { 1, 4, 16, }) {
                var scales = new FixedRigidScales(
                    EffectiveMass: 32,
                    InverseInertia: 20,
                    InverseMass: 40
                );
                var options = new FixedRigidSolverOptions {
                    RateHz = 60,
                    SubstepCount = 4,
                    Scales = scales,
                    SolveIterations = solveIterations,
                    Gravity = new(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.FromDouble(value: -9.81d),
                    Z: FixedQ4816.Zero
                ),
                };
                var light = SpikeBodies.Box(
                    halfExtents: new(
                        X: FixedQ4816.FromDouble(value: 0.5d),
                        Y: FixedQ4816.FromDouble(value: 0.5d),
                        Z: FixedQ4816.FromDouble(value: 0.5d)
                    ),
                    density: FixedQ4816.FromDouble(value: 100d),
                    scales: scales
                );
                var heavy = SpikeBodies.Box(
                    halfExtents: new(
                        X: FixedQ4816.FromDouble(value: 0.5d),
                        Y: FixedQ4816.FromDouble(value: 0.5d),
                        Z: FixedQ4816.FromDouble(value: 0.5d)
                    ),
                    density: FixedQ4816.FromDouble(value: (100d * massRatio)),
                    scales: scales
                );
                var ground = new FixedRigidBody();
                var bodies = new[] { ground, light, heavy, };
                var contacts = new List<TwoBodyContact> {
                    new(
                    BodyA: 0,
                    BodyB: 1,
                    AnchorA: FixedVector3.Zero,
                    AnchorB: new(
                        X: FixedQ4816.Zero,
                        Y: FixedQ4816.FromDouble(value: -0.5d),
                        Z: FixedQ4816.Zero
                    ),
                    Normal: new(
                        X: FixedQ4816.Zero,
                        Y: FixedQ4816.One,
                        Z: FixedQ4816.Zero
                    ),
                    RestSeparation: FixedQ4816.Zero
                ),
                    new(
                    BodyA: 1,
                    BodyB: 2,
                    AnchorA: new(
                        X: FixedQ4816.FromDouble(value: 0.3d),
                        Y: FixedQ4816.FromDouble(value: 0.5d),
                        Z: FixedQ4816.Zero
                    ),
                    AnchorB: new(
                        X: FixedQ4816.FromDouble(value: 0.3d),
                        Y: FixedQ4816.FromDouble(value: -0.5d),
                        Z: FixedQ4816.Zero
                    ),
                    Normal: new(
                        X: FixedQ4816.Zero,
                        Y: FixedQ4816.One,
                        Z: FixedQ4816.Zero
                    ),
                    RestSeparation: FixedQ4816.Zero
                ),
                    new(
                    BodyA: 1,
                    BodyB: 2,
                    AnchorA: new(
                        X: FixedQ4816.FromDouble(value: -0.3d),
                        Y: FixedQ4816.FromDouble(value: 0.5d),
                        Z: FixedQ4816.Zero
                    ),
                    AnchorB: new(
                        X: FixedQ4816.FromDouble(value: -0.3d),
                        Y: FixedQ4816.FromDouble(value: -0.5d),
                        Z: FixedQ4816.Zero
                    ),
                    Normal: new(
                        X: FixedQ4816.Zero,
                        Y: FixedQ4816.One,
                        Z: FixedQ4816.Zero
                    ),
                    RestSeparation: FixedQ4816.Zero
                ),
                };
                var solver = new TwoBodySolver(options: options);
                const int Steps = 300;
                const int JitterWindow = 60;
                var heavyY = 1.5d;
                var yHistory = new double[JitterWindow];

                for (var step = 0; (step < Steps); ++step) {
                    solver.Step(
                        bodies: bodies,
                        contacts: contacts,
                        step: step
                    );
                    heavyY += ((double)heavy.DeltaPosition.Y);
                    yHistory[(step % JitterWindow)] = heavyY;
                }

                var yDrift = (yHistory.Max() - yHistory.Min());
                var outcome = ((solver.RefusalCount > 0)
                    ? "RED-KERNEL-REFUSAL"
                    : ((yDrift > 0.0002d)
                        ? "RED-POSITION-JITTER"
                        : "green"
                ));

                totalRefusals += solver.RefusalCount;

                if (solveIterations == 1) {
                    driftAtOneIteration[massRatio] = yDrift;
                } else if (solveIterations == 16) {
                    driftAtSixteenIterations[massRatio] = yDrift;
                }

                MeasurementReport.Write(line: $"{massRatio} | {solveIterations} | {outcome} | {yDrift:0.000000}");
            }
        }

        Assert.Equal(
            actual: totalRefusals,
            expected: 0
        );

        // The measured disentangling result: at these extreme ratios, MORE biased-solve iterations leave MORE drift
        // behind, not less — ruling out "the drift is an under-iterated solve" and pointing at the bit placement (or
        // the frictionless two-point rig's own unconstrained rocking mode) instead. A result the other way would mean
        // this fact's own premise (drift is disentangleable from iteration count at all) had failed.
        foreach (var massRatio in driftAtOneIteration.Keys) {
            Assert.True(
                condition: (driftAtSixteenIterations[massRatio] >= driftAtOneIteration[massRatio]),
                userMessage: $"mass ratio {massRatio}: 16 iterations left {driftAtSixteenIterations[massRatio]:0.000000} drift, less than 1 iteration's {driftAtOneIteration[massRatio]:0.000000}"
            );
        }
    }
    [Fact]
    public void TwoDynamicBodyPrecisionFloorIsMeasuredByRunningSettleJitter() {
        MeasurementReport.Section(title: "Two-dynamic-body precision floor: settle jitter vs inverse-inertia placement x mass ratio (off-center contact, real TwoBodySolver)");
        MeasurementReport.Write(line: "massRatio | inertiaPlacement | outcome | maxHeavyYDriftLast60 | maxHeavyAngularVelocityZLast60 | refusalCount");

        double[] massRatios = [1d, 10d, 100d, 1000d, 10000d];
        int[] inertiaPlacements = [40, 32, 24, 20, 16, 12, 9, 6, 4];
        const double JitterThreshold = 0.0002d;
        var totalRefusals = 0;
        // FixedRigidScales.RoomScale ships InverseInertia at 40 bits: this rig never construction-refuses there across
        // the whole ratio sweep, which is the floor this fact can actually certify (below, none of it settles under
        // JitterThreshold at ANY placement — a frictionless two-point rig has a real, non-zero rocking mode the
        // threshold does not account for, which is itself a finding, not something to paper over with a looser bound).
        var shippedPlacementConstructs = true;
        var driftAtShippedPlacement = new Dictionary<double, double>();

        foreach (var massRatio in massRatios) {
            foreach (var inertiaPlacement in inertiaPlacements) {
                var scales = new FixedRigidScales(
                    EffectiveMass: 32,
                    InverseInertia: inertiaPlacement,
                    InverseMass: 40
                );
                FixedRigidBody light;
                FixedRigidBody heavy;

                try {
                    light = SpikeBodies.Box(
                        halfExtents: new(
                            X: FixedQ4816.FromDouble(value: 0.5d),
                            Y: FixedQ4816.FromDouble(value: 0.5d),
                            Z: FixedQ4816.FromDouble(value: 0.5d)
                        ),
                        density: FixedQ4816.FromDouble(value: 100d),
                        scales: scales
                    );
                    heavy = SpikeBodies.Box(
                        halfExtents: new(
                            X: FixedQ4816.FromDouble(value: 0.5d),
                            Y: FixedQ4816.FromDouble(value: 0.5d),
                            Z: FixedQ4816.FromDouble(value: 0.5d)
                        ),
                        density: FixedQ4816.FromDouble(value: (100d * massRatio)),
                        scales: scales
                    );
                } catch (InvalidOperationException) {
                    if (inertiaPlacement == 40) {
                        shippedPlacementConstructs = false;
                    }

                    MeasurementReport.Write(line: $"{massRatio} | {inertiaPlacement} | REFUSED-AT-CONSTRUCTION | - | - | -");
                    continue;
                }

                var ground = new FixedRigidBody();
                var bodies = new[] { ground, light, heavy, };
                // Heavy rests on light at TWO off-center points (its two front/back bottom edges) rather than one: a
                // single off-center point has no way to resist the tipping torque its own lever arm creates
                // (frictionless, one normal constraint cannot fix a rotational degree of freedom the same lever arm
                // perturbs). Two points is the minimum that can hold heavy level while still routing a real lever arm
                // through the inertia kernel at each point.
                var contacts = new List<TwoBodyContact> {
                    new(
                    BodyA: 0,
                    BodyB: 1,
                    AnchorA: FixedVector3.Zero,
                    AnchorB: new(
                        X: FixedQ4816.Zero,
                        Y: FixedQ4816.FromDouble(value: -0.5d),
                        Z: FixedQ4816.Zero
                    ),
                    Normal: new(
                        X: FixedQ4816.Zero,
                        Y: FixedQ4816.One,
                        Z: FixedQ4816.Zero
                    ),
                    RestSeparation: FixedQ4816.Zero
                ),
                    new(
                    BodyA: 1,
                    BodyB: 2,
                    AnchorA: new(
                        X: FixedQ4816.FromDouble(value: 0.3d),
                        Y: FixedQ4816.FromDouble(value: 0.5d),
                        Z: FixedQ4816.Zero
                    ),
                    AnchorB: new(
                        X: FixedQ4816.FromDouble(value: 0.3d),
                        Y: FixedQ4816.FromDouble(value: -0.5d),
                        Z: FixedQ4816.Zero
                    ),
                    Normal: new(
                        X: FixedQ4816.Zero,
                        Y: FixedQ4816.One,
                        Z: FixedQ4816.Zero
                    ),
                    RestSeparation: FixedQ4816.Zero
                ),
                    new(
                    BodyA: 1,
                    BodyB: 2,
                    AnchorA: new(
                        X: FixedQ4816.FromDouble(value: -0.3d),
                        Y: FixedQ4816.FromDouble(value: 0.5d),
                        Z: FixedQ4816.Zero
                    ),
                    AnchorB: new(
                        X: FixedQ4816.FromDouble(value: -0.3d),
                        Y: FixedQ4816.FromDouble(value: -0.5d),
                        Z: FixedQ4816.Zero
                    ),
                    Normal: new(
                        X: FixedQ4816.Zero,
                        Y: FixedQ4816.One,
                        Z: FixedQ4816.Zero
                    ),
                    RestSeparation: FixedQ4816.Zero
                ),
                };
                var options = new FixedRigidSolverOptions {
                    RateHz = 60,
                    SubstepCount = 4,
                    Scales = scales,
                    Gravity = new(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.FromDouble(value: -9.81d),
                    Z: FixedQ4816.Zero
                ),
                };
                var solver = new TwoBodySolver(options: options);
                const int Steps = 300;
                const int JitterWindow = 60;
                var heavyY = 1.5d;
                var yHistory = new double[JitterWindow];
                var wHistory = new double[JitterWindow];

                for (var step = 0; (step < Steps); ++step) {
                    solver.Step(
                        bodies: bodies,
                        contacts: contacts,
                        step: step
                    );
                    heavyY += ((double)heavy.DeltaPosition.Y);

                    var slot = (step % JitterWindow);

                    yHistory[slot] = heavyY;
                    wHistory[slot] = ((double)heavy.AngularVelocity.Z);
                }

                var yDrift = (yHistory.Max() - yHistory.Min());
                var wDrift = (wHistory.Max() - wHistory.Min());
                var outcome = ((solver.RefusalCount > 0)
                    ? "RED-KERNEL-REFUSAL"
                    : ((yDrift > JitterThreshold)
                        ? "RED-POSITION-JITTER"
                        : "green"
                ));

                totalRefusals += solver.RefusalCount;

                if (inertiaPlacement == 40) {
                    driftAtShippedPlacement[massRatio] = yDrift;
                }

                MeasurementReport.Write(line: $"{massRatio} | {inertiaPlacement} | {outcome} | {yDrift:0.000000} | {wDrift:0.000000} | {solver.RefusalCount}");
            }
        }

        Assert.Equal(
            actual: totalRefusals,
            expected: 0
        );
        Assert.True(
            condition: shippedPlacementConstructs,
            userMessage: "FixedRigidScales.RoomScale's InverseInertia=40 placement must construct at every ratio in the measured sweep"
        );

        // The measured floor this rig actually yields: at the shipped placement, drift grows monotonically with mass
        // ratio rather than staying flat — the pathology the two-body precision floor exists to characterize.
        var orderedRatios = massRatios.OrderBy(keySelector: ratio => ratio).ToArray();

        for (var index = 1; (index < orderedRatios.Length); ++index) {
            Assert.True(
                condition: (driftAtShippedPlacement[orderedRatios[index]] >= driftAtShippedPlacement[orderedRatios[(index - 1)]]),
                userMessage: $"drift at ratio {orderedRatios[index]} ({driftAtShippedPlacement[orderedRatios[index]]:0.000000}) must be at least drift at ratio {orderedRatios[(index - 1)]} ({driftAtShippedPlacement[orderedRatios[(index - 1)]]:0.000000})"
            );
        }
    }
    [Fact]
    public void VelocityImpulseResolutionIsMeasuredAtTheHeaviestLightestExtreme() {
        MeasurementReport.Section(title: "Velocity/impulse resolution: heavy body's response to light body's impulse (one raw Q48.16 unit = 2^-16)");
        MeasurementReport.Write(line: "sizeRatio | densityRatio | linearSpeedBound | angularSpeedBound | closingVelocity | effectiveMassRaw | impulseRaw | heavyVelocityDeltaRaw | ulps | outcome");

        double[] linearSpeedBounds = [1d, 10d, 100d];
        double[] angularSpeedBounds = [1d, 10d, 100d];
        var scales = new FixedRigidScales(
            EffectiveMass: 32,
            InverseInertia: 40,
            InverseMass: 40
        );
        var starvedToZeroCount = 0;

        foreach (var sizeRatio in new[] { 10d, 100d, 1000d, }) {
            foreach (var densityRatio in new[] { 1d, 31.6d, 1000d, }) {
                var sizeMin = (SizeMax / sizeRatio);
                var densityMin = (DensityMax / densityRatio);
                FixedRigidBody heavy;
                FixedRigidBody light;

                try {
                    heavy = SpikeBodies.Box(
                        halfExtents: new(
                            X: FixedQ4816.FromDouble(value: SizeMax),
                            Y: FixedQ4816.FromDouble(value: SizeMax),
                            Z: FixedQ4816.FromDouble(value: SizeMax)
                        ),
                        density: FixedQ4816.FromDouble(value: DensityMax),
                        scales: scales
                    );
                    light = SpikeBodies.Box(
                        halfExtents: new(
                            X: FixedQ4816.FromDouble(value: sizeMin),
                            Y: FixedQ4816.FromDouble(value: sizeMin),
                            Z: FixedQ4816.FromDouble(value: sizeMin)
                        ),
                        density: FixedQ4816.FromDouble(value: densityMin),
                        scales: scales
                    );
                } catch (InvalidOperationException) {
                    MeasurementReport.Write(line: $"{sizeRatio} | {densityRatio} | - | - | - | REFUSED-AT-CONSTRUCTION(placement={scales.InverseMass}/{scales.InverseInertia}) | - | - | - | RED");
                    continue;
                }

                var anchorHeavy = new FixedVector3(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.FromDouble(value: -SizeMax),
                    Z: FixedQ4816.Zero
                );
                var anchorLight = new FixedVector3(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.FromDouble(value: sizeMin),
                    Z: FixedQ4816.Zero
                );
                var normal = new FixedVector3(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.One,
                    Z: FixedQ4816.Zero
                );
                var refusals = 0;

                if (!TwoBodyDynamics.TryEffectiveMass(
                    anchorA: anchorLight,
                    anchorB: anchorHeavy,
                    bodyA: light,
                    bodyB: heavy,
                    normal: normal,
                    normalMassRaw: out var effectiveMassRaw,
                    refusals: ref refusals,
                    scales: scales
                )) {
                    MeasurementReport.Write(line: $"{sizeRatio} | {densityRatio} | - | - | - | REFUSED-EFFECTIVE-MASS | - | - | - | RED");
                    continue;
                }

                foreach (var linearBound in linearSpeedBounds) {
                    foreach (var angularBound in angularSpeedBounds) {
                        // reach ~ heavy's own bounding radius, sqrt(3) x its half-extent.
                        var reach = (Math.Sqrt(d: 3d) * SizeMax);
                        var closingVelocity = (linearBound + (angularBound * reach));
                        var closingRaw = FixedQ4816.FromDouble(value: closingVelocity).Value;

                        if (!FusedArithmetic.TryMixedScaleProduct(
                            a: effectiveMassRaw,
                            fractionBitsA: scales.EffectiveMass,
                            b: closingRaw,
                            fractionBitsB: FixedQ4816.FractionBitCount,
                            fractionBitsOut: FixedQ4816.FractionBitCount,
                            result: out var impulseRaw
                        )) {
                            MeasurementReport.Write(line: $"{sizeRatio} | {densityRatio} | {linearBound} | {angularBound} | {closingVelocity:0.###} | {effectiveMassRaw} | REFUSED-IMPULSE | - | - | RED");
                            continue;
                        }

                        if (!FusedArithmetic.TryMixedScaleProduct(
                            a: heavy.InverseMassRaw,
                            fractionBitsA: scales.InverseMass,
                            b: impulseRaw,
                            fractionBitsB: FixedQ4816.FractionBitCount,
                            fractionBitsOut: FixedQ4816.FractionBitCount,
                            result: out var heavyDeltaRaw
                        )) {
                            MeasurementReport.Write(line: $"{sizeRatio} | {densityRatio} | {linearBound} | {angularBound} | {closingVelocity:0.###} | {effectiveMassRaw} | {impulseRaw} | REFUSED-VELOCITY-DELTA | - | RED");
                            continue;
                        }

                        var ulps = Math.Abs(value: heavyDeltaRaw);
                        var outcome = ((ulps == 0L)
                            ? "RED-STARVED-TO-ZERO"
                            : ((ulps < 16L)
                                ? "amber-thin"
                                : "green"
                        ));

                        if (outcome == "RED-STARVED-TO-ZERO") {
                            ++starvedToZeroCount;
                        }

                        MeasurementReport.Write(line: $"{sizeRatio} | {densityRatio} | {linearBound} | {angularBound} | {closingVelocity:0.###} | {effectiveMassRaw} | {impulseRaw} | {heavyDeltaRaw} | {ulps} | {outcome}");
                    }
                }
            }
        }

        // Measured floor: only the slowest closing speed (linear=1, angular=1) at the two most extreme corners this
        // grid names starves the heavy body's velocity delta to exactly zero raw units; every faster approach and
        // every milder size/density combination stays representable. A regression that widens this count means more
        // of the envelope has gone invisible to the heavy body's own impulse response.
        Assert.Equal(
            actual: starvedToZeroCount,
            expected: 2
        );
    }
}
