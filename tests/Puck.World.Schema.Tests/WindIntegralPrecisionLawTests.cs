using System.Numerics;
using Puck.Hosting;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Degree-81 rate integrals are checked against exact rational power polynomials, independent of the
/// production Bernstein basis. Bounds include the accumulated rounding of a compiled whole-period total.</summary>
public sealed class WindIntegralPrecisionLawTests {
    private const ulong Period = (8UL * EngineTicks.PerSecond);
    private const ulong Start = ((9UL * EngineTicks.PerSecond) / 4UL);

    private static Rational Q(long numerator, long denominator = 1) => new(Denominator: denominator, Numerator: numerator);
    private static BindableScalar Curve(string clock, double halfway, float maximum) => new(keys: new WorldKeys<BindableScalar>(Clock: clock,
        Keys: [new(At: 0d, Ease: WorldKeyEase.Smooth, Value: 0f), new(At: halfway, Ease: WorldKeyEase.Smooth, Value: maximum)]));
    private static (WorldDefinition World, BindableScalar Rate) Fixture(float scale, int depth = 3) {
        var clocks = new List<WorldClock> { new("day", PeriodSeconds: 8d, StartSeconds: 2.25d) };

        for (var index = 0; (index < depth); index++) {
            clocks.Add(item: new WorldClock($"phase{(index + 1)}", Phase: Curve(clock: clocks[^1].Name, halfway: ((index == 0) ? 4d : 0.5d), maximum: 0.25f)));
        }
        return (new WorldDefinition(TimelineRaw: new WorldTimelineSection(Clocks: clocks)), Curve(clock: clocks[^1].Name, halfway: 0.5d, maximum: scale));
    }
    // Exact symbolic power coefficients are deliberately a different representation and algorithm from production.
    private static Rational[] Multiply(Rational[] left, Rational[] right) {
        var result = new Rational[((left.Length + right.Length) - 1)];

        for (var a = 0; (a < left.Length); a++) {
            for (var b = 0; (b < right.Length); b++) { result[(a + b)] += (left[a] * right[b]); }
        }
        return result;
    }
    private static Rational[] Smooth(Rational[] value) {
        var square = Multiply(left: value, right: value);
        var cube = Multiply(left: square, right: value);

        for (var index = 0; (index < cube.Length); index++) { cube[index] = ((-Q(2) * cube[index]) + ((index < square.Length) ? (Q(3) * square[index]) : Rational.Zero)); }
        return cube;
    }
    private static Rational[] Scale(Rational[] value, Rational scale) => value.Select(selector: item => (item * scale)).ToArray();
    private static Rational[] Polynomial() {
        var phase = Scale(Smooth(value: [Rational.Zero, Rational.One]), Q(denominator: 4, numerator: 1));

        for (var level = 1; (level < 3); level++) { phase = Scale(Smooth(value: Scale(phase, Q(2))), Q(denominator: 4, numerator: 1)); }
        return Smooth(value: Scale(phase, Q(2)));
    }
    private static Rational Integral(Rational[] polynomial, Rational at) {
        var value = Rational.Zero;

        for (var index = (polynomial.Length - 1); (index >= 0); index--) { value = ((value * at) + (polynomial[index] / Q((index + 1)))); }
        return (value * at);
    }
    private static Rational Through(Rational[] polynomial, Rational at) => ((at <= Q(denominator: 2, numerator: 1))
        ? (Q(4) * Integral(polynomial, (at * Q(2))))
        : ((Q(8) * Integral(polynomial, Rational.One)) - (Q(4) * Integral(polynomial, (Q(2) - (at * Q(2)))))));
    private static double Oracle(Rational[] polynomial, ulong whole, Rational fraction, Rational scale) {
        var shifted = (new BigInteger(value: whole) + Start);
        var periods = BigInteger.DivRem(dividend: shifted, divisor: Period, remainder: out var remainder);
        var value = (scale * ((((new Rational(Denominator: 1, Numerator: periods) * Q(8)) * Integral(polynomial, Rational.One))
            + Through(polynomial, ((new Rational(Denominator: 1, Numerator: remainder) + fraction) / Q(((long)Period)))))
            - Through(polynomial, new Rational(Denominator: Period, Numerator: Start))));
        var period = Q(4096);

        value -= (new Rational(((value / period) + Q(denominator: 2, numerator: 1)).Floor(), 1) * period);
        return value.ToDouble();
    }

    [InlineData(5368709, 268435456, 4096d)]
    [InlineData(-5368709, 268435456, 4096d)]
    [InlineData(7130317, 4194304, Math.Tau)]
    [Theory]
    public void Literal_motion_reduces_before_large_products(long numerator, long denominator, double modulus) {
        var rate = Q(denominator: denominator, numerator: numerator);
        var literal = ((float)rate.ToDouble());

        Assert.True(condition: WorldRateIntegral.TryCompile(literal, new WorldDefinition(), out var integral, out var reason), userMessage: reason);
        var value = (rate * ((new Rational(new BigInteger(value: ulong.MaxValue), 1) + Q(denominator: 4, numerator: 1)) / Q(((long)EngineTicks.PerSecond))));
        var period = ((modulus == 4096d) ? Q(4096) : Q(denominator: 140737488355328, numerator: 884279719003555)); // Exact binary64 Math.Tau.

        value -= (new Rational(((value / period) + Q(denominator: 2, numerator: 1)).Floor(), 1) * period);
        var actual = integral!.At(new PresentedTick(Fraction: 0.25d, Whole: ulong.MaxValue), modulus, out var work);
        var error = Math.Abs(value: Math.IEEERemainder(x: (actual - value.ToDouble()), y: modulus));

        Assert.InRange(actual: error, high: 1e-12d, low: 0d);
        Assert.Equal(0, work.CoefficientBlends);
        Assert.Equal(0, integral.Cost.Coefficients);
        Assert.InRange(work.PeriodDoublings, 1, 64);
    }
    [Fact]
    public void Literal_key_endpoints_keep_their_exact_binary32_values_during_double_integration() {
        var world = new WorldDefinition(TimelineRaw: new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d)]));
        var rate = new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: "day", Keys: [new(0d, 0.1f), new(4d, 1.5f)]));

        Assert.True(condition: WorldRateIntegral.TryCompile(rate, world, out var integral, out var reason), userMessage: reason);
        // Integral at two seconds: 2*a + (b-a)/2. The authored 0.1f is exactly this rational.
        var first = Q(denominator: 134217728, numerator: 13421773);
        var expected = ((Q(2) * first) + ((Q(denominator: 2, numerator: 3) - first) / Q(2))).ToDouble();

        Assert.InRange(Math.Abs(value: (integral!.At(new PresentedTick(Fraction: 0d, Whole: (2UL * EngineTicks.PerSecond)), 4096d) - expected)), 0d, 1e-14d);
    }
    [InlineData(0UL, 1, 1e-11d)]
    [InlineData(100799UL, 1, 1e-11d)]
    [InlineData(289799UL, 1, 1e-11d)]
    [InlineData((1UL << 40), 1, 1e-10d)]
    [InlineData(ulong.MaxValue, 1, 2e-4d)]
    [InlineData(ulong.MaxValue, 1048576, 2e-10d)]
    [Theory]
    public void The_maximum_degree_matches_an_exact_rational_integral_at_large_and_wrapping_offsets(ulong whole, int divisor, double errorBound) {
        var (world, rate) = Fixture((1f / divisor));
        Assert.True(condition: WorldRateIntegral.TryCompile(rate, world, out var integral, out var reason, name: "wind.speed"), userMessage: reason);
        Assert.Equal(81, integral!.Cost.Degree);
        Assert.Equal(new WorldRateCost(Coefficients: 166, Degree: 81, Pieces: 2), integral.Cost);
        var actual = integral.At(new PresentedTick(Fraction: 0.25d, Whole: whole), 4096d, out var work);
        var expected = Oracle(Polynomial(), whole, Q(denominator: 4, numerator: 1), Q(denominator: divisor, numerator: 1));
        var error = Math.Abs(value: Math.IEEERemainder(x: (actual - expected), y: 4096d));

        TestContext.Current.TestOutputHelper!.WriteLine(message: $"tick={whole}, divisor={divisor}, expected={expected:R}, actual={actual:R}, error={error:R}, bound={errorBound:R}");
        Assert.True(condition: (error <= errorBound), userMessage: $"tick={whole}, divisor={divisor}, expected={expected:R}, actual={actual:R}, error={error:R}, bound={errorBound:R}");
        Assert.Equal(1, work.PieceSearches);
        Assert.Equal(3403, work.CoefficientBlends); // 82*83/2: exactly one degree-82 antiderivative.
        Assert.InRange(work.PeriodDoublings, 0, 64);
    }
    [Fact]
    public void Every_compiled_piece_boundary_is_continuous_and_evaluates_only_one_piece() {
        var (world, rate) = Fixture(1f);
        Assert.True(condition: WorldRateIntegral.TryCompile(rate, world, out var integral, out var reason), userMessage: reason);
        for (var piece = 0; (piece < integral!.Cost.Pieces); piece++) {
            var boundary = (((((ulong)Math.Round(a: (integral.PieceStart(index: piece) * Period))) + Period) - Start) % Period);
            var before = integral.At(new PresentedTick(Fraction: 0.999d, Whole: (boundary - 1UL)), 4096d);
            var at = integral.At(new PresentedTick(Fraction: 0d, Whole: boundary), 4096d);
            var after = integral.At(new PresentedTick(Fraction: 0.001d, Whole: boundary), 4096d);

            Assert.InRange(Math.Abs(value: (at - before)), 0d, ((0.001d / EngineTicks.PerSecond) + 1e-12d));
            Assert.InRange(Math.Abs(value: (after - at)), 0d, ((0.001d / EngineTicks.PerSecond) + 1e-12d));
        }
    }
    [Fact]
    public void A_fourth_smooth_phase_is_refused_by_rate_name_full_chain_and_depth() {
        var (world, rate) = Fixture(1f, depth: 4);
        Assert.False(condition: WorldRateIntegral.TryCompile(rate, world, out _, out var reason, name: "wind.speed"));
        Assert.Contains(actualString: reason, expectedSubstring: "wind.speed");
        Assert.Contains(actualString: reason, expectedSubstring: "day → phase1 → phase2 → phase3 → phase4");
        Assert.Contains(actualString: reason, expectedSubstring: "4 smooth phase clocks deep; limit 3");
        // The ordinary keyed value still has a legal clock graph; only analytical rates impose the degree budget.
        Assert.True(condition: WorldValueValidation.TryValidate(curve: rate.Keys!, definition: world, reason: out reason), userMessage: reason);
    }
    [InlineData("degree", 5, 2, "degree 243; limit 81")]
    [InlineData("pieces", 0, 1025, "piece count exceeds limit 1024")]
    [InlineData("coefficients", 3, 800, "coefficient count 65570; limit 65536")]
    [Theory]
    public void Independent_internal_bounds_refuse_by_rate_and_clock_chain(string bound, int depth, int keyCount, string detail) {
        var (world, rate) = Fixture(depth: depth, scale: 1f);
        if (bound == "degree") {
            rate = new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: rate.Keys!.Clock,
                Keys: [new(0d, 0f), new(0.5d, 1f)]));
        } else {
            var keys = new WorldKeys<BindableScalar>(Clock: "day", Keys: Enumerable.Range(count: keyCount, start: 0)
                .Select(selector: index => new WorldKey<BindableScalar>(At: ((8d * index) / keyCount), Ease: WorldKeyEase.Smooth, Value: (((index & 1) == 0) ? 0f : 0.25f))).ToArray());

            if (depth == 0) { rate = new BindableScalar(keys: keys); } else {
                var clocks = world.Timeline.Clocks!.ToArray();

                clocks[1] = clocks[1] with { Phase = new BindableScalar(keys: keys) };
                world = world with { TimelineRaw = new WorldTimelineSection(Clocks: clocks) };
            }
        }
        Assert.False(condition: WorldRateIntegral.TryCompile(rate, world, out _, out var reason, name: "wind.speed"));
        Assert.Contains(actualString: reason, expectedSubstring: "wind.speed (day");
        Assert.Contains(actualString: reason, expectedSubstring: detail);
    }
}
