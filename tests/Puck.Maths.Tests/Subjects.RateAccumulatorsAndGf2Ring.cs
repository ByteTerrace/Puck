using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- FixedRateAccumulator and FixedVector3RateAccumulator ----

    /// <summary>Maps a sampled raw onto a positive time base. Zero and the negatives are the documented refusal sites
    /// and belong to <c>rate.construction-and-refusals</c>, not to a value law; the map is TOTAL and is applied
    /// identically in subject and oracle, so every sampled pair reaches a defined comparison.</summary>
    /// <param name="raw">The sampled raw.</param>
    /// <returns>A positive time base.</returns>
    private static long RateTicksPerSecond(long raw) =>
        ((0L == raw)
            ? 1L
            : ((raw < 0L)
                ? ((long.MinValue == raw)
                    ? long.MaxValue
                    : -raw)
                : raw
        ));
    // Integrates one step and reports whether the checked quotient refused, without a lambda — the accumulator is a
    // MUTABLE struct and must be driven by reference, so the caller can read its state back after a refusal.
    private static bool RateIntegrateThrows(ref FixedRateAccumulator accumulator, long rateRaw, ulong elapsedTicks) {
        try {
            _ = accumulator.Integrate(
                ratePerSecond: Raw(value: rateRaw),
                elapsedTicks: elapsedTicks
            );
        } catch (OverflowException) {
            return true;
        }

        return false;
    }
    private static bool RateVectorIntegrateThrows(ref FixedVector3RateAccumulator accumulator, FixedVector3 ratePerSecond, ulong elapsedTicks) {
        try {
            _ = accumulator.Integrate(
                elapsedTicks: elapsedTicks,
                ratePerSecond: ratePerSecond
            );
        } catch (OverflowException) {
            return true;
        }

        return false;
    }

    /// <summary>Proves <see cref="FixedRateAccumulator.Integrate"/> against an exact rational ledger, step by step over
    /// the committed schedule: the advanced quantity and the retained remainder at EVERY step, the ledger identity at
    /// every prefix, the remainder's band, the zero-tick fixed point, and the atomicity of an overflowing step.</summary>
    /// <param name="left">The base rate raw in its first lane and the seed remainder in its second.</param>
    /// <param name="right">The time base raw in its first lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RateScheduleVsLedger(long[] left, long[] right) {
        var ticksPerSecond = RateTicksPerSecond(raw: right[0]);
        var initialRemainder = (left[1] % ticksPerSecond);
        Span<long> rateRaws = stackalloc long[RateSchedule.Length];
        Span<ulong> elapsedTicks = stackalloc ulong[RateSchedule.Length];

        for (var step = 0; (step < RateSchedule.Length); ++step) {
            rateRaws[step] = unchecked((left[0] * RateSchedule[step].RateScale));
            elapsedTicks[step] = RateSchedule[step].Ticks;
        }

        var ledger = Oracles.RateIntegrationLedger(
            elapsedTicks: elapsedTicks,
            initialRemainder: initialRemainder,
            rateRaws: rateRaws,
            ticksPerSecond: ticksPerSecond
        );
        var accumulator = FixedRateAccumulator.FromRemainder(
            remainder: initialRemainder,
            ticksPerSecond: ticksPerSecond
        );

        if (accumulator.Remainder != initialRemainder) { return "the restored accumulator did not carry the seed remainder"; }
        if (accumulator.TicksPerSecond != ticksPerSecond) { return "the restored accumulator did not carry the time base"; }

        var advancedSum = BigInteger.Zero;
        var exactSum = BigInteger.Zero;

        for (var step = 0; (step < RateSchedule.Length); ++step) {
            var (expectedAdvanced, expectedRemainder) = ledger[step];
            var previousRemainder = accumulator.Remainder;

            if (!WithinCarrier(value: expectedAdvanced)) {
                if (!RateIntegrateThrows(
                    accumulator: ref accumulator,
                    rateRaw: rateRaws[step],
                    elapsedTicks: elapsedTicks[step]
                )) { return $"step {step} advanced {expectedAdvanced}, which the carrier cannot hold, without refusing"; }
                if (accumulator.Remainder != previousRemainder) { return $"a refused step moved the remainder from {previousRemainder} to {accumulator.Remainder}"; }
                if (accumulator.TicksPerSecond != ticksPerSecond) { return "a refused step moved the time base"; }

                return null;
            }

            var advanced = accumulator.Integrate(
                ratePerSecond: Raw(value: rateRaws[step]),
                elapsedTicks: elapsedTicks[step]
            );

            if (advanced.Value != expectedAdvanced) { return $"step {step} advanced {advanced.Value}, expected {expectedAdvanced}"; }
            if (accumulator.Remainder != expectedRemainder) { return $"step {step} retained {accumulator.Remainder}, expected {expectedRemainder}"; }
            if (BigInteger.Abs(value: new BigInteger(value: accumulator.Remainder)) >= ticksPerSecond) { return $"step {step} left the remainder {accumulator.Remainder} outside (−{ticksPerSecond}, {ticksPerSecond})"; }
            if (
                (0UL == elapsedTicks[step]) &&
                ((0L != advanced.Value) || (accumulator.Remainder != previousRemainder))
            ) { return $"the zero-tick step {step} was not a fixed point"; }

            advancedSum += expectedAdvanced;
            exactSum += (new BigInteger(value: rateRaws[step]) * elapsedTicks[step]);

            if (((ticksPerSecond * advancedSum) + accumulator.Remainder) != (exactSum + initialRemainder)) { return $"the ledger identity failed at the prefix ending in step {step}"; }
        }

        return null;
    }
    /// <summary>Proves the scalar integrator's construction surface: the time-base refusal ladder, the loud
    /// default-initialized state, <see cref="FixedRateAccumulator.FromRemainder"/>'s admission band tested from both
    /// sides at every ladder base, that a restored accumulator continues the captured integration exactly, and
    /// <see cref="FixedRateAccumulator.Reset"/>'s selectivity.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RateConstructionAndRefusals() {
        foreach (var refused in RateRefusedBases) {
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = new FixedRateAccumulator(ticksPerSecond: refused),
                paramName: "ticksPerSecond"
            )) { return $"the constructor accepted the time base {refused}"; }
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = FixedRateAccumulator.FromRemainder(
                    remainder: 0L,
                    ticksPerSecond: refused
                ),
                paramName: "ticksPerSecond"
            )) { return $"FromRemainder accepted the time base {refused}"; }
        }

        foreach (var admitted in RateAdmittedBases) {
            var accumulator = new FixedRateAccumulator(ticksPerSecond: admitted);

            if (accumulator.TicksPerSecond != admitted) { return $"the time base {admitted} did not read back"; }
            if (0L != accumulator.Remainder) { return $"a fresh accumulator at base {admitted} carries the remainder {accumulator.Remainder}"; }
        }

        var unbound = default(FixedRateAccumulator);

        if (0L != unbound.TicksPerSecond) { return "the default-initialized accumulator does not report a zero time base"; }
        if (0L != unbound.Remainder) { return "the default-initialized accumulator does not report a zero remainder"; }
        if (!Throws<InvalidOperationException>(action: () => _ = unbound.Integrate(
            ratePerSecond: FixedQ4816.One,
            elapsedTicks: 1UL
        ))) { return "the default-initialized accumulator did not refuse to integrate"; }

        foreach (var ticksPerSecond in RateBaseLadder) {
            foreach (var remainder in new[] { (ticksPerSecond - 1L), -(ticksPerSecond - 1L), 0L }) {
                var restored = FixedRateAccumulator.FromRemainder(
                    remainder: remainder,
                    ticksPerSecond: ticksPerSecond
                );

                if (restored.Remainder != remainder) { return $"the remainder {remainder} did not read back at base {ticksPerSecond}"; }
                if (restored.TicksPerSecond != ticksPerSecond) { return $"the time base {ticksPerSecond} did not read back after a restore"; }
            }

            foreach (var remainder in new[] { ticksPerSecond, -ticksPerSecond, long.MaxValue }) {
                if (!Throws<ArgumentOutOfRangeException>(
                    action: () => _ = FixedRateAccumulator.FromRemainder(
                        remainder: remainder,
                        ticksPerSecond: ticksPerSecond
                    ),
                    paramName: "remainder"
                )) {
                    return $"FromRemainder accepted the remainder {remainder} at base {ticksPerSecond}";
                }
            }
        }

        return (RateRestoreContinuesFailure() ?? RateResetFailure());
    }

    // A restored accumulator CONTINUES the captured integration exactly: the tail of the committed schedule run against
    // FromRemainder of the remainder the full run reached reproduces the full run's remaining steps.
    private static string? RateRestoreContinuesFailure() {
        const long TicksPerSecond = 1000L;
        const long RateRaw = 100000L;
        const int Cut = 5;
        var full = new FixedRateAccumulator(ticksPerSecond: TicksPerSecond);
        var advanced = new long[RateSchedule.Length];
        var remainderAtCut = 0L;

        for (var step = 0; (step < RateSchedule.Length); ++step) {
            advanced[step] = full.Integrate(
                ratePerSecond: Raw(value: (RateRaw * RateSchedule[step].RateScale)),
                elapsedTicks: RateSchedule[step].Ticks
            ).Value;

            if (step == (Cut - 1)) { remainderAtCut = full.Remainder; }
        }

        var resumed = FixedRateAccumulator.FromRemainder(
            remainder: remainderAtCut,
            ticksPerSecond: TicksPerSecond
        );

        for (var step = Cut; (step < RateSchedule.Length); ++step) {
            var tail = resumed.Integrate(
                ratePerSecond: Raw(value: (RateRaw * RateSchedule[step].RateScale)),
                elapsedTicks: RateSchedule[step].Ticks
            ).Value;

            if (tail != advanced[step]) { return $"the resumed run advanced {tail} at step {step}, expected {advanced[step]}"; }
        }

        if (resumed.Remainder != full.Remainder) { return "the resumed run closed on a different remainder"; }

        return null;
    }
    private static string? RateResetFailure() {
        const long TicksPerSecond = 60L;
        var accumulator = new FixedRateAccumulator(ticksPerSecond: TicksPerSecond);

        _ = accumulator.Integrate(
            ratePerSecond: Raw(value: 65537L),
            elapsedTicks: 7UL
        );

        if (0L == accumulator.Remainder) { return "the reset witness never accumulated a remainder"; }

        accumulator.Reset();

        if (0L != accumulator.Remainder) { return "Reset did not clear the remainder"; }
        if (TicksPerSecond != accumulator.TicksPerSecond) { return "Reset did not preserve the time base"; }

        var fresh = new FixedRateAccumulator(ticksPerSecond: TicksPerSecond);

        if (accumulator.Integrate(
            ratePerSecond: Raw(value: 65537L),
            elapsedTicks: 7UL
        ) != fresh.Integrate(
            ratePerSecond: Raw(value: 65537L),
            elapsedTicks: 7UL
        )) { return "a reset accumulator does not integrate as a fresh one does"; }

        return null;
    }

    /// <summary>Proves the documented headline as the ledger invariant reads it: a constant rate advances by exactly its
    /// own raw after <c>ticksPerSecond</c> one-tick calls, with a closing remainder of exactly zero, for either sign and
    /// for an arbitrary swept rate; that a base above the fraction scale advances exactly zero for many steps; and that
    /// the two routes agree for a single-signed schedule.</summary>
    /// <param name="left">The swept rate raw in its first lane.</param>
    /// <param name="right">The ladder base selector in its first lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RateUnitAdvanceExact(long[] left, long[] right) {
        var ticksPerSecond = RateBaseLadder[((int)(((ulong)right[0]) % ((ulong)RateBaseLadder.Length)))];

        if (ticksPerSecond <= RateLoopLimit) {
            return (RateFullTurnFailure(
                rateRaw: OneRaw,
                ticksPerSecond: ticksPerSecond
            )
                ?? (RateFullTurnFailure(
                rateRaw: -OneRaw,
                ticksPerSecond: ticksPerSecond
            )
                ?? RateFullTurnFailure(
                ticksPerSecond: ticksPerSecond,
                rateRaw: left[0]
            )));
        }

        return (RatePrefixFailure(
            rateRaw: OneRaw,
            ticksPerSecond: ticksPerSecond
        )
            ?? (RatePrefixFailure(
            rateRaw: -OneRaw,
            ticksPerSecond: ticksPerSecond
        )
            ?? RatePrefixFailure(
            ticksPerSecond: ticksPerSecond,
            rateRaw: left[0]
        )));
    }

    // N one-tick calls at one rate reach exactly that rate with a closing remainder of exactly zero, and one N-tick call
    // reaches the same pair — the route agreement, claimed for a single-signed schedule only. Every INTERMEDIATE delta
    // and remainder is checked against the ledger re-derived in BigInteger, not just the closing pair: the closing pair
    // alone is satisfied by any schedule that happens to land right, and the negative arm's asymmetry (at N = 3 and
    // R = −2¹⁶ the deltas are −21845, −21845, −21846 against the remainders −1, −2, 0) lives entirely in the middle.
    private static string? RateFullTurnFailure(long ticksPerSecond, long rateRaw) {
        var accumulator = new FixedRateAccumulator(ticksPerSecond: ticksPerSecond);
        var total = BigInteger.Zero;
        var remainder = BigInteger.Zero;

        for (var tick = 0L; (tick < ticksPerSecond); ++tick) {
            var numerator = (remainder + rateRaw);
            var quotient = BigInteger.Divide(
                dividend: BigInteger.Abs(value: numerator),
                divisor: ticksPerSecond
            );
            var expected = ((numerator.Sign < 0)
                ? -quotient
                : quotient
            );
            var step = accumulator.Integrate(
                ratePerSecond: Raw(value: rateRaw),
                elapsedTicks: 1UL
            ).Value;

            remainder = (numerator - (expected * ticksPerSecond));
            total += step;

            if (step != expected) { return $"tick {tick} at rate {rateRaw} over base {ticksPerSecond} advanced {step}, expected {expected}"; }
            if (accumulator.Remainder != remainder) { return $"tick {tick} at rate {rateRaw} over base {ticksPerSecond} retained {accumulator.Remainder}, expected {remainder}"; }
        }

        if (total != rateRaw) { return $"{ticksPerSecond} one-tick calls at rate {rateRaw} advanced {total}"; }
        if (0L != accumulator.Remainder) { return $"{ticksPerSecond} one-tick calls at rate {rateRaw} closed on the remainder {accumulator.Remainder}"; }

        accumulator.Reset();

        if (
            (0L != accumulator.Remainder) ||
            (ticksPerSecond != accumulator.TicksPerSecond)
        ) { return "Reset did not clear the remainder while preserving the base"; }

        var single = new FixedRateAccumulator(ticksPerSecond: ticksPerSecond);
        var advanced = single.Integrate(
            ratePerSecond: Raw(value: rateRaw),
            elapsedTicks: ((ulong)ticksPerSecond)
        );

        if (advanced.Value != rateRaw) { return $"one {ticksPerSecond}-tick call at rate {rateRaw} advanced {advanced.Value}"; }
        if (0L != single.Remainder) { return $"one {ticksPerSecond}-tick call at rate {rateRaw} closed on the remainder {single.Remainder}"; }

        return null;
    }
    // At a base above the fraction scale the whole unit arrives LATE: what a bounded prefix pins is that after k
    // one-tick calls the total is exactly the truncated quotient of k·rate by the base and the remainder is exactly the
    // rest — so an implementation that dropped the remainder would return zero here forever.
    private static string? RatePrefixFailure(long ticksPerSecond, long rateRaw) {
        var accumulator = new FixedRateAccumulator(ticksPerSecond: ticksPerSecond);
        var total = BigInteger.Zero;

        for (var step = 1L; (step <= RatePrefixSteps); ++step) {
            var tick = accumulator.Integrate(
                ratePerSecond: Raw(value: rateRaw),
                elapsedTicks: 1UL
            ).Value;

            total += tick;

            // The claim is about a rate that cannot be represented per tick: the FIRST tick of a one-unit-per-second
            // rate over a base above the fraction scale advances exactly nothing and retains the whole rate, so an
            // integrator that dropped the tail would sit at zero forever instead of letting the unit arrive late.
            if (
                (1L == step) &&
                (OneRaw == rateRaw) &&
                ((0L != tick) || (accumulator.Remainder != rateRaw))
            ) {
                return $"the first one-tick call at a unit-per-second rate over base {ticksPerSecond} advanced {tick} retaining {accumulator.Remainder}";
            }

            var exact = (new BigInteger(value: rateRaw) * step);
            var expectedTotal = (exact / ticksPerSecond);

            if (total != expectedTotal) { return $"after {step} one-tick calls at rate {rateRaw} over base {ticksPerSecond} the total is {total}, expected {expectedTotal}"; }
            if (accumulator.Remainder != (exact - (expectedTotal * ticksPerSecond))) { return $"after {step} one-tick calls the remainder is {accumulator.Remainder}, expected {(exact - (expectedTotal * ticksPerSecond))}"; }
        }

        var single = new FixedRateAccumulator(ticksPerSecond: ticksPerSecond);
        var advanced = single.Integrate(
            ratePerSecond: Raw(value: rateRaw),
            elapsedTicks: ((ulong)RatePrefixSteps)
        );

        if (advanced.Value != total) { return $"one {RatePrefixSteps}-tick call advanced {advanced.Value}, not the {total} the one-tick route reached"; }
        if (single.Remainder != accumulator.Remainder) { return "the two routes closed on different remainders"; }

        return null;
    }

    /// <summary>Proves the vector integrator is three independent scalar ledgers over one shared base: every axis
    /// against its own exact ledger and against a separate scalar accumulator, no cross-contamination, the four resets'
    /// selectivity, the atomicity of an overflowing axis, and the loud default-initialized state.</summary>
    /// <param name="left">The three base rate raws in its first three lanes.</param>
    /// <param name="right">The time base raw in its first lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RateVectorAxesIndependent(long[] left, long[] right) {
        var ticksPerSecond = RateTicksPerSecond(raw: right[0]);
        var steps = RateSchedule.Length;
        Span<ulong> elapsedTicks = stackalloc ulong[steps];
        var rateRaws = new long[3][];

        for (var axis = 0; (axis < 3); ++axis) { rateRaws[axis] = new long[steps]; }

        for (var step = 0; (step < steps); ++step) {
            elapsedTicks[step] = RateSchedule[step].Ticks;

            for (var axis = 0; (axis < 3); ++axis) { rateRaws[axis][step] = unchecked((left[axis] * RateSchedule[step].RateScale)); }
        }

        var ledgerX = Oracles.RateIntegrationLedger(
            rateRaws: rateRaws[0],
            elapsedTicks: elapsedTicks,
            ticksPerSecond: ticksPerSecond,
            initialRemainder: 0L
        );
        var ledgerY = Oracles.RateIntegrationLedger(
            rateRaws: rateRaws[1],
            elapsedTicks: elapsedTicks,
            ticksPerSecond: ticksPerSecond,
            initialRemainder: 0L
        );
        var ledgerZ = Oracles.RateIntegrationLedger(
            rateRaws: rateRaws[2],
            elapsedTicks: elapsedTicks,
            ticksPerSecond: ticksPerSecond,
            initialRemainder: 0L
        );
        var vector = new FixedVector3RateAccumulator(ticksPerSecond: ticksPerSecond);
        var scalarX = new FixedRateAccumulator(ticksPerSecond: ticksPerSecond);
        var scalarY = new FixedRateAccumulator(ticksPerSecond: ticksPerSecond);
        var scalarZ = new FixedRateAccumulator(ticksPerSecond: ticksPerSecond);

        for (var step = 0; (step < steps); ++step) {
            var rate = Space(
                x: rateRaws[0][step],
                y: rateRaws[1][step],
                z: rateRaws[2][step]
            );
            var representable = (WithinCarrier(value: ledgerX[step].Advanced) && WithinCarrier(value: ledgerY[step].Advanced) && WithinCarrier(value: ledgerZ[step].Advanced));
            var remainders = (vector.XRemainder, vector.YRemainder, vector.ZRemainder);

            if (!representable) {
                if (!RateVectorIntegrateThrows(
                    accumulator: ref vector,
                    ratePerSecond: rate,
                    elapsedTicks: elapsedTicks[step]
                )) { return $"step {step} overflowed an axis without refusing"; }
                if ((vector.XRemainder, vector.YRemainder, vector.ZRemainder) != remainders) { return "a refused vector step moved a remainder"; }
                if (vector.TicksPerSecond != ticksPerSecond) { return "a refused vector step moved the time base"; }

                return RateVectorSelectivityFailure();
            }

            var advanced = vector.Integrate(
                ratePerSecond: rate,
                elapsedTicks: elapsedTicks[step]
            );

            if (advanced.X.Value != ledgerX[step].Advanced) { return $"step {step}'s X advance is {advanced.X.Value}, expected {ledgerX[step].Advanced}"; }
            if (advanced.Y.Value != ledgerY[step].Advanced) { return $"step {step}'s Y advance is {advanced.Y.Value}, expected {ledgerY[step].Advanced}"; }
            if (advanced.Z.Value != ledgerZ[step].Advanced) { return $"step {step}'s Z advance is {advanced.Z.Value}, expected {ledgerZ[step].Advanced}"; }
            if (vector.XRemainder != ledgerX[step].Remainder) { return $"step {step}'s X remainder is {vector.XRemainder}, expected {ledgerX[step].Remainder}"; }
            if (vector.YRemainder != ledgerY[step].Remainder) { return $"step {step}'s Y remainder is {vector.YRemainder}, expected {ledgerY[step].Remainder}"; }
            if (vector.ZRemainder != ledgerZ[step].Remainder) { return $"step {step}'s Z remainder is {vector.ZRemainder}, expected {ledgerZ[step].Remainder}"; }

            var stepX = scalarX.Integrate(
                ratePerSecond: Raw(value: rateRaws[0][step]),
                elapsedTicks: elapsedTicks[step]
            );
            var stepY = scalarY.Integrate(
                ratePerSecond: Raw(value: rateRaws[1][step]),
                elapsedTicks: elapsedTicks[step]
            );
            var stepZ = scalarZ.Integrate(
                ratePerSecond: Raw(value: rateRaws[2][step]),
                elapsedTicks: elapsedTicks[step]
            );

            if (
                (advanced.X != stepX) ||
                (advanced.Y != stepY) ||
                (advanced.Z != stepZ)
            ) { return $"step {step} disagrees with three separate scalar accumulators"; }
            if (
                (vector.XRemainder != scalarX.Remainder) ||
                (vector.YRemainder != scalarY.Remainder) ||
                (vector.ZRemainder != scalarZ.Remainder)
            ) { return $"step {step}'s retained remainders disagree with three separate scalar accumulators"; }
        }

        // The three axes never cross-contaminate: a schedule that drives one axis alone leaves the other two at exactly
        // zero, on both the advance and the retained remainder.
        for (var driven = 0; (driven < 3); ++driven) {
            var solo = new FixedVector3RateAccumulator(ticksPerSecond: ticksPerSecond);

            for (var step = 0; (step < steps); ++step) {
                var rate = Space(
                    x: ((0 == driven)
                    ? rateRaws[0][step]
                    : 0L),
                    y: ((1 == driven)
                    ? rateRaws[1][step]
                    : 0L),
                    z: ((2 == driven)
                    ? rateRaws[2][step]
                    : 0L)
                );

                if (!WithinCarrier(value: ((0 == driven)
                    ? ledgerX
                    : ((1 == driven)
                        ? ledgerY
                        : ledgerZ))[step].Advanced)) { break; }

                var advanced = solo.Integrate(
                    ratePerSecond: rate,
                    elapsedTicks: elapsedTicks[step]
                );

                if (
                    (0 != driven) &&
                    ((0L != advanced.X.Value) || (0L != solo.XRemainder))
                ) { return $"driving axis {driven} moved X"; }
                if (
                    (1 != driven) &&
                    ((0L != advanced.Y.Value) || (0L != solo.YRemainder))
                ) { return $"driving axis {driven} moved Y"; }
                if (
                    (2 != driven) &&
                    ((0L != advanced.Z.Value) || (0L != solo.ZRemainder))
                ) { return $"driving axis {driven} moved Z"; }
            }
        }

        var unbound = default(FixedVector3RateAccumulator);

        if (0L != unbound.TicksPerSecond) { return "the default-initialized vector accumulator does not report a zero time base"; }
        if (!Throws<InvalidOperationException>(action: () => _ = unbound.Integrate(
            ratePerSecond: FixedVector3.Zero,
            elapsedTicks: 1UL
        ))) { return "the default-initialized vector accumulator did not refuse to integrate"; }

        return RateVectorSelectivityFailure();
    }

    // The four resets are exactly as selective as their names, and an overflowing axis leaves a coherent vector state
    // rather than a torn one — both read from a state whose three remainders are DISTINCT and non-zero, so a transposed
    // field fails here.
    private static string? RateVectorSelectivityFailure() {
        const long TicksPerSecond = 1000L;
        var seeded = FixedVector3RateAccumulator.FromRemainders(
            ticksPerSecond: TicksPerSecond,
            xRemainder: 1L,
            yRemainder: -2L,
            zRemainder: 3L
        );
        var byX = seeded;
        var byY = seeded;
        var byZ = seeded;
        var byAll = seeded;

        byX.ResetX();
        byY.ResetY();
        byZ.ResetZ();
        byAll.Reset();

        if (
            (0L != byX.XRemainder) ||
            (-2L != byX.YRemainder) ||
            (3L != byX.ZRemainder) ||
            (TicksPerSecond != byX.TicksPerSecond)
        ) { return "ResetX is not selective"; }
        if (
            (1L != byY.XRemainder) ||
            (0L != byY.YRemainder) ||
            (3L != byY.ZRemainder) ||
            (TicksPerSecond != byY.TicksPerSecond)
        ) { return "ResetY is not selective"; }
        if (
            (1L != byZ.XRemainder) ||
            (-2L != byZ.YRemainder) ||
            (0L != byZ.ZRemainder) ||
            (TicksPerSecond != byZ.TicksPerSecond)
        ) { return "ResetZ is not selective"; }
        if (
            (0L != byAll.XRemainder) ||
            (0L != byAll.YRemainder) ||
            (0L != byAll.ZRemainder) ||
            (TicksPerSecond != byAll.TicksPerSecond)
        ) { return "Reset did not clear all three axes while preserving the base"; }

        var atomic = seeded;

        if (!RateVectorIntegrateThrows(
            accumulator: ref atomic,
            ratePerSecond: Space(
                x: 0L,
                y: 0L,
                z: long.MaxValue
            ),
            elapsedTicks: 2000UL
        )) { return "an overflowing Z axis did not refuse"; }
        if (
            (1L != atomic.XRemainder) ||
            (-2L != atomic.YRemainder) ||
            (3L != atomic.ZRemainder)
        ) { return "an overflowing Z axis left a torn vector state"; }

        return null;
    }

    /// <summary>Proves the vector integrator's construction surface: the time-base refusal ladder, that
    /// <see cref="FixedVector3RateAccumulator.FromRemainders"/> refuses when ANY axis leaves the band and admits the
    /// boundary triple, the per-axis parameter name each refusal reports and the X, Y, Z order it reports them in, and
    /// that a restored vector accumulator continues each axis's captured integration exactly.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RateVectorConstructionAndRefusals() {
        foreach (var refused in RateRefusedBases) {
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = new FixedVector3RateAccumulator(ticksPerSecond: refused),
                paramName: "ticksPerSecond"
            )) { return $"the vector constructor accepted the time base {refused}"; }
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = FixedVector3RateAccumulator.FromRemainders(
                    ticksPerSecond: refused,
                    xRemainder: 0L,
                    yRemainder: 0L,
                    zRemainder: 0L
                ),
                paramName: "ticksPerSecond"
            )) { return $"FromRemainders accepted the time base {refused}"; }
        }

        foreach (var admitted in RateAdmittedBases) {
            var accumulator = new FixedVector3RateAccumulator(ticksPerSecond: admitted);

            if (accumulator.TicksPerSecond != admitted) { return $"the vector time base {admitted} did not read back"; }
            if (
                (0L != accumulator.XRemainder) ||
                (0L != accumulator.YRemainder) ||
                (0L != accumulator.ZRemainder)
            ) { return $"a fresh vector accumulator at base {admitted} carries a remainder"; }
        }

        foreach (var ticksPerSecond in RateBaseLadder) {
            var boundary = FixedVector3RateAccumulator.FromRemainders(
                ticksPerSecond: ticksPerSecond,
                xRemainder: (ticksPerSecond - 1L),
                yRemainder: 0L,
                zRemainder: -(ticksPerSecond - 1L)
            );

            if (boundary.XRemainder != (ticksPerSecond - 1L)) { return $"the boundary X remainder did not read back at base {ticksPerSecond}"; }
            if (0L != boundary.YRemainder) { return $"the boundary Y remainder did not read back at base {ticksPerSecond}"; }
            if (boundary.ZRemainder != -(ticksPerSecond - 1L)) { return $"the boundary Z remainder did not read back at base {ticksPerSecond}"; }
            if (boundary.TicksPerSecond != ticksPerSecond) { return $"the boundary triple did not carry the base {ticksPerSecond}"; }

            // One axis at a time, so all three validation calls are reached and each refusal must name ITS OWN axis —
            // the whole point of the per-axis parameter names is that a caller restoring a snapshot learns which axis
            // was rejected.
            for (var axis = 0; (axis < 3); ++axis) {
                var x = ((0 == axis)
                    ? ticksPerSecond
                    : 0L
                );
                var y = ((1 == axis)
                    ? ticksPerSecond
                    : 0L
                );
                var z = ((2 == axis)
                    ? -ticksPerSecond
                    : 0L
                );
                var expected = RateAxisParameterNames[axis];

                if (!Throws<ArgumentOutOfRangeException>(
                    action: () => _ = FixedVector3RateAccumulator.FromRemainders(
                        ticksPerSecond: ticksPerSecond,
                        xRemainder: x,
                        yRemainder: y,
                        zRemainder: z
                    ),
                    paramName: expected
                )) {
                    return $"FromRemainders accepted an out-of-band remainder on axis {axis} at base {ticksPerSecond}, or named a parameter other than '{expected}'";
                }
            }

            // The axes are validated in X, Y, Z order, so a refusal reports the FIRST axis out of band.
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = FixedVector3RateAccumulator.FromRemainders(
                    ticksPerSecond: ticksPerSecond,
                    xRemainder: ticksPerSecond,
                    yRemainder: ticksPerSecond,
                    zRemainder: -ticksPerSecond
                ),
                paramName: "xRemainder"
            )) {
                return $"three out-of-band axes at base {ticksPerSecond} did not refuse naming 'xRemainder'";
            }

            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = FixedVector3RateAccumulator.FromRemainders(
                    ticksPerSecond: ticksPerSecond,
                    xRemainder: 0L,
                    yRemainder: ticksPerSecond,
                    zRemainder: -ticksPerSecond
                ),
                paramName: "yRemainder"
            )) {
                return $"out-of-band Y and Z axes at base {ticksPerSecond} did not refuse naming 'yRemainder'";
            }
        }

        return RateVectorRestoreContinuesFailure();
    }

    private static string? RateVectorRestoreContinuesFailure() {
        const long TicksPerSecond = 1000L;
        const int Cut = 5;
        var full = new FixedVector3RateAccumulator(ticksPerSecond: TicksPerSecond);
        var advanced = new FixedVector3[RateSchedule.Length];
        var cutRemainders = (0L, 0L, 0L);

        for (var step = 0; (step < RateSchedule.Length); ++step) {
            advanced[step] = full.Integrate(
                ratePerSecond: RateVectorRate(scale: RateSchedule[step].RateScale),
                elapsedTicks: RateSchedule[step].Ticks
            );

            if (step == (Cut - 1)) { cutRemainders = (full.XRemainder, full.YRemainder, full.ZRemainder); }
        }

        var resumed = FixedVector3RateAccumulator.FromRemainders(
            ticksPerSecond: TicksPerSecond,
            xRemainder: cutRemainders.Item1,
            yRemainder: cutRemainders.Item2,
            zRemainder: cutRemainders.Item3
        );

        for (var step = Cut; (step < RateSchedule.Length); ++step) {
            if (resumed.Integrate(
                ratePerSecond: RateVectorRate(scale: RateSchedule[step].RateScale),
                elapsedTicks: RateSchedule[step].Ticks
            ) != advanced[step]) {
                return $"the resumed vector run diverged at step {step}";
            }
        }

        if ((resumed.XRemainder, resumed.YRemainder, resumed.ZRemainder) != (full.XRemainder, full.YRemainder, full.ZRemainder)) { return "the resumed vector run closed on different remainders"; }

        return null;
    }
    private static FixedVector3 RateVectorRate(int scale) =>
        Space(
            x: (100000L * scale),
            y: (-70001L * scale),
            z: (3L * scale)
        );

    // The schedule every rate law is driven through, declared once and part of the law: it changes sign mid-run, carries
    // a zero-tick step, a many-tick step, a rate that is no multiple of the base, and a step whose numerator divides
    // exactly. The rate of each step is the swept operand times that step's scale, so the SHAPE is fixed and
    // deterministic while the magnitudes sweep.
    private static readonly (int RateScale, ulong Ticks)[] RateSchedule = [
        (1, 1UL), (1, 1UL), (1, 1UL), (1, 1UL),
        (1, 0UL),
        (-1, 1UL), (-1, 3UL),
        (1, 7UL), (3, 5UL), (-2, 11UL),
        (1, 1UL), (1, 1UL),
    ];
    // T7 — the time bases the unit-advance law runs at: odd, even, prime, a power of two, the two the engine actually
    // uses, and one above 2¹⁶ so the per-step quotient is genuinely zero for a one-unit-per-second rate.
    private static readonly long[] RateBaseLadder = [1L, 2L, 3L, 7L, 60L, 64L, 1000L, 65537L, 1_000_000_007L];
    // T8 — the construction refusal and admission ladders.
    private static readonly long[] RateRefusedBases = [0L, -1L, -65536L, long.MinValue];
    private static readonly long[] RateAdmittedBases = [1L, 2L, 60L, long.MaxValue];
    // The parameter FromRemainders names when it rejects each axis, indexed by axis, so the refusal probe asserts the
    // axis it drove out of band rather than a name shared by all three.
    private static readonly string[] RateAxisParameterNames = ["xRemainder", "yRemainder", "zRemainder"];

    // The largest base the exhaustive one-tick loop runs at, and the bounded prefix the two larger bases take instead:
    // a base of a billion cannot be looped, and what a prefix pins there is the exact truncated quotient at every step.
    private const long RateLoopLimit = 1000L;
    private const long RatePrefixSteps = 1000L;

    // ---- the GF(2)[t] ring ----

    // The domain raw reinterpreted as the packed coefficient carrier, the established UnsignedRaw fold: every one of
    // the sixty-four bits is a legal coefficient, so nothing is folded away.
    private static BinaryPolynomial Poly(long raw) =>
        new(bits: UnsignedRaw(raw: raw));

    // The low sixty-four coefficients — the packed carrier's own window on an unbounded product.
    private static readonly BigInteger BinaryCarrierMask = ((BigInteger.One << 64) - BigInteger.One);
    // The written forms, hand-derived from the convention: the two constants, the indeterminate, the degree-one pair,
    // a sparse pentanomial and the bare top monomial. The dense word is stated separately, by its shape.
    private static readonly (ulong Bits, string Text)[] BinaryPolynomialTexts = [
        (0UL, "0"),
        (1UL, "1"),
        (2UL, "t"),
        (3UL, "t+1"),
        (0b100101UL, "t^5+t^2+1"),
        ((1UL << 63), "t^63"),
    ];
    // The published carryless-multiply reference vectors: two sixty-four-bit operands and the two halves of their
    // exact one-hundred-and-twenty-eight-bit product.
    private static readonly (ulong Left, ulong Right, ulong Low, ulong High)[] BinaryCarrylessVectors = [
        (0x63746F725D53475DUL, 0x5B477565726F6E5DUL, 0x929633D5D36F0451UL, 0x1D4D84C85C3440C0UL),
        (0x7B5B546573745665UL, 0x5B477565726F6E5DUL, 0xBABF262DF4B7D5C9UL, 0x1A2BF6DB3A30862FUL),
        (0x63746F725D53475DUL, 0x4869285368617929UL, 0x7FA540AC2A281315UL, 0x1BD17C8D556AB5A1UL),
        (0x7B5B546573745665UL, 0x4869285368617929UL, 0xD66EE03E410FD4EDUL, 0x1D1E1F2C592E7C45UL),
    ];
    // The shift-count ladder: both sides of the carrier-width seam at 63/64, both sides of the half-width seam, and
    // the two counts a masked shift would wrap back into range.
    private static readonly int[] BinaryShiftCounts = [0, 1, 7, 8, 31, 32, 62, 63, 64, 65, 127, int.MaxValue];
    private static readonly int[] BinaryShiftRefusals = [-1, -64, int.MinValue];
    // OEIS A001037: the number of monic irreducible polynomials of degree n over the two-element field.
    private static readonly int[] BinaryIrreducibleCounts = [2, 1, 2, 3, 6, 9, 18, 30, 56, 99, 186, 335, 630, 1161, 2182, 4080];
    // The catalog moduli, transcribed from the field wing's own table: the minimum-weight irreducible pentanomials at
    // degrees 8, 16 and 32.
    private static readonly (int Degree, ulong Bits)[] BinaryCatalogModuli = [
        (8, 0x11BUL),
        (16, 0x1002BUL),
        (32, 0x10000008DUL),
    ];
    // Factor pairs, each of degree at least one, whose ORACLE-formed products are reducible by definition. The
    // products reach degrees 2, 3, 16, 32, 40, 48, 55 and 63 — the last three above anything exhaustive trial
    // division can decide — and one pair plants a zero constant term.
    private static readonly (ulong Left, ulong Right)[] BinaryReducibleFactorPairs = [
        (0x3UL, 0x3UL),
        (0x3UL, 0x7UL),
        (0x2UL, 0x11BUL),
        (0x11BUL, 0x11BUL),
        (0x1002BUL, 0x1002BUL),
        (0x1002BUL, 0x100001BUL),
        (0x10000008DUL, 0x1002BUL),
        (0x10000008DUL, 0x800011UL),
        (0x10000008DUL, 0x80000009UL),
    ];
    // High-degree primitivity rows, UNLABELLED — the oracle is the judge, so no transcription mistake here can make
    // the case red at landing. Each degree carries the first primitive polynomial the deep mirror's own search
    // reaches, a catalog or near-catalog modulus, and the reducible t^d + 1, so both arms are exercised at Default.
    private static readonly ulong[] BinaryHighDegreeModuli = [
        0x1002DUL, 0x1002BUL, 0x10001UL,
        0x100001BUL, 0x1000001UL,
        0x1000000AFUL, 0x10000008DUL, 0x100000001UL,
    ];
    // The degrees the deep primitivity search reaches above the census ceiling, and the candidate budget each is
    // allowed before its exhaustion is reported as a failure rather than passing silently.
    private static readonly int[] BinaryPrimitiveSearchDegrees = [16, 24, 32];

    private const int BinaryPrimitiveSearchBudget = 4096;

    // The odd orders the factorization runs at. 17 is load-bearing: its factors are one of degree one and two of
    // degree eight, and eight is exactly ⌊17/2⌋, so a search bound narrowed by one turns that row into the
    // did-not-finish path. 15 is the row with three factors of equal degree, which is what makes the declared
    // tie-break observable at all.
    private static readonly int[] BinaryOddCycleLadder = [1, 3, 5, 7, 9, 15, 17, 21];
    private static readonly int[] BinaryDeepOddCycleLadder = [1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31];
    private static readonly int[] BinaryOddCycleRefusals = [0, -1, 32, 33, int.MinValue, int.MaxValue];

    /// <summary>Proves the additive group of <see cref="BinaryPolynomial"/>, the identity constants, the accessors and
    /// the diagnostic written form: the constructor round trip over the whole carrier, the coefficient-wise sum against
    /// an arithmetic oracle, characteristic two stated three ways, and the degree against a downward scan.</summary>
    /// <param name="left">The first operand vector, two raws.</param>
    /// <param name="right">The second operand vector, two raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryPolynomialAdditiveAndAccessors(long[] left, long[] right) {
        var zero = BinaryPolynomial.Zero;
        var one = BinaryPolynomial.One;
        var additiveIdentity = BinaryPolynomial.AdditiveIdentity;
        var multiplicativeIdentity = BinaryPolynomial.MultiplicativeIdentity;
        var indeterminate = BinaryPolynomial.Indeterminate;

        if (zero != additiveIdentity) { return "Zero and AdditiveIdentity differ"; }
        if (zero != default) { return "Zero is not the default value"; }
        if (
            (0UL != zero.Bits) ||
            !zero.IsZero ||
            zero.IsOne ||
            (-1 != zero.Degree)
        ) { return $"the zero polynomial reads back as bits {zero.Bits}, degree {zero.Degree}"; }
        if (one != multiplicativeIdentity) { return "One and MultiplicativeIdentity differ"; }
        if (
            (1UL != one.Bits) ||
            !one.IsOne ||
            one.IsZero ||
            (0 != one.Degree)
        ) { return $"the constant one reads back as bits {one.Bits}, degree {one.Degree}"; }
        if (
            (2UL != indeterminate.Bits) ||
            (1 != indeterminate.Degree) ||
            indeterminate.IsOne ||
            indeterminate.IsZero
        ) { return $"the indeterminate reads back as bits {indeterminate.Bits}, degree {indeterminate.Degree}"; }

        foreach (var raw in new[] { left[0], left[1], right[0], right[1], }) {
            var bits = UnsignedRaw(raw: raw);
            var value = new BinaryPolynomial(bits: bits);
            var scanned = Oracles.BinaryPolynomialDegree(value: bits);
            var written = Oracles.BinaryPolynomialText(
                value: bits,
                width: 64
            );

            if (value.Bits != bits) { return $"the constructor and Bits do not round trip at {bits}"; }
            if (value.Degree != scanned) { return $"the degree of {bits} is {value.Degree}, the oracle scan gives {scanned}"; }
            if (value.IsZero != (-1 == value.Degree)) { return $"IsZero disagrees with a degree of minus one at {bits}"; }
            if (value.IsOne != (1UL == bits)) { return $"IsOne disagrees with the bit pattern at {bits}"; }
            if (-value != value) { return $"unary negation moved {bits}"; }
            if (!(value + value).IsZero) { return $"{bits} added to itself is not zero"; }
            if (!(value - value).IsZero) { return $"{bits} subtracted from itself is not zero"; }
            if (
                ((value + zero) != value) ||
                ((zero + value) != value) ||
                ((value - zero) != value)
            ) { return $"zero is not the additive identity at {bits}"; }
            if (value.ToString() != written) { return $"the written form of {bits} is {value}, the oracle gives {written}"; }
        }

        var a = Poly(raw: left[0]);
        var b = Poly(raw: left[1]);
        var c = Poly(raw: right[0]);
        var sum = ((ulong)Oracles.BinaryPolynomialSum(
            left: a.Bits,
            right: b.Bits,
            width: 64
        ));

        if ((a + b).Bits != sum) { return $"the sum of {a.Bits} and {b.Bits} is {(a + b).Bits}, the coefficient-wise oracle gives {sum}"; }
        if ((a - b) != (a + b)) { return $"subtraction is not addition at {a.Bits} and {b.Bits}"; }
        if ((a + b) != (b + a)) { return $"addition is not commutative at {a.Bits} and {b.Bits}"; }
        if (((a + b) + c) != (a + (b + c))) { return $"addition is not associative at {a.Bits}, {b.Bits} and {c.Bits}"; }

        return BinaryPolynomialTextLadderFailure();
    }

    // The written-form ladder, and the dense word stated by its shape rather than by a transcription of the rule that
    // produced it: a reversed exponent loop or a misplaced separator moves at least one of these.
    private static string? BinaryPolynomialTextLadderFailure() {
        foreach (var (bits, text) in BinaryPolynomialTexts) {
            var written = new BinaryPolynomial(bits: bits).ToString();

            if (written != text) { return $"the written form of {bits} is {written}, expected {text}"; }
            if (written != Oracles.BinaryPolynomialText(
                value: bits,
                width: 64
            )) { return $"the written form of {bits} disagrees with the oracle"; }
        }

        var dense = new BinaryPolynomial(bits: ulong.MaxValue).ToString();
        var terms = dense.Split(separator: '+');

        if (dense != Oracles.BinaryPolynomialText(
            value: BinaryCarrierMask,
            width: 64
        )) { return "the dense written form disagrees with the oracle"; }
        if (64 != terms.Length) { return $"the dense written form carries {terms.Length} terms, expected 64"; }
        if (!dense.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "t^63+t^62+"
        )) { return "the dense written form does not open at the top exponent"; }
        if (!dense.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "+t^2+t+1"
        )) { return "the dense written form does not close at the constant term"; }

        return null;
    }

    /// <summary>The subject truncating carryless product, raw in and raw out.</summary>
    /// <param name="a">The multiplicand's raw.</param>
    /// <param name="b">The multiplier's raw.</param>
    /// <returns>The product's packed coefficients, as a raw.</returns>
    public static long BinaryPolynomialMultiply(long a, long b) =>
        unchecked((long)(Poly(raw: a) * Poly(raw: b)).Bits);
    /// <summary>The shared-nothing carryless product, truncated to the packed carrier.</summary>
    /// <param name="a">The multiplicand's raw.</param>
    /// <param name="b">The multiplier's raw.</param>
    /// <returns>The exact product's low sixty-four coefficients, as a raw.</returns>
    public static long BinaryPolynomialMultiplyOracle(long a, long b) =>
        unchecked((long)((ulong)(Oracles.CarrylessProduct(
            left: UnsignedRaw(raw: a),
            right: UnsignedRaw(raw: b)
        ) & BinaryCarrierMask)));
    /// <summary>Proves the checked operator's overflow predicate from OUTSIDE the packed carrier, the quotient-ring
    /// laws the truncating product obeys because discarding above degree 63 is reduction modulo <c>t^64</c>, and the
    /// published carryless reference vectors.</summary>
    /// <param name="left">The first operand vector, two raws.</param>
    /// <param name="right">The second operand vector, two raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryPolynomialCheckedMultiplyAndRingLaws(long[] left, long[] right) {
        var zero = BinaryPolynomial.Zero;
        var one = BinaryPolynomial.One;
        var indeterminate = BinaryPolynomial.Indeterminate;
        var a = Poly(raw: left[0]);
        var b = Poly(raw: left[1]);
        var c = Poly(raw: right[0]);

        foreach (var (first, second) in new[] { (a, b), (b, c), (a, c), }) {
            var exact = Oracles.CarrylessProduct(
                left: first.Bits,
                right: second.Bits
            );
            var truncating = (first * second);

            if (truncating.Bits != ((ulong)(exact & BinaryCarrierMask))) { return $"the product of {first.Bits} and {second.Bits} is {truncating.Bits}, the carryless oracle's low word is {((ulong)(exact & BinaryCarrierMask))}"; }

            if (64 <= Oracles.BinaryPolynomialDegree(value: exact)) {
                if (!Throws<OverflowException>(action: () => _ = checked((first * second)))) { return $"the checked product of {first.Bits} and {second.Bits} did not report a coefficient above degree 63"; }
            } else if (checked((first * second)) != truncating) {
                return $"the checked product of {first.Bits} and {second.Bits} disagrees with the truncating one";
            }
        }

        if ((a * b) != (b * a)) { return $"the truncating product is not commutative at {a.Bits} and {b.Bits}"; }
        if (((a * b) * c) != (a * (b * c))) { return $"the truncating product is not associative at {a.Bits}, {b.Bits} and {c.Bits}"; }
        if ((a * (b + c)) != ((a * b) + (a * c))) { return $"the truncating product does not distribute over addition at {a.Bits}, {b.Bits} and {c.Bits}"; }

        foreach (var value in new[] { a, b, c, }) {
            if (
                ((value * one) != value) ||
                ((one * value) != value)
            ) { return $"one is not a two-sided multiplicative identity at {value.Bits}"; }
            if (
                !(value * zero).IsZero ||
                !(zero * value).IsZero
            ) { return $"zero does not annihilate {value.Bits}"; }
            if ((value * indeterminate) != (value << 1)) { return $"multiplication by the indeterminate is not the single left shift at {value.Bits}"; }
        }

        return BinaryCarrylessVectorFailure();
    }

    // The published vectors, compared exactly: the truncating operator must answer the published low word, and the
    // checked one must refuse, because every published high word is non-zero.
    private static string? BinaryCarrylessVectorFailure() {
        foreach (var (leftBits, rightBits, low, high) in BinaryCarrylessVectors) {
            var first = new BinaryPolynomial(bits: leftBits);
            var second = new BinaryPolynomial(bits: rightBits);
            var product = (first * second);

            if (product.Bits != low) { return $"the published vector {leftBits:X16} times {rightBits:X16} has low word {product.Bits:X16}, expected {low:X16}"; }
            if (0UL == high) { return $"the published vector {leftBits:X16} times {rightBits:X16} declares a zero high word, which cannot exercise the checked operator"; }
            if (!Throws<OverflowException>(action: () => _ = checked((first * second)))) { return $"the checked product of the published vector {leftBits:X16} times {rightBits:X16} did not report its high word {high:X16}"; }
        }

        return null;
    }

    /// <summary>Proves Euclidean division against the bottom-up monomial oracle: both components, the remainder's
    /// degree bound, the division identity, the two operator projections, the zero-divisor refusals and the four
    /// poles.</summary>
    /// <param name="left">The dividend vector, two raws.</param>
    /// <param name="right">The divisor vector, two raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryPolynomialDivRemVsOracle(long[] left, long[] right) {
        for (var lane = 0; (lane < left.Length); ++lane) {
            var dividend = Poly(raw: left[lane]);
            var drawn = Poly(raw: right[lane]);
            // A zero divisor divides nothing. One is the substitute, applied identically here and in the oracle; the
            // zero divisor's own contract is the refusal ladder below, which uses Zero explicitly.
            var divisor = (drawn.IsZero
                ? BinaryPolynomial.One
                : drawn
            );

            var (quotient, remainder) = dividend.DivRem(divisor: divisor);
            var expected = Oracles.BinaryPolynomialDivRem(
                dividend: dividend.Bits,
                divisor: divisor.Bits
            );

            if (quotient.Bits != ((ulong)expected.Quotient)) { return $"the quotient of {dividend.Bits} by {divisor.Bits} is {quotient.Bits}, the monomial oracle gives {expected.Quotient}"; }
            if (remainder.Bits != ((ulong)expected.Remainder)) { return $"the remainder of {dividend.Bits} by {divisor.Bits} is {remainder.Bits}, the monomial oracle gives {expected.Remainder}"; }
            if (remainder.Degree >= divisor.Degree) { return $"the remainder {remainder.Bits} has degree {remainder.Degree}, not below the divisor's {divisor.Degree}"; }
            if (((quotient * divisor) + remainder) != dividend) { return $"the division identity fails at {dividend.Bits} by {divisor.Bits}"; }
            if (
                ((dividend / divisor) != quotient) ||
                ((dividend % divisor) != remainder)
            ) { return $"the operators disagree with DivRem at {dividend.Bits} by {divisor.Bits}"; }
            if (!Throws<DivideByZeroException>(action: () => _ = dividend.DivRem(divisor: BinaryPolynomial.Zero))) { return $"DivRem accepted a zero divisor at {dividend.Bits}"; }
            if (!Throws<DivideByZeroException>(action: () => _ = (dividend / BinaryPolynomial.Zero))) { return $"the division operator accepted a zero divisor at {dividend.Bits}"; }
            if (!Throws<DivideByZeroException>(action: () => _ = (dividend % BinaryPolynomial.Zero))) { return $"the modulus operator accepted a zero divisor at {dividend.Bits}"; }
        }

        return BinaryPolynomialDivisionPoleFailure(dividend: Poly(raw: left[0]));
    }

    // The degenerate cases a sampled stream reaches only by accident: the zero dividend, a divisor of strictly higher
    // degree (the loop that never turns), a polynomial against itself, and division by one.
    private static string? BinaryPolynomialDivisionPoleFailure(BinaryPolynomial dividend) {
        var one = BinaryPolynomial.One;
        var zero = BinaryPolynomial.Zero;
        var divisor = (dividend.IsZero
            ? one
            : dividend
        );
        var fromZero = zero.DivRem(divisor: divisor);

        if (
            !fromZero.Quotient.IsZero ||
            !fromZero.Remainder.IsZero
        ) { return $"the zero dividend by {divisor.Bits} is not the zero pair"; }

        if (dividend.Degree < 63) {
            var larger = new BinaryPolynomial(bits: (1UL << (dividend.Degree + 1)));
            var underflow = dividend.DivRem(divisor: larger);

            if (
                !underflow.Quotient.IsZero ||
                (underflow.Remainder != dividend)
            ) { return $"a divisor of degree {larger.Degree} did not leave {dividend.Bits} untouched"; }
        }

        if (!dividend.IsZero) {
            var self = dividend.DivRem(divisor: dividend);

            if (
                !self.Quotient.IsOne ||
                !self.Remainder.IsZero
            ) { return $"{dividend.Bits} divided by itself is not one with a zero remainder"; }
        }

        var byOne = dividend.DivRem(divisor: one);

        if (
            (byOne.Quotient != dividend) ||
            !byOne.Remainder.IsZero
        ) { return $"{dividend.Bits} divided by one is not itself with a zero remainder"; }

        return null;
    }

    /// <summary>Proves the greatest common divisor against the binary (Stein) descent, on the drawn pair AND on a
    /// planted common-factor pair: agreement, divisibility, maximality against a witness that genuinely divides both,
    /// the poles and the automatic monicity.</summary>
    /// <param name="left">The first operand vector, two raws.</param>
    /// <param name="right">The second operand vector, two raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryPolynomialGcdVsOracle(long[] left, long[] right) {
        for (var lane = 0; (lane < left.Length); ++lane) {
            var a = Poly(raw: left[lane]);
            var b = Poly(raw: right[lane]);

            if (BinaryPolynomialGcdFailure(
                a: a,
                b: b
            ) is { } drawn) { return drawn; }

            // Two random sixty-four-bit polynomials are coprime with high probability, so a third drawn polynomial is
            // planted as a common factor of both — folded to one where either product would leave the carrier — and
            // it is that pair which actually exercises the descent.
            var seed = Poly(raw: right[((lane + 1) % right.Length)]);
            var factor = ((!seed.IsZero && ((a.Degree + seed.Degree) <= 63) && ((b.Degree + seed.Degree) <= 63))
                ? seed
                : BinaryPolynomial.One
            );
            var plantedLeft = (a * factor);
            var plantedRight = (b * factor);

            if (BinaryPolynomialGcdFailure(
                a: plantedLeft,
                b: plantedRight
            ) is { } planted) { return planted; }

            var common = plantedLeft.GreatestCommonDivisor(other: plantedRight);

            if (
                !plantedLeft.IsZero ||
                !plantedRight.IsZero
            ) {
                // MAXIMALITY: the planted factor divides both operands by construction, so it must divide the
                // reported greatest common divisor. A routine that returned some common divisor rather than the
                // greatest satisfies divisibility and fails here.
                if (!(common % factor).IsZero) { return $"the planted common factor {factor.Bits} does not divide the reported divisor {common.Bits} of ({plantedLeft.Bits}, {plantedRight.Bits})"; }
                if (common.Degree < factor.Degree) { return $"the planted common factor {factor.Bits} was not reached in ({plantedLeft.Bits}, {plantedRight.Bits})"; }
            }
        }

        return BinaryPolynomialGcdPoleFailure(value: Poly(raw: left[0]));
    }

    // One gcd pair: the descent oracle, symmetry, divisibility of both operands, and the monicity that costs nothing.
    private static string? BinaryPolynomialGcdFailure(BinaryPolynomial a, BinaryPolynomial b) {
        var divisor = a.GreatestCommonDivisor(other: b);
        var expected = Oracles.BinaryPolynomialGcd(
            left: a.Bits,
            right: b.Bits
        );

        if (divisor.Bits != ((ulong)expected)) { return $"the greatest common divisor of {a.Bits} and {b.Bits} is {divisor.Bits}, the binary descent gives {expected}"; }
        if (divisor != b.GreatestCommonDivisor(other: a)) { return $"the greatest common divisor of {a.Bits} and {b.Bits} is not symmetric"; }

        if (divisor.IsZero) {
            // The only zero answer is the one both operands force.
            if (
                !a.IsZero ||
                !b.IsZero
            ) { return $"the greatest common divisor of {a.Bits} and {b.Bits} is zero"; }

            return null;
        }

        if (
            !(a % divisor).IsZero ||
            !(b % divisor).IsZero
        ) { return $"the divisor {divisor.Bits} does not divide both {a.Bits} and {b.Bits}"; }
        // Over the two-element field every non-zero polynomial has leading coefficient one, so the documented "monic"
        // gcd costs no normalization step and there is no normalization to get wrong. Stated rather than skipped, so a
        // reader is not left looking for a missing check.
        if (1UL != ((divisor.Bits >>> divisor.Degree) & 1UL)) { return $"the greatest common divisor {divisor.Bits} is not monic"; }

        return null;
    }
    // The poles the member's own doc states.
    private static string? BinaryPolynomialGcdPoleFailure(BinaryPolynomial value) {
        var one = BinaryPolynomial.One;
        var zero = BinaryPolynomial.Zero;

        if (value.GreatestCommonDivisor(other: zero) != value) { return $"the greatest common divisor of {value.Bits} and zero is not itself"; }
        if (zero.GreatestCommonDivisor(other: value) != value) { return $"the greatest common divisor of zero and {value.Bits} is not itself"; }
        if (!zero.GreatestCommonDivisor(other: zero).IsZero) { return "the greatest common divisor of two zeros is not zero"; }
        if (value.GreatestCommonDivisor(other: value) != value) { return $"the greatest common divisor of {value.Bits} with itself is not itself"; }
        if (!value.GreatestCommonDivisor(other: one).IsOne) { return $"the greatest common divisor of {value.Bits} and one is not one"; }

        return null;
    }

    /// <summary>Proves that the three shifts ARE monomial arithmetic: the left shift against a carryless product by
    /// <c>t^count</c>, the right shift against the monomial oracle's quotient, the carrier-masking seam from both
    /// sides, the round trip where nothing is lost, and the refusal ladder.</summary>
    /// <param name="left">The first operand vector, two raws.</param>
    /// <param name="right">The second operand vector, two raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryPolynomialShiftsAreMonomialArithmetic(long[] left, long[] right) {
        foreach (var raw in new[] { left[0], left[1], right[0], right[1], }) {
            var value = Poly(raw: raw);

            foreach (var count in BinaryShiftCounts) {
                var up = (value << count);
                var down = (value >>> count);

                if (down != (value >> count)) { return $"the two right shifts of {value.Bits} disagree at count {count}"; }

                if (63 < count) {
                    // NOT the carrier's masked shift, which would wrap the count back into range and resurrect exactly
                    // the coefficients the operator promises to discard.
                    if (
                        !up.IsZero ||
                        !down.IsZero
                    ) { return $"a shift of {value.Bits} by {count} did not empty the carrier"; }

                    continue;
                }

                var monomial = new BinaryPolynomial(bits: (1UL << count));
                var raised = ((ulong)(Oracles.CarrylessProduct(
                    left: value.Bits,
                    right: (BigInteger.One << count)
                ) & BinaryCarrierMask));
                var lowered = ((ulong)Oracles.BinaryPolynomialDivRem(
                    dividend: value.Bits,
                    divisor: (BigInteger.One << count)
                ).Quotient);

                if (up.Bits != raised) { return $"{value.Bits} shifted up by {count} is {up.Bits}, the carryless oracle gives {raised}"; }
                if (down.Bits != lowered) { return $"{value.Bits} shifted down by {count} is {down.Bits}, the monomial oracle gives {lowered}"; }
                if (up != (value * monomial)) { return $"the left shift of {value.Bits} by {count} is not multiplication by that monomial"; }
                if (down != (value / monomial)) { return $"the right shift of {value.Bits} by {count} is not division by that monomial"; }
                if (
                    (value.Degree <= (63 - count)) &&
                    ((up >>> count) != value)
                ) { return $"{value.Bits} did not survive a round trip through count {count}"; }
            }
        }

        return BinaryPolynomialShiftRefusalFailure();
    }

    // All three shift operators refuse a negative count and name the parameter they refused; the ordinary right shift
    // reaches its guard through the unsigned one, so the name it reports is the delegated-to operator's.
    private static string? BinaryPolynomialShiftRefusalFailure() {
        var value = BinaryPolynomial.Indeterminate;

        foreach (var count in BinaryShiftRefusals) {
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = (value << count),
                paramName: "count"
            )) { return $"the left shift accepted the count {count}"; }
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = (value >> count),
                paramName: "count"
            )) { return $"the right shift accepted the count {count}"; }
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = (value >>> count),
                paramName: "count"
            )) { return $"the unsigned right shift accepted the count {count}"; }
        }

        return null;
    }

    /// <summary>Proves the irreducibility decision: the published census at every degree through
    /// <paramref name="censusDegree"/>, per-polynomial agreement with exhaustive trial division through
    /// <paramref name="trialDegree"/>, the three short circuits by value, oracle-constructed reducible products above
    /// the trial ceiling, and the catalog moduli against the shipped fields.</summary>
    /// <param name="censusDegree">The highest degree the published count is checked at.</param>
    /// <param name="trialDegree">The highest degree every monic polynomial is trial-divided at.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryPolynomialIrreducibility(int censusDegree, int trialDegree) {
        for (var degree = 1; (degree <= censusDegree); ++degree) {
            var counted = 0;

            for (var tail = 0UL; (tail < (1UL << degree)); ++tail) {
                if (new BinaryPolynomial(bits: (1UL << degree) | tail).IsIrreducible()) { ++counted; }
            }

            if (counted != BinaryIrreducibleCounts[(degree - 1)]) { return $"there are {counted} monic irreducible polynomials of degree {degree}, the published count is {BinaryIrreducibleCounts[(degree - 1)]}"; }
        }

        for (var degree = 1; (degree <= trialDegree); ++degree) {
            for (var tail = 0UL; (tail < (1UL << degree)); ++tail) {
                var bits = (1UL << degree) | tail;
                var subject = new BinaryPolynomial(bits: bits).IsIrreducible();
                var divided = Oracles.BinaryPolynomialIsIrreducible(value: bits);

                if (subject != divided) { return $"the irreducibility of {bits} is {subject}, exhaustive trial division says {divided}"; }
            }
        }

        if (BinaryPolynomial.Zero.IsIrreducible()) { return "the zero polynomial is reported irreducible"; }
        if (BinaryPolynomial.One.IsIrreducible()) { return "the constant one is reported irreducible"; }
        // The indeterminate is the row that separates the degree-one rule from the zero-constant-term rule: it is
        // irreducible AND has a zero constant term, so a rule ordering that swallowed it would be caught here.
        if (!BinaryPolynomial.Indeterminate.IsIrreducible()) { return "the indeterminate is not reported irreducible"; }
        if (!new BinaryPolynomial(bits: 3UL).IsIrreducible()) { return "t + 1 is not reported irreducible"; }

        for (var degree = 2; (degree <= 8); ++degree) {
            for (var tail = 0UL; (tail < (1UL << degree)); tail += 2UL) {
                var bits = (1UL << degree) | tail;

                if (new BinaryPolynomial(bits: bits).IsIrreducible()) { return $"the zero-constant-term polynomial {bits} is reported irreducible"; }
            }
        }

        foreach (var (first, second) in BinaryReducibleFactorPairs) {
            var product = ((ulong)Oracles.CarrylessProduct(
                left: first,
                right: second
            ));
            var composite = new BinaryPolynomial(bits: product);

            if (composite.IsIrreducible()) { return $"the product of {first} and {second}, {product} of degree {composite.Degree}, is reported irreducible"; }
        }

        return BinaryCatalogModulusFailure();
    }

    // The catalog moduli: each is irreducible, and the transcribed constants agree with the shipped fields' own degree
    // and reduction tail reassembled into whole moduli.
    private static string? BinaryCatalogModulusFailure() {
        foreach (var (degree, bits) in BinaryCatalogModuli) {
            var modulus = new BinaryPolynomial(bits: bits);

            if (modulus.Degree != degree) { return $"the catalog modulus {bits} has degree {modulus.Degree}, expected {degree}"; }
            if (!modulus.IsIrreducible()) { return $"the catalog modulus {bits} of degree {degree} is not reported irreducible"; }
        }

        var byteField = (1UL << BinaryFields.Degree8.Degree) | BinaryFields.Degree8.ReductionTail;
        var wordField = (1UL << BinaryFields.Degree16.Degree) | BinaryFields.Degree16.ReductionTail;
        var doubleField = (1UL << BinaryFields.Degree32.Degree) | BinaryFields.Degree32.ReductionTail;

        if (byteField != BinaryCatalogModuli[0].Bits) { return $"the transcribed degree-8 modulus {BinaryCatalogModuli[0].Bits} disagrees with the shipped field's {byteField}"; }
        if (wordField != BinaryCatalogModuli[1].Bits) { return $"the transcribed degree-16 modulus {BinaryCatalogModuli[1].Bits} disagrees with the shipped field's {wordField}"; }
        if (doubleField != BinaryCatalogModuli[2].Bits) { return $"the transcribed degree-32 modulus {BinaryCatalogModuli[2].Bits} disagrees with the shipped field's {doubleField}"; }

        return null;
    }

    /// <summary>Proves the primitivity decision against the EXACT multiplicative order of <c>t</c>: per-polynomial
    /// agreement over every monic polynomial with a non-zero constant term through <paramref name="censusDegree"/>,
    /// the closed-form census, the implication into irreducibility with its strictness witness, the degree cap from
    /// both sides, an unlabelled high-degree ladder, and the indeterminate's false verdict.</summary>
    /// <param name="censusDegree">The highest degree the exhaustive census runs at.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryPolynomialPrimitivity(int censusDegree) {
        var cap = BinaryPolynomial.MaximumPrimitiveDegree;

        if (32 != cap) { return $"the primitivity degree cap is {cap}, expected 32"; }

        for (var degree = 1; (degree <= censusDegree); ++degree) {
            var divisors = Oracles.AscendingMersenneDivisors(degree: degree);
            var groupOrder = ((1UL << degree) - 1UL);
            var primitives = 0;

            for (var tail = 1UL; (tail < (1UL << degree)); tail += 2UL) {
                var bits = (1UL << degree) | tail;
                var polynomial = new BinaryPolynomial(bits: bits);
                var subject = polynomial.IsPrimitive();
                var order = Oracles.BinaryPolynomialIsPrimitive(
                    ascendingDivisors: divisors,
                    modulus: bits
                );

                if (subject != order.Primitive) { return $"the primitivity of {bits} is {subject}, the exact order of t says {order.Primitive} at order {order.Order}"; }
                if (
                    !subject &&
                    (groupOrder == order.Order)
                ) { return $"{bits} is reported non-primitive though t reaches the full order {groupOrder}"; }

                if (subject) {
                    ++primitives;

                    if (!polynomial.IsIrreducible()) { return $"the primitive polynomial {bits} is not reported irreducible"; }
                }
            }

            var expected = (BinaryTotient(value: groupOrder) / ((ulong)degree));

            if (((ulong)primitives) != expected) { return $"there are {primitives} primitive polynomials of degree {degree}, the closed form gives {expected}"; }
        }

        return BinaryPolynomialPrimitiveWitnessFailure();
    }

    // The two hand-derivable witnesses, the degree cap from both sides, the unlabelled high-degree ladder, and the
    // indeterminate's false verdict.
    private static string? BinaryPolynomialPrimitiveWitnessFailure() {
        // The shipped digital-net plane generator, and the smallest primitive polynomial there is.
        if (!new BinaryPolynomial(bits: 3UL).IsPrimitive()) { return "t + 1 is not reported primitive"; }

        // t^5 + 1 = (t + 1)(t^4 + t^3 + t^2 + t + 1), so the root's order is 5 rather than 2^4 − 1 = 15.
        var negative = new BinaryPolynomial(bits: 0b11111UL);
        var negativeOrder = Oracles.BinaryPolynomialIsPrimitive(
            modulus: 0b11111UL,
            ascendingDivisors: Oracles.AscendingMersenneDivisors(degree: 4)
        );

        if (!negative.IsIrreducible()) { return "t^4+t^3+t^2+t+1 is not reported irreducible"; }
        if (negative.IsPrimitive()) { return "t^4+t^3+t^2+t+1 is reported primitive, though the order of its root is 5"; }
        if (5UL != negativeOrder.Order) { return $"the order of t modulo t^4+t^3+t^2+t+1 is {negativeOrder.Order}, expected 5"; }

        // The straddle above the census ceiling, ASSERTED rather than assumed: each unlabelled degree has to carry a
        // primitive row AND a non-primitive one, or the ladder has quietly stopped exercising one of IsPrimitive's two
        // arms up there. This is the only Default-tier evidence that the ACCEPTING arm works above degree 10 — the
        // exhaustive census stops at 10 and the searching mirror is Deep-only — so a row-by-row comparison alone would
        // let an all-rejecting ladder pass while proving nothing about it.
        var acceptingRows = new int[BinaryPrimitiveSearchDegrees.Length];
        var rejectingRows = new int[BinaryPrimitiveSearchDegrees.Length];

        foreach (var bits in BinaryHighDegreeModuli) {
            var polynomial = new BinaryPolynomial(bits: bits);
            var divisors = Oracles.AscendingMersenneDivisors(degree: polynomial.Degree);
            var order = Oracles.BinaryPolynomialIsPrimitive(
                ascendingDivisors: divisors,
                modulus: bits
            );
            var subject = polynomial.IsPrimitive();

            if (subject != order.Primitive) { return $"the primitivity of {bits} at degree {polynomial.Degree} is {subject}, the exact order of t says {order.Primitive} at order {order.Order}"; }
            if (
                subject &&
                !polynomial.IsIrreducible()
            ) { return $"the primitive polynomial {bits} is not reported irreducible"; }

            var rung = Array.IndexOf(
                array: BinaryPrimitiveSearchDegrees,
                value: polynomial.Degree
            );

            if (0 > rung) { return $"the high-degree ladder carries degree {polynomial.Degree}, which is not one of the declared degrees above the census ceiling"; }

            if (subject) { ++acceptingRows[rung]; } else { ++rejectingRows[rung]; }
        }

        for (var rung = 0; (rung < BinaryPrimitiveSearchDegrees.Length); ++rung) {
            if (0 == acceptingRows[rung]) { return $"the degree-{BinaryPrimitiveSearchDegrees[rung]} ladder no longer carries a primitive polynomial, so IsPrimitive's accepting arm above the census ceiling is unexercised"; }
            if (0 == rejectingRows[rung]) { return $"the degree-{BinaryPrimitiveSearchDegrees[rung]} ladder no longer carries a non-primitive polynomial, so IsPrimitive's rejecting arm above the census ceiling is unexercised"; }
        }

        var aboveCap = new BinaryPolynomial(bits: (1UL << 33) | 1UL);

        if (!Throws<NotSupportedException>(action: () => _ = aboveCap.IsPrimitive())) { return "a degree-33 polynomial was decided rather than refused"; }

        // The indeterminate is the one polynomial in the whole type the constant-term gate has to answer by itself: it
        // is irreducible, so nothing above the gate refuses it, while every other zero-constant-term polynomial has
        // degree at least two and IsIrreducible catches it first. The irreducibility row is asserted here rather than
        // borrowed from the sibling case, because a change that made t reducible would leave the gate unreached and
        // this row decorative. The verdict is false — a primitive polynomial has a non-zero constant term — and the
        // oracle reports the reason beside it: t reduces to 0 modulo t, so no exponent at all sends it to one.
        var indeterminate = Oracles.BinaryPolynomialIsPrimitive(
            modulus: 2UL,
            ascendingDivisors: Oracles.AscendingMersenneDivisors(degree: 1)
        );

        if (!BinaryPolynomial.Indeterminate.IsIrreducible()) { return "t is no longer reported irreducible, so it no longer reaches the constant-term gate this row exists for"; }
        if (BinaryPolynomial.Indeterminate.IsPrimitive()) { return "t is reported primitive, though it maps to 0 in GF(2)[t]/(t) and generates nothing"; }
        if (
            indeterminate.Primitive ||
            (0UL != indeterminate.Order)
        ) { return $"the exact order of t modulo t is {indeterminate.Order}, expected none"; }

        return null;
    }

    /// <summary>Searches ascending for the first primitive polynomial at each degree above the census ceiling,
    /// requiring the subject to agree with the exact order at every candidate along the way. An exhausted budget is a
    /// NAMED failure, so a search that never reaches the accepting arm cannot pass silently.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryPolynomialPrimitiveSearch() {
        foreach (var degree in BinaryPrimitiveSearchDegrees) {
            var divisors = Oracles.AscendingMersenneDivisors(degree: degree);
            var found = false;
            var steps = 0;

            for (var tail = 1UL; ((tail < (1UL << degree)) && (steps < BinaryPrimitiveSearchBudget)); tail += 2UL) {
                var bits = (1UL << degree) | tail;
                var subject = new BinaryPolynomial(bits: bits).IsPrimitive();
                var order = Oracles.BinaryPolynomialIsPrimitive(
                    ascendingDivisors: divisors,
                    modulus: bits
                );

                ++steps;

                if (subject != order.Primitive) { return $"the primitivity of {bits} at degree {degree} is {subject}, the exact order of t says {order.Primitive} at order {order.Order}"; }

                if (subject) {
                    found = true;

                    break;
                }
            }

            if (!found) { return $"no primitive polynomial of degree {degree} was reached inside the {BinaryPrimitiveSearchBudget}-candidate budget, so the accepting arm above the census ceiling was never exercised"; }
        }

        return null;
    }
    /// <summary>Proves the factorization of <c>t^n + 1</c> at the Default ladder of odd orders.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryPolynomialFactorOddCycle() =>
        BinaryPolynomialFactorOddCycleFailure(orders: BinaryOddCycleLadder);
    /// <summary>Proves the factorization of <c>t^n + 1</c> at EVERY odd order the member admits.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryPolynomialFactorOddCycleExhaustive() =>
        BinaryPolynomialFactorOddCycleFailure(orders: BinaryDeepOddCycleLadder);

    // The factor-degree multiset against the 2-cyclotomic cosets, the values against their fold under the oracle's own
    // carryless product, each factor's irreducibility against exhaustive trial division, distinctness, the declared
    // order, and the refusal ladder.
    private static string? BinaryPolynomialFactorOddCycleFailure(int[] orders) {
        foreach (var order in orders) {
            var factors = BinaryPolynomial.FactorOddCycle(cycleOrder: order);
            var degrees = Oracles.BinaryCyclotomicFactorDegrees(cycleOrder: order);
            var product = BigInteger.One;

            if (factors.Length != degrees.Length) { return $"the factorization of order {order} has {factors.Length} factors, the cyclotomic cosets give {degrees.Length}"; }

            for (var index = 0; (index < factors.Length); ++index) {
                var factor = factors[index];

                if (factor.Degree != degrees[index]) { return $"factor {index} of order {order} has degree {factor.Degree}, the cyclotomic cosets give {degrees[index]}"; }
                if (!Oracles.BinaryPolynomialIsIrreducible(value: factor.Bits)) { return $"factor {index} of order {order}, {factor.Bits}, is not irreducible by trial division"; }
                if (0UL == (factor.Bits & 1UL)) { return $"factor {index} of order {order}, {factor.Bits}, has a zero constant term"; }
                if (1UL != ((factor.Bits >>> factor.Degree) & 1UL)) { return $"factor {index} of order {order}, {factor.Bits}, is not monic"; }

                for (var earlier = 0; (earlier < index); ++earlier) {
                    if (factors[earlier] == factor) { return $"the factorization of order {order} repeats {factor.Bits}"; }
                }

                if (0 < index) {
                    var previous = factors[(index - 1)];

                    if (
                        (factor.Degree < previous.Degree) ||
                        ((factor.Degree == previous.Degree) && (factor.Bits <= previous.Bits))
                    ) { return $"the factorization of order {order} is not ascending by degree then packed value at index {index}"; }
                }

                product = Oracles.CarrylessProduct(
                    left: product,
                    right: factor.Bits
                );
            }

            var expected = (BigInteger.One << order) | BigInteger.One;

            if (product != expected) { return $"the oracle-formed product of order {order}'s factors is {product}, expected {expected}"; }
        }

        var single = BinaryPolynomial.FactorOddCycle(cycleOrder: 1);

        if (
            (1 != single.Length) ||
            (3UL != single[0].Bits)
        ) { return "the order-one factorization is not the single factor t + 1"; }

        foreach (var refused in BinaryOddCycleRefusals) {
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = BinaryPolynomial.FactorOddCycle(cycleOrder: refused),
                paramName: "cycleOrder"
            )) { return $"the factorization accepted the order {refused}"; }
        }

        for (var even = 2; (even <= 30); even += 2) {
            var order = even;

            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = BinaryPolynomial.FactorOddCycle(cycleOrder: order),
                paramName: "cycleOrder"
            )) { return $"the factorization accepted the even order {order}"; }
        }

        return null;
    }
    // The Euler totient by trial division — the independent factorization the primitive census's closed form needs,
    // formed here rather than taken from the shipped prime-factor enumerator the subject itself uses.
    private static ulong BinaryTotient(ulong value) {
        var remaining = value;
        var total = value;

        for (var candidate = 2UL; ((candidate * candidate) <= remaining); ++candidate) {
            if (0UL != (remaining % candidate)) { continue; }

            while (0UL == (remaining % candidate)) { remaining /= candidate; }

            total -= (total / candidate);
        }

        if (1UL < remaining) { total -= (total / remaining); }

        return total;
    }

}
