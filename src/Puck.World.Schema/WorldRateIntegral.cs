using Puck.Hosting;

namespace Puck.World;

/// <summary>A compiled, history-free integral of a presentation rate. Key segments and composed phase curves are
/// polynomials; compilation splits them at crossed keys and integrates their coefficients. A frame evaluates one
/// antiderivative and a whole-period total, without sampling past frames or taking numerical integration steps.</summary>
public sealed class WorldRateIntegral {
    private const int MaximumCoefficients = 65536;
    private const int MaximumPieces = 1024;

    private readonly float? m_literal;
    private readonly ulong m_period;
    private readonly ulong m_start;
    private readonly Piece[] m_pieces;
    private readonly double m_total;
    private readonly double m_origin;

    private WorldRateIntegral(float literal) { m_literal = literal; m_pieces = []; }
    private WorldRateIntegral(WorldClock root, Piece[] pieces) {
        m_period = WorldClocks.PeriodTicks(clock: root);
        m_start = WorldClocks.StartTicks(clock: root);
        m_pieces = pieces;
        var accumulated = 0d;
        var correction = 0d;
        var seconds = (m_period / ((double)EngineTicks.PerSecond));

        for (var index = 0; (index < pieces.Length); index++) {
            var piece = pieces[index];
            var integral = new double[(piece.Coefficients.Length + 1)];

            for (var point = 0; (point < piece.Coefficients.Length); point++) {
                integral[(point + 1)] = (integral[point] + (((piece.Coefficients[point] * (piece.End - piece.Start)) * seconds) / piece.Coefficients.Length));
            }
            pieces[index] = piece with { Coefficients = integral, Prefix = accumulated };
            var contribution = (integral[^1] - correction);
            var next = (accumulated + contribution);

            correction = ((next - accumulated) - contribution);
            accumulated = next;
        }
        m_total = accumulated;
        m_origin = Through(blends: out _, phase: (m_start / ((double)m_period)), searches: out _);
    }

    /// <summary>The retained piece, coefficient and polynomial-degree counts of this compiled rate.</summary>
    public WorldRateCost Cost => new(Pieces: m_pieces.Length, Coefficients: m_pieces.Sum(selector: static piece => piece.Coefficients.Length),
        Degree: ((m_pieces.Length == 0) ? 0 : m_pieces.Max(selector: static piece => (piece.Coefficients.Length - 2))));

    /// <summary>Returns a piece's first phase in the root tick clock, for counted inspection and boundary checks.</summary>
    /// <param name="index">The zero-based piece index.</param>
    /// <returns>The root phase in [0, 1).</returns>
    public double PieceStart(int index) => m_pieces[index].Start;
    /// <summary>Compiles a literal rate or a literal-valued key curve driven entirely by tick clocks.</summary>
    /// <param name="rate">The rate in units per second.</param>
    /// <param name="definition">The world declaring the curve's clocks.</param>
    /// <param name="integral">The compiled integral when accepted.</param>
    /// <param name="reason">A named refusal, or an empty string.</param>
    /// <param name="name">The authored rate field, included in complexity refusals.</param>
    /// <returns>Whether the rate has a history-independent integral.</returns>
    public static bool TryCompile(BindableScalar rate, WorldDefinition definition, out WorldRateIntegral? integral, out string reason, string name = "rate") {
        integral = null;
        if (rate.State is not null) {
            reason = "a rate may not bind state directly; its integral would depend on that state's history";
            return false;
        }
        if (rate.Keys is not { } keys) {
            if ((rate.Literal is not { } literal) || !float.IsFinite(f: literal)) {
                reason = "a rate requires a finite literal or keys on a tick-driven clock";
                return false;
            }
            integral = new WorldRateIntegral(literal: literal);
            reason = string.Empty;
            return true;
        }
        if (!WorldValueValidation.TryValidate(curve: keys, definition: definition, reason: out reason)) { return false; }
        for (var index = 0; (index < keys.Keys.Count); index++) {
            if ((keys.Keys[index].Value.Literal is not { } literal) || !float.IsFinite(f: literal)) {
                reason = $"rate key {index} on clock '{keys.Clock}' requires a finite literal; state-driven rates depend on history";
                return false;
            }
        }
        var resolver = new WorldValueResolver(definition, default);
        var clock = resolver.Clock(name: keys.Clock)!;
        var chain = new List<WorldClock>();

        for (var current = clock; ; current = resolver.Clock(name: current.Phase!.Value.Keys!.Clock)!) {
            chain.Add(item: current);
            if (current.Phase?.Keys is null) { break; }
        }
        chain.Reverse();
        var path = $"{name} ({string.Join(separator: " → ", values: chain.Select(selector: static item => item.Name))})";
        var smoothDepth = chain.Count(predicate: static item => (item.Phase?.Keys?.Keys.Any(predicate: static key => (key.Ease == WorldKeyEase.Smooth)) == true));

        if (keys.Keys.Any(predicate: static key => (key.Ease == WorldKeyEase.Smooth)) && (smoothDepth > 3)) {
            reason = $"{path}: {smoothDepth} smooth phase clocks deep; limit 3 for a smooth rate";
            return false;
        }
        try {
            if (!CompileClock(clock: clock, path: path, pieces: out var phase, reason: out reason, resolver: resolver, root: out var root)) { return false; }
            integral = new WorldRateIntegral(root!, Apply(phase!, keys, clock.Span, path));
            return true;
        } catch (RateRefusal refusal) { reason = refusal.Message; return false; }
    }
    /// <summary>Evaluates the integral from tick zero, reduced by the field's repeat period.</summary>
    /// <param name="tick">The presented tick.</param>
    /// <param name="modulus">The positive repeat period in integrated units.</param>
    /// <returns>The reduced displacement.</returns>
    public double At(PresentedTick tick, double modulus) => At(modulus: modulus, tick: tick, work: out _);
    /// <summary>Evaluates only the active piece and reports the work actually performed.</summary>
    /// <param name="tick">The presented tick.</param>
    /// <param name="modulus">The positive repeat period in integrated units.</param>
    /// <param name="work">Piece-search comparisons, Bernstein blends and period doublings performed by this call.</param>
    /// <returns>The reduced displacement.</returns>
    public double At(PresentedTick tick, double modulus, out WorldRateEvaluation work) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(modulus);
        if (m_literal is { } literal) {
            var whole = MultiplyReduced(literal, (tick.Whole / EngineTicks.PerSecond), modulus, out var literalDoublings);
            var literalRemainder = (((tick.Whole % EngineTicks.PerSecond) + tick.Fraction) / EngineTicks.PerSecond);

            work = new(CoefficientBlends: 0, PeriodDoublings: literalDoublings, PieceSearches: 0);
            return Math.IEEERemainder(x: (whole + (literal * literalRemainder)), y: modulus);
        }
        var shifted = (((UInt128)tick.Whole) + m_start);
        var periods = ((ulong)(shifted / m_period));
        var remainder = ((ulong)(shifted % m_period));
        var phase = ((remainder + tick.Fraction) / m_period);
        var cycles = MultiplyReduced(count: periods, doublings: out var doublings, modulus: modulus, value: m_total);
        var active = Through(blends: out var blends, phase: phase, searches: out var searches);

        work = new(CoefficientBlends: blends, PeriodDoublings: doublings, PieceSearches: searches);
        return Math.IEEERemainder(x: ((cycles + active) - m_origin), y: modulus);
    }

    private double Through(double phase, out int searches, out int blends) {
        searches = blends = 0;
        if (phase >= 1d) { return m_total; }
        var lower = 0;
        var upper = (m_pieces.Length - 1);

        while (lower < upper) {
            searches++;
            var middle = ((lower + upper) / 2);

            if (phase >= m_pieces[middle].End) { lower = (middle + 1); } else { upper = middle; }
        }
        var piece = m_pieces[lower];

        blends = ((piece.Coefficients.Length * (piece.Coefficients.Length - 1)) / 2);
        return (piece.Prefix + WorldRatePolynomial.Evaluate(piece.Coefficients, ((phase - piece.Start) / (piece.End - piece.Start))));
    }
    // Reduce before each doubling rather than converting an unbounded period count to double, which would lose
    // low bits after 2^53 periods. This loop has at most 64 iterations and creates no per-frame storage.
    private static double MultiplyReduced(double value, ulong count, double modulus, out int doublings) {
        var result = 0d;

        doublings = 0;
        value = Math.IEEERemainder(x: value, y: modulus);
        while (count != 0UL) {
            doublings++;
            if ((count & 1UL) != 0UL) { result = Math.IEEERemainder(x: (result + value), y: modulus); }
            count >>= 1;
            value = Math.IEEERemainder(x: (value + value), y: modulus);
        }
        return result;
    }
    private static bool CompileClock(WorldClock clock, WorldValueResolver resolver, string path, out WorldClock? root, out Piece[]? pieces, out string reason) {
        root = null;
        pieces = null;
        if (clock.State is not null) {
            reason = $"rate clock '{clock.Name}' reads state; its integral would depend on that state's history";
            return false;
        }
        if (clock.Phase?.Keys is { } keys) {
            var parent = resolver.Clock(name: keys.Clock)!;

            if (!CompileClock(clock: parent, path: path, pieces: out var parentPieces, reason: out reason, resolver: resolver, root: out root)) { return false; }
            pieces = Apply(parentPieces!, keys, parent.Span, path);
            // A constant authored phase of one is phase zero, including throughout a step segment.
            for (var index = 0; (index < pieces.Length); index++) {
                if (pieces[index].Coefficients.Length == 1) {
                    pieces[index].Coefficients[0] = WorldClocks.Phase(value: pieces[index].Coefficients[0]);
                }
            }
        } else {
            root = clock;
            pieces = [new Piece(0d, 1d, [0d, 1d])];
        }
        reason = string.Empty;
        return true;
    }
    private static Piece[] Apply(Piece[] phases, WorldKeys<BindableScalar> curve, double span, string path) {
        var result = new List<Piece>();
        var retained = 0;

        foreach (var phase in phases) {
            var cuts = new List<double> { 0d, 1d };
            var a = phase.Coefficients[0];
            var b = phase.Coefficients[^1];

            for (var index = 0; (index < curve.Keys.Count); index++) {
                var boundary = (curve.Keys[index].At / span);

                if ((boundary > Math.Min(val1: a, val2: b)) && (boundary < Math.Max(val1: a, val2: b))) {
                    cuts.Add(item: Crossing(phase.Coefficients, boundary, (b > a)));
                }
            }
            cuts.Sort();
            for (var interval = 0; ((interval + 1) < cuts.Count); interval++) {
                var lower = cuts[interval];
                var upper = cuts[(interval + 1)];

                if (upper <= lower) { continue; }
                var sample = WorldClocks.Phase(value: WorldRatePolynomial.Evaluate(phase.Coefficients, ((lower + upper) * 0.5d)));

                WorldKeyInterpolation.Select(at: (sample * span), curve: curve, duration: out var keyDuration, from: out var from, offset: out _, span: span, to: out var to);
                var start = (from.At / span);
                var duration = (keyDuration / span);
                var coefficients = WorldRatePolynomial.Slice(phase.Coefficients, lower, upper);

                for (var point = 0; (point < coefficients.Length); point++) {
                    coefficients[point] = Math.Clamp((((coefficients[point] + ((sample < start) ? 1d : 0d)) - start) / duration), 0d, 1d);
                }
                var outer = WorldKeyInterpolation.ControlPoints(ease: from.Ease);
                var degree = ((coefficients.Length - 1) * (outer.Length - 1));

                if (degree > WorldRatePolynomial.MaximumDegree) { throw new RateRefusal(message: $"{path}: compiled polynomial degree {degree}; limit {WorldRatePolynomial.MaximumDegree}"); }
                coefficients = WorldRatePolynomial.Compose(inner: coefficients, outer: outer);
                double first = from.Value.Literal!.Value;
                var delta = (to.Value.Literal!.Value - first);

                for (var point = 0; (point < coefficients.Length); point++) { coefficients[point] = (first + (coefficients[point] * delta)); }
                if (coefficients.All(predicate: value => (value == coefficients[0]))) { coefficients = [coefficients[0]]; }
                if (result.Count >= MaximumPieces) { throw new RateRefusal(message: $"{path}: compiled piece count exceeds limit {MaximumPieces}"); }
                retained += (coefficients.Length + 1);
                if (retained > MaximumCoefficients) { throw new RateRefusal(message: $"{path}: compiled coefficient count {retained}; limit {MaximumCoefficients}"); }
                result.Add(item: new Piece((phase.Start + ((phase.End - phase.Start) * lower)), (phase.Start + ((phase.End - phase.Start) * upper)), coefficients));
            }
        }
        return [.. result];
    }
    // Each phase segment is monotone: linear/smooth/step interpolation and composition preserve that property.
    // This locates its key crossing once at compile time; integration itself is the polynomial antiderivative.
    private static double Crossing(double[] coefficients, double value, bool ascending) {
        var lower = 0d;
        var upper = 1d;

        for (var iteration = 0; (iteration < 56); iteration++) {
            var middle = ((lower + upper) * 0.5d);

            if ((WorldRatePolynomial.Evaluate(at: middle, points: coefficients) < value) == ascending) { lower = middle; } else { upper = middle; }
        }
        return ((lower + upper) * 0.5d);
    }

    private sealed class RateRefusal(string message) : Exception(message);
    private sealed record Piece(double Start, double End, double[] Coefficients, double Prefix = 0d);
}
