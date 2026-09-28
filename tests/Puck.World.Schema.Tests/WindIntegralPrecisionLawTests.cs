using System.Numerics;
using Puck.Hosting;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Degree-81 rate integrals are checked against exact rational power polynomials, independent of the
/// production Bernstein basis. Bounds include the accumulated rounding of a compiled whole-period total.</summary>
public sealed class WindIntegralPrecisionLawTests {
    private const ulong Period = 8UL * EngineTicks.PerSecond;
    private const ulong Start = 9UL * EngineTicks.PerSecond / 4UL;
    private static Rational Q(long numerator, long denominator = 1) => new(numerator, denominator);

    private static BindableScalar Curve(string clock, double halfway, float maximum) => new(new WorldKeys<BindableScalar>(clock,
        [new(0d, 0f, WorldKeyEase.Smooth), new(halfway, maximum, WorldKeyEase.Smooth)]));

    private static (WorldDefinition World, BindableScalar Rate) Fixture(float scale, int depth = 3) {
        var clocks = new List<WorldClock> { new("day", PeriodSeconds: 8d, StartSeconds: 2.25d) };
        for (var index = 0; index < depth; index++) {
            clocks.Add(new WorldClock($"phase{index + 1}", Phase: Curve(clocks[^1].Name, index == 0 ? 4d : 0.5d, 0.25f)));
        }
        return (new WorldDefinition(TimelineRaw: new WorldTimelineSection(clocks)), Curve(clocks[^1].Name, 0.5d, scale));
    }

    // Exact symbolic power coefficients are deliberately a different representation and algorithm from production.
    private static Rational[] Multiply(Rational[] left, Rational[] right) {
        var result = new Rational[left.Length + right.Length - 1];
        for (var a = 0; a < left.Length; a++) {
            for (var b = 0; b < right.Length; b++) { result[a + b] += left[a] * right[b]; }
        }
        return result;
    }
    private static Rational[] Smooth(Rational[] value) {
        var square = Multiply(value, value);
        var cube = Multiply(square, value);
        for (var index = 0; index < cube.Length; index++) { cube[index] = -Q(2) * cube[index] + (index < square.Length ? Q(3) * square[index] : Rational.Zero); }
        return cube;
    }
    private static Rational[] Scale(Rational[] value, Rational scale) => value.Select(item => item * scale).ToArray();
    private static Rational[] Polynomial() {
        var phase = Scale(Smooth([Rational.Zero, Rational.One]), Q(1, 4));
        for (var level = 1; level < 3; level++) { phase = Scale(Smooth(Scale(phase, Q(2))), Q(1, 4)); }
        return Smooth(Scale(phase, Q(2)));
    }
    private static Rational Integral(Rational[] polynomial, Rational at) {
        var value = Rational.Zero;
        for (var index = polynomial.Length - 1; index >= 0; index--) { value = (value * at) + (polynomial[index] / Q(index + 1)); }
        return value * at;
    }
    private static Rational Through(Rational[] polynomial, Rational at) => at <= Q(1, 2)
        ? Q(4) * Integral(polynomial, at * Q(2))
        : Q(8) * Integral(polynomial, Rational.One) - Q(4) * Integral(polynomial, Q(2) - (at * Q(2)));
    private static double Oracle(Rational[] polynomial, ulong whole, Rational fraction, Rational scale) {
        var shifted = new BigInteger(whole) + Start;
        var periods = BigInteger.DivRem(shifted, Period, out var remainder);
        var value = scale * ((new Rational(periods, 1) * Q(8) * Integral(polynomial, Rational.One))
            + Through(polynomial, (new Rational(remainder, 1) + fraction) / Q((long)Period))
            - Through(polynomial, new Rational(Start, Period)));
        var period = Q(4096);
        value -= new Rational(((value / period) + Q(1, 2)).Floor(), 1) * period;
        return value.ToDouble();
    }

    [Theory]
    [InlineData(5368709, 268435456, 4096d)]
    [InlineData(-5368709, 268435456, 4096d)]
    [InlineData(7130317, 4194304, Math.Tau)]
    public void Literal_motion_reduces_before_large_products(long numerator, long denominator, double modulus) {
        var rate = Q(numerator, denominator);
        var literal = (float)rate.ToDouble();
        Assert.True(WorldRateIntegral.TryCompile(literal, new WorldDefinition(), out var integral, out var reason), reason);
        var value = rate * ((new Rational(new BigInteger(ulong.MaxValue), 1) + Q(1, 4)) / Q((long)EngineTicks.PerSecond));
        var period = modulus == 4096d ? Q(4096) : Q(884279719003555, 140737488355328); // Exact binary64 Math.Tau.
        value -= new Rational(((value / period) + Q(1, 2)).Floor(), 1) * period;
        var actual = integral!.At(new PresentedTick(ulong.MaxValue, 0.25d), modulus, out var work);
        var error = Math.Abs(Math.IEEERemainder(actual - value.ToDouble(), modulus));
        Assert.InRange(error, 0d, 1e-12d);
        Assert.Equal(0, work.CoefficientBlends);
        Assert.Equal(0, integral.Cost.Coefficients);
        Assert.InRange(work.PeriodDoublings, 1, 64);
    }

    [Fact]
    public void Literal_key_endpoints_keep_their_exact_binary32_values_during_double_integration() {
        var world = new WorldDefinition(TimelineRaw: new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]));
        var rate = new BindableScalar(new WorldKeys<BindableScalar>("day", [new(0d, 0.1f), new(4d, 1.5f)]));
        Assert.True(WorldRateIntegral.TryCompile(rate, world, out var integral, out var reason), reason);
        // Integral at two seconds: 2*a + (b-a)/2. The authored 0.1f is exactly this rational.
        var first = Q(13421773, 134217728);
        var expected = ((Q(2) * first) + ((Q(3, 2) - first) / Q(2))).ToDouble();
        Assert.InRange(Math.Abs(integral!.At(new PresentedTick(2UL * EngineTicks.PerSecond, 0d), 4096d) - expected), 0d, 1e-14d);
    }

    [Theory]
    [InlineData(0UL, 1, 1e-11d)]
    [InlineData(100799UL, 1, 1e-11d)]
    [InlineData(289799UL, 1, 1e-11d)]
    [InlineData(1UL << 40, 1, 1e-10d)]
    [InlineData(ulong.MaxValue, 1, 2e-4d)]
    [InlineData(ulong.MaxValue, 1048576, 2e-10d)]
    public void The_maximum_degree_matches_an_exact_rational_integral_at_large_and_wrapping_offsets(ulong whole, int divisor, double errorBound) {
        var (world, rate) = Fixture(1f / divisor);
        Assert.True(WorldRateIntegral.TryCompile(rate, world, out var integral, out var reason, name: "wind.speed"), reason);
        Assert.Equal(81, integral!.Cost.Degree);
        Assert.Equal(new WorldRateCost(Pieces: 2, Coefficients: 166, Degree: 81), integral.Cost);
        var actual = integral.At(new PresentedTick(whole, 0.25d), 4096d, out var work);
        var expected = Oracle(Polynomial(), whole, Q(1, 4), Q(1, divisor));
        var error = Math.Abs(Math.IEEERemainder(actual - expected, 4096d));
        TestContext.Current.TestOutputHelper!.WriteLine($"tick={whole}, divisor={divisor}, expected={expected:R}, actual={actual:R}, error={error:R}, bound={errorBound:R}");
        Assert.True(error <= errorBound, $"tick={whole}, divisor={divisor}, expected={expected:R}, actual={actual:R}, error={error:R}, bound={errorBound:R}");
        Assert.Equal(1, work.PieceSearches);
        Assert.Equal(3403, work.CoefficientBlends); // 82*83/2: exactly one degree-82 antiderivative.
        Assert.InRange(work.PeriodDoublings, 0, 64);
    }

    [Fact]
    public void Every_compiled_piece_boundary_is_continuous_and_evaluates_only_one_piece() {
        var (world, rate) = Fixture(1f);
        Assert.True(WorldRateIntegral.TryCompile(rate, world, out var integral, out var reason), reason);
        for (var piece = 0; piece < integral!.Cost.Pieces; piece++) {
            var boundary = ((ulong)Math.Round(integral.PieceStart(piece) * Period) + Period - Start) % Period;
            var before = integral.At(new PresentedTick(boundary - 1UL, 0.999d), 4096d);
            var at = integral.At(new PresentedTick(boundary, 0d), 4096d);
            var after = integral.At(new PresentedTick(boundary, 0.001d), 4096d);
            Assert.InRange(Math.Abs(at - before), 0d, 0.001d / EngineTicks.PerSecond + 1e-12d);
            Assert.InRange(Math.Abs(after - at), 0d, 0.001d / EngineTicks.PerSecond + 1e-12d);
        }
    }

    [Fact]
    public void A_fourth_smooth_phase_is_refused_by_rate_name_full_chain_and_depth() {
        var (world, rate) = Fixture(1f, depth: 4);
        Assert.False(WorldRateIntegral.TryCompile(rate, world, out _, out var reason, name: "wind.speed"));
        Assert.Contains("wind.speed", reason);
        Assert.Contains("day → phase1 → phase2 → phase3 → phase4", reason);
        Assert.Contains("4 smooth phase clocks deep; limit 3", reason);
        // The ordinary keyed value still has a legal clock graph; only analytical rates impose the degree budget.
        Assert.True(WorldValueValidation.TryValidate(rate.Keys!, world, out reason), reason);
    }

    [Theory]
    [InlineData("degree", 5, 2, "degree 243; limit 81")]
    [InlineData("pieces", 0, 1025, "piece count exceeds limit 1024")]
    [InlineData("coefficients", 3, 800, "coefficient count 65570; limit 65536")]
    public void Independent_internal_bounds_refuse_by_rate_and_clock_chain(string bound, int depth, int keyCount, string detail) {
        var (world, rate) = Fixture(1f, depth);
        if (bound == "degree") {
            rate = new BindableScalar(new WorldKeys<BindableScalar>(rate.Keys!.Clock,
                [new(0d, 0f), new(0.5d, 1f)]));
        } else {
            var keys = new WorldKeys<BindableScalar>("day", Enumerable.Range(0, keyCount)
                .Select(index => new WorldKey<BindableScalar>(8d * index / keyCount, (index & 1) == 0 ? 0f : 0.25f, WorldKeyEase.Smooth)).ToArray());
            if (depth == 0) { rate = new BindableScalar(keys); }
            else {
                var clocks = world.Timeline.Clocks!.ToArray();
                clocks[1] = clocks[1] with { Phase = new BindableScalar(keys) };
                world = world with { TimelineRaw = new WorldTimelineSection(clocks) };
            }
        }
        Assert.False(WorldRateIntegral.TryCompile(rate, world, out _, out var reason, name: "wind.speed"));
        Assert.Contains("wind.speed (day", reason);
        Assert.Contains(detail, reason);
    }
}
