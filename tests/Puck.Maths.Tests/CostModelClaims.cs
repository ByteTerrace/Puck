using Xunit;

namespace Puck.Maths.Tests;

/// <summary>Exact arithmetic and refusal contracts for abstract service costs; no claim of hardware calibration.</summary>
internal static class CostModelClaims {
    private static readonly long[] BoundEdges = [long.MinValue, -1, 0, 1, 2, long.MaxValue - 1, long.MaxValue];
    private static readonly int[] Rates = [0, 1, 2, 3, 7, 60, 240, 50_400];
    private static readonly int[] InvalidRates = [int.MinValue, -1, 11, 50_401, int.MaxValue];
    private static readonly string?[] InvalidNames = [null, "", " \t"];
    private static readonly long[] NegativeCycles = [long.MinValue, -1L];
    private static readonly CostBound[] StatusBounds = [
        CostBound.Known(3), CostBound.Unmodeled("left"), CostBound.Unmodeled("right"), CostBound.Overflow,
    ];
    private static readonly int[,] StatusWinners = {
        { 0, 1, 2, 3 }, { 1, 1, 1, 3 }, { 2, 2, 2, 3 }, { 3, 3, 3, 3 },
    };
    private static readonly (long Frequency, long Numerator, long Denominator, string Parameter)[] InvalidProfiles = [
        (0, 1, 1, "cyclesPerSecond"), (-1, 1, 1, "cyclesPerSecond"),
        (1, 0, 1, "subsystemShareNumerator"), (1, -1, 1, "subsystemShareNumerator"),
        (1, 1, 0, "subsystemShareDenominator"), (1, 1, -1, "subsystemShareDenominator"),
        (1, 2, 1, "subsystemShareNumerator"),
    ];

    private static void CheckBound(CostBound actual, int kind, long cycles = 0, string? reason = null) {
        Assert.Equal(expected: kind, actual: (int)actual.Kind);
        Assert.Equal(expected: cycles, actual: actual.Cycles);
        Assert.Equal(expected: reason, actual: actual.Reason);
        Assert.Equal(expected: kind == 0, actual: actual.IsKnown);
        Assert.Equal(expected: kind == 1, actual: actual.IsUnmodeled);
        Assert.Equal(expected: kind == 2, actual: actual.IsOverflow);
        Assert.Equal(expected: kind == 0 ? cycles : long.MaxValue, actual: actual.ToSaturatingCycles());
    }

    public static string? BoundConstruction() {
        var known = CostBoundKind.Known;
        var unmodeled = CostBoundKind.Unmodeled;
        var overflow = CostBoundKind.Overflow;
        Assert.Equal(expected: (0, 1, 2), actual: ((int)known, (int)unmodeled, (int)overflow));
        Assert.Equal(expected: typeof(byte), actual: Enum.GetUnderlyingType(typeof(CostBoundKind)));
        Assert.Equal(expected: new[] { known, unmodeled, overflow }, actual: Enum.GetValues<CostBoundKind>());
        CheckBound(actual: default, kind: 0);
        CheckBound(actual: CostBound.Zero, kind: 0);
        CheckBound(actual: CostBound.Overflow, kind: 2);
        CheckBound(actual: CostBound.Unmodeled("missing kernel"), kind: 1, reason: "missing kernel");
        foreach (var cycles in BoundEdges) {
            CheckBound(actual: new CostBound(cycles), kind: cycles < 0 ? 2 : 0, cycles: cycles < 0 ? 0 : cycles);
            CheckBound(actual: CostBound.Known(cycles), kind: cycles < 0 ? 2 : 0, cycles: cycles < 0 ? 0 : cycles);
        }
        foreach (var reason in InvalidNames) {
            if (reason is null) {
                Assert.Equal(expected: "reason", actual: Assert.Throws<ArgumentNullException>(() => CostBound.Unmodeled(reason!)).ParamName);
            } else {
                Assert.Equal(expected: "reason", actual: Assert.Throws<ArgumentException>(() => CostBound.Unmodeled(reason)).ParamName);
            }
        }
        return null;
    }

    public static string? StatusPropagation() {
        // Complete table for known, two distinct reasons, and overflow. Entries select the winning reason,
        // independently of the implementation's branch order. Known arithmetic has its own swept law.
        var bounds = StatusBounds;
        for (var left = 0; left < bounds.Length; ++left) {
            for (var right = 0; right < bounds.Length; ++right) {
                var winner = StatusWinners[left, right];
                var expected = winner == 0 ? CostBound.Known(6) : bounds[winner];
                Assert.Equal(expected: expected, actual: CostBound.Add(bounds[left], bounds[right]));
                Assert.Equal(expected: expected, actual: bounds[left] + bounds[right]);
                Assert.Equal(expected: bounds[winner], actual: CostBound.Max(bounds[left], bounds[right]));
            }
            foreach (var multiplier in BoundEdges) {
                var expected = multiplier == 0 ? CostBound.Zero : multiplier < 0 ? CostBound.Overflow : bounds[left];
                if (left == 0 && multiplier > 0) {
                    var exact = Oracles.CostProduct(3, multiplier);
                    expected = exact.Kind == 0 ? CostBound.Known(exact.Cycles) : CostBound.Overflow;
                }
                Assert.Equal(expected: expected, actual: CostBound.Multiply(bounds[left], multiplier));
                Assert.Equal(expected: expected, actual: bounds[left] * multiplier);
                Assert.Equal(expected: expected, actual: multiplier * bounds[left]);
            }
        }
        return null;
    }

    public static string? BoundArithmetic(long[] left, long[] right) {
        var a = left[0] & long.MaxValue;
        var b = right[0] & long.MaxValue;
        var first = CostBound.Known(a);
        var second = CostBound.Known(b);
        var sum = Oracles.CostSum(a, b);
        var product = Oracles.CostProduct(a, b);
        CheckBound(actual: CostBound.Add(first, second), kind: sum.Kind, cycles: sum.Cycles);
        CheckBound(actual: first + second, kind: sum.Kind, cycles: sum.Cycles);
        CheckBound(actual: CostBound.Multiply(first, b), kind: product.Kind, cycles: product.Cycles);
        CheckBound(actual: first * b, kind: product.Kind, cycles: product.Cycles);
        CheckBound(actual: b * first, kind: product.Kind, cycles: product.Cycles);
        CheckBound(actual: CostBound.Max(first, second), kind: 0, cycles: a >= b ? a : b);
        return null;
    }

    public static string? ProfileConstruction() {
        var profile = new CostModelProfile("custom", long.MaxValue, long.MaxValue - 1, long.MaxValue);
        Assert.Equal(expected: ("custom", long.MaxValue, long.MaxValue - 1, long.MaxValue, 50_400L),
            actual: (profile.Name, profile.CyclesPerSecond, profile.SubsystemShareNumerator, profile.SubsystemShareDenominator, profile.EngineTicksPerSecond));
        var portable = CostModelProfile.Portable;
        Assert.Equal(expected: ("puck.portable64.v1", 3_000_000_000L, 1L, 2L, 50_400L),
            actual: (portable.Name, portable.CyclesPerSecond, portable.SubsystemShareNumerator, portable.SubsystemShareDenominator, portable.EngineTicksPerSecond));
        _ = new CostModelProfile("minimum", 1, 1, 1);
        foreach (var name in InvalidNames) {
            if (name is null) {
                Assert.Equal(expected: "name", actual: Assert.Throws<ArgumentNullException>(() => new CostModelProfile(name!, 1, 1, 1)).ParamName);
            } else {
                Assert.Equal(expected: "name", actual: Assert.Throws<ArgumentException>(() => new CostModelProfile(name, 1, 1, 1)).ParamName);
            }
        }
        foreach (var row in InvalidProfiles) {
            Assert.Equal(expected: row.Parameter, actual: Assert.Throws<ArgumentOutOfRangeException>(
                () => new CostModelProfile("invalid", row.Frequency, row.Numerator, row.Denominator)).ParamName);
        }
        return null;
    }

    public static string? RateContract() {
        var profile = CostModelProfile.Portable;
        // Every positive divisor of the declared clock, including its endpoints, has an exact recurring period.
        for (var rate = 1; rate <= 50_400; ++rate) {
            if (50_400 % rate == 0) {
                Assert.Equal(expected: 50_400L, actual: profile.StepPeriodEngineTicks(rate) * rate);
            }
        }
        Assert.Equal(expected: 0L, actual: profile.StepPeriodEngineTicks(0));
        Assert.Equal(expected: 0L, actual: profile.AuthoredBudgetCycles(0));
        Assert.Equal(expected: (Int128.Zero, 1L), actual: profile.ReferenceStepFraction(long.MaxValue, 0));
        Assert.False(profile.Admits(CostBound.Zero, 0));
        foreach (var rate in InvalidRates) {
            CheckRateRefusal(() => profile.StepPeriodEngineTicks(rate), rate);
            CheckRateRefusal(() => profile.AuthoredBudgetCycles(rate), rate);
            CheckRateRefusal(() => profile.ReferenceStepFraction(1, rate), rate);
            CheckRateRefusal(() => profile.Admits(CostBound.Overflow, rate), rate);
        }
        foreach (var cycles in NegativeCycles) {
            Assert.Equal(expected: "cycles", actual: Assert.Throws<ArgumentOutOfRangeException>(() => profile.ReferenceEngineTicks(cycles)).ParamName);
            Assert.Equal(expected: "cycles", actual: Assert.Throws<ArgumentOutOfRangeException>(() => profile.ReferenceStepFraction(cycles, 0)).ParamName);
        }
        return null;
    }

    private static void CheckRateRefusal(Action action, int rate) {
        if (rate < 0) {
            Assert.Equal(expected: "rateHz", actual: Assert.Throws<ArgumentOutOfRangeException>(action).ParamName);
        } else {
            Assert.Equal(expected: "rateHz", actual: Assert.Throws<ArgumentException>(action).ParamName);
        }
    }

    private static long Positive(long raw) => Math.Max(1L, raw & long.MaxValue);

    public static string? Budgets(long[] left, long[] right) {
        var frequency = Positive(left[0]);
        var denominator = Positive(left[1]);
        var numerator = Math.Min(Positive(left[2]), denominator);
        var profile = new CostModelProfile("sweep", frequency, numerator, denominator);
        foreach (var rate in Rates) {
            var budget = Oracles.CostBudget(frequency, numerator, denominator, rate);
            Assert.Equal(expected: budget, actual: profile.AuthoredBudgetCycles(rate));
            foreach (var cycles in new[] { right[0] & long.MaxValue, budget, budget == 0 ? 0 : budget - 1, budget == long.MaxValue ? budget : budget + 1 }) {
                Assert.Equal(expected: Oracles.CostAdmitted(cycles, frequency, numerator, denominator, rate),
                    actual: profile.Admits(CostBound.Known(cycles), rate));
            }
            Assert.False(profile.Admits(CostBound.Unmodeled("unpriced"), rate));
            Assert.False(profile.Admits(CostBound.Overflow, rate));
        }
        return null;
    }

    public static string? Conversions(long[] left, long[] right) {
        var cycles = left[0] & long.MaxValue;
        var frequency = Positive(right[0]);
        var profile = new CostModelProfile("sweep", frequency, 1, 1);
        var expected = Oracles.CostReferenceTicks(cycles, frequency);
        if (expected > long.MaxValue) {
            Assert.Throws<OverflowException>(() => profile.ReferenceEngineTicks(cycles));
        } else {
            Assert.Equal(expected: (long)expected, actual: profile.ReferenceEngineTicks(cycles));
        }
        foreach (var rate in Rates) {
            Assert.Equal(expected: Oracles.CostStepFraction(cycles, frequency, rate), actual: profile.ReferenceStepFraction(cycles, rate));
        }
        return null;
    }
}
