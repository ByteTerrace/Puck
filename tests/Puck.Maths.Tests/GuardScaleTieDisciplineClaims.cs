using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Proves the deliberate tie-rule correction behind <c>SecondOrderExactMath.RoundToGuardScale</c> — routed through
/// <see cref="FixedPointRounding.RoundRational"/> (ties to even) rather than the deleted half-up formula
/// <see cref="Oracles.RoundHalfUp"/> reconstructs — both WHERE the two disciplines are required to differ (an exact
/// tie at the guard scale) and that no representable <see cref="SecondOrderDynamics.Create"/>/<see cref="SecondOrderDynamics.Compile"/>
/// sample, swept across every branch and its boundaries, ever reaches one.
/// </summary>
internal static class GuardScaleTieDisciplineClaims {
    /// <summary>Proves the two disciplines are REQUIRED to differ, and exactly where: at an exact guard-scale tie
    /// whose truncated neighbor is even (ties-to-even holds it, half-up carries it), never at an odd-truncated tie
    /// (both round up, so they still agree) and never off a tie (both round to the same nearer neighbor). Swept over
    /// truncated parities 0-7 at the guard scale itself, the same pattern re-scaled to a 300-bit-wide operand pair
    /// (a common large factor multiplied into both numerator and denominator preserves the exact ratio and therefore
    /// the tie), and a battery of non-tie operands spanning small and 200+ bit magnitudes.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RoundToGuardScaleTiesVsHalfUpSurface() {
        const int guard = SecondOrderExactMath.GuardFractionBitCount;
        var widenFactor = (BigInteger.One << 300);

        for (var truncated = 0; (truncated <= 7); ++truncated) {
            // (2·truncated + 1) / 2^(guard+1), scaled by 2^guard, is exactly truncated + 0.5 — a guard-scale tie whose
            // truncated neighbor has the swept parity.
            var numerator = ((2 * ((BigInteger)truncated)) + BigInteger.One);
            var denominator = (BigInteger.One << (guard + 1));
            var newRaw = FixedPointRounding.RoundRational(numerator: numerator, denominator: denominator, fractionBitCount: guard);
            var oldRaw = Oracles.RoundHalfUp(numerator: numerator, denominator: denominator, fractionBitCount: guard);
            var isEven = (0 == (truncated & 1));

            // Half-up always carries the tie up, to truncated+1.
            if (oldRaw != (truncated + 1)) {
                return $"RoundHalfUp({numerator}, {denominator}, {guard}) = {oldRaw}, expected {(truncated + 1)} (half-up always carries an exact tie up)";
            }

            // Ties-to-even holds at an even truncated neighbor, carries up at an odd one.
            var expectedNew = (isEven ? truncated : (truncated + 1));

            if (newRaw != expectedNew) {
                return $"RoundRational({numerator}, {denominator}, {guard}) = {newRaw}, expected {expectedNew} (ties-to-even, truncated={truncated} is {(isEven ? "even" : "odd")})";
            }

            // The two disciplines are REQUIRED to differ exactly at an even-truncated tie, and REQUIRED to agree at
            // an odd-truncated one — a canary in both directions, not merely a divergence count.
            var shouldDiffer = isEven;

            if (shouldDiffer == (newRaw == oldRaw)) {
                return $"RoundRational vs RoundHalfUp at truncated={truncated} (guard-scale tie): shouldDiffer={shouldDiffer} but new={newRaw} old={oldRaw}";
            }

            // The same exact ratio re-scaled to a 300-bit-wide operand pair: an exact tie is an exact tie at any
            // magnitude, so the two disciplines diverge (or agree) identically re-scaled.
            var wideNumerator = (numerator * widenFactor);
            var wideDenominator = (denominator * widenFactor);
            var wideNewRaw = FixedPointRounding.RoundRational(numerator: wideNumerator, denominator: wideDenominator, fractionBitCount: guard);
            var wideOldRaw = Oracles.RoundHalfUp(numerator: wideNumerator, denominator: wideDenominator, fractionBitCount: guard);

            if ((wideNewRaw != newRaw) || (wideOldRaw != oldRaw)) {
                return $"re-scaling truncated={truncated}'s exact tie to a 300-bit-wide operand pair changed the rounded result: new {newRaw}->{wideNewRaw}, old {oldRaw}->{wideOldRaw}";
            }
        }

        // Off a tie, both disciplines round to the SAME nearer neighbor — the correction changes nothing away from
        // an exact half.
        (BigInteger Numerator, BigInteger Denominator)[] nonTieOperands = [
            (1, 3), (2, 3), (7, 16), (9, 16), (12345, 999983),
            (((BigInteger.One << 200) + 1), ((BigInteger.One << 202) + 3)),
            (((BigInteger.One << 250) - 7), ((BigInteger.One << 251) + 11)),
        ];

        foreach (var (numerator, denominator) in nonTieOperands) {
            var newRaw = FixedPointRounding.RoundRational(numerator: numerator, denominator: denominator, fractionBitCount: guard);
            var oldRaw = Oracles.RoundHalfUp(denominator: denominator, fractionBitCount: guard, numerator: numerator);

            if (newRaw != oldRaw) {
                return $"off a tie, RoundRational({numerator}, {denominator}, {guard}) = {newRaw} disagrees with RoundHalfUp's {oldRaw}, but the two disciplines only differ AT an exact even-truncated tie";
            }
        }

        return null;
    }
    /// <summary>Searches every guard-scale rounding <c>SecondOrderExactMath.CompilePropagator</c> performs, across a
    /// representative sweep of authored <see cref="SecondOrderDynamics.Create"/>/<see cref="SecondOrderDynamics.Compile"/>
    /// samples spanning every branch and the critical boundary, for a case where the deleted half-up formula would
    /// have rounded differently from the live ties-to-even core. Because everything downstream of a guard-scale
    /// rounding is a deterministic function of its result, an identical guard-scale raw on both disciplines PROVES —
    /// not merely suggests — an identical public Q32 propagator entry; this claim is what lets that proof stand for
    /// the whole representative domain rather than for one authored sample.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? GuardScalePublicDivergenceSearchSurface() {
        const int guard = SecondOrderExactMath.GuardFractionBitCount;
        // InitialResponse plays no part in CompilePropagator's guard-scale rounding (it feeds only the separate
        // target-velocity/retarget gains), so it is held fixed here rather than swept.
        (double FrequencyHz, double Damping)[] createSamples = [
            (0.5, 0.0), (0.5, 16.0),
            (2.0, 0.01), (2.0, 0.5),
            (2.0, 0.999), (2.0, 1.0), (2.0, 1.001), // the critical boundary, straddled
            (5.0, 2.0), (5.0, 8.0),
            (77.0, 0.3), (100.0, 16.0),
        ];
        (ulong StepTicks, ulong TicksPerSecond)[] stepSamples = [
            (1UL, 30UL), (1UL, 60UL), (1UL, 240UL), (4UL, 240UL), (1UL, 1UL), (1_000_000UL, 240UL), (1UL, 1_000_000UL),
        ];

        foreach (var (frequencyHz, damping) in createSamples) {
            SecondOrderDynamics dynamics;

            try {
                dynamics = SecondOrderDynamics.Create(
                    frequencyHz: FixedQ4816.FromDouble(value: frequencyHz),
                    dampingRatio: FixedQ4816.FromDouble(value: damping),
                    initialResponse: FixedQ4816.One
                );
            } catch (ArgumentOutOfRangeException) {
                // Too close to critical for this frequency to resolve a Q16-representable oscillation rate — no
                // propagator is ever compiled for this sample, so there is nothing here for a guard-scale rounding
                // to reach.
                continue;
            }

            foreach (var (stepTicks, ticksPerSecond) in stepSamples) {
                var stepDenominator = (((BigInteger)ticksPerSecond) << SecondOrderDynamics.CoefficientFractionBitCount);
                var decayTimeNumerator = (((BigInteger)dynamics.DecayRateRaw) * stepTicks);

                var mismatch = CheckExpNegativeReduction(
                    denominator: stepDenominator,
                    guard: guard,
                    label: $"ExpNegative(decayTime) at f={frequencyHz} zeta={damping} stepTicks={stepTicks} ticksPerSecond={ticksPerSecond}",
                    numerator: decayTimeNumerator
                );

                if (mismatch is not null) { return mismatch; }

                if (dynamics.Branch == SecondOrderDynamicsBranch.Underdamped) {
                    var angleNumerator = (((BigInteger)dynamics.OscillationRateRaw) * stepTicks);
                    var sinCosMismatch = CheckSinCosReduction(
                        denominator: stepDenominator,
                        guard: guard,
                        label: $"SinCosExact(angle) at f={frequencyHz} zeta={damping} stepTicks={stepTicks} ticksPerSecond={ticksPerSecond}",
                        numerator: angleNumerator
                    );

                    if (sinCosMismatch is not null) { return sinCosMismatch; }
                } else if (dynamics.Branch == SecondOrderDynamicsBranch.Overdamped) {
                    var p1Numerator = (((BigInteger)(dynamics.DecayRateRaw - dynamics.OscillationRateRaw)) * stepTicks);
                    var p2Numerator = (((BigInteger)(dynamics.DecayRateRaw + dynamics.OscillationRateRaw)) * stepTicks);

                    var p1Mismatch = CheckExpNegativeReduction(denominator: stepDenominator, guard: guard, label: $"ExpNegative(p1) at f={frequencyHz} zeta={damping} stepTicks={stepTicks} ticksPerSecond={ticksPerSecond}", numerator: p1Numerator);

                    if (p1Mismatch is not null) { return p1Mismatch; }

                    var p2Mismatch = CheckExpNegativeReduction(denominator: stepDenominator, guard: guard, label: $"ExpNegative(p2) at f={frequencyHz} zeta={damping} stepTicks={stepTicks} ticksPerSecond={ticksPerSecond}", numerator: p2Numerator);

                    if (p2Mismatch is not null) { return p2Mismatch; }
                }
            }
        }

        return null;
    }

    // Mirrors ExpNegative's own early exits (a zero numerator and the underflow floor never reach RoundToGuardScale
    // at all) and its halving reduction loop, from the live ExpUnderflowExponent/ResidualShift constants — a
    // transcription, not an independent algorithm, because the point is to reach the SAME (numerator, denominator)
    // pair the subject's own RoundToGuardScale call sees, not to re-derive exp another way.
    private static string? CheckExpNegativeReduction(BigInteger numerator, BigInteger denominator, int guard, string label) {
        if (numerator.IsZero || (numerator > (SecondOrderExactMath.ExpUnderflowExponent * denominator))) {
            return null;
        }

        var reducedDenominator = denominator;

        while ((numerator << SecondOrderExactMath.ResidualShift) >= reducedDenominator) {
            reducedDenominator <<= 1;
        }

        return CompareGuardRounding(denominator: reducedDenominator, guard: guard, label: label, numerator: numerator);
    }
    // Mirrors SinCosExact's own modulo-2π reduction; unlike ExpNegative it has no early exit, so every call reaches
    // RoundToGuardScale.
    private static string? CheckSinCosReduction(BigInteger numerator, BigInteger denominator, int guard, string label) {
        var twoPiNumerator = (2 * ((BigInteger)FixedQ4816.PiQ61));
        var twoPiDenominator = (BigInteger.One << FixedQ4816.PiQ61FractionBitCount);
        var reducedNumerator = ((numerator * twoPiDenominator) - ((((numerator * twoPiDenominator) / (denominator * twoPiNumerator)) * denominator) * twoPiNumerator));
        var reducedDenominator = (denominator * twoPiDenominator);

        return CompareGuardRounding(denominator: reducedDenominator, guard: guard, label: label, numerator: reducedNumerator);
    }
    private static string? CompareGuardRounding(BigInteger numerator, BigInteger denominator, int guard, string label) {
        var newRaw = FixedPointRounding.RoundRational(numerator: numerator, denominator: denominator, fractionBitCount: guard);
        var oldRaw = Oracles.RoundHalfUp(denominator: denominator, fractionBitCount: guard, numerator: numerator);

        return ((newRaw == oldRaw)
            ? null
            : $"{label}: guard-scale rounding diverges, new={newRaw} old={oldRaw} (numerator={numerator} denominator={denominator}) — a genuine public Q32 divergence is now possible for this sample and must be traced through CompilePropagator"
        );
    }
}
