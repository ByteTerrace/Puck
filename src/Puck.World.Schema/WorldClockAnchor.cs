using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// What a projection carries in place of a state clock's row: the clock's phase at one authoritative tick and the
/// polynomial it follows from there. The authority and the recipient call the one prediction, <see cref="Predict"/>, so
/// the authority knows, at every authoritative tick, the phase the recipient presents, and sends a new anchor exactly
/// when that prediction differs from its own phase (<see cref="WorldClockAnchorLedger"/>).
/// </summary>
/// <remarks>
/// <para>A phase is a whole turn as an unsigned 64-bit count: <c>2^64</c> is one turn, and arithmetic wraps, so a clock
/// that passes a whole unit needs no reduction. A Fixed row's phase is its fractional bits, exactly
/// (<see cref="PhaseOf"/>), so the phase a recipient predicts is the phase the authority's own presentation reads, bit
/// for bit, at every authoritative tick.</para>
/// <para>The curve is the row's accumulation over its stored base, in raw units, as a polynomial in the engine ticks
/// elapsed since the anchor (<see cref="StateAdvance.TryValueCurve"/>): the row reads its base plus the curve truncated
/// toward zero, as the authority's own read truncates its exact accumulation (<see cref="StateAdvance"/>). An affine
/// advance's curve has degree one; a nested advance's is the composition of its rate's integral with its parents' own
/// curves, valid until a parent crosses a piece of its shape. The authority sends a curve only over a span it has
/// proved (<see cref="WorldClockAnchors.Read"/>); an eased, cycling, clamped or written row carries none and
/// re-anchors wherever its phase moves.</para>
/// <para>An anchor predicts forward only. The authority keeps one anchor per recipient per clock and replaces it
/// whenever a prediction misses, so anchors a recipient was never sent leave no trace in the one it holds: the phase
/// before an anchor's tick is not the curve extended backward, nor anything else a recipient can know.
/// <see cref="Predict"/> refuses a tick before the anchor, and a presentation between the delivery before an anchor and
/// the anchor's own tick presents the anchor's phase (<see cref="PhaseAt"/>).</para>
/// </remarks>
/// <param name="Tick">The engine tick the anchor stands at.</param>
/// <param name="Phase">The phase at <paramref name="Tick"/>, a whole turn being <c>2^64</c>.</param>
/// <param name="Base">The row's stored raw base the curve accumulates over; zero for a clock held still.</param>
/// <param name="Curve">The accumulation over <paramref name="Base"/>, in raw units, as exact rational coefficients of a
/// polynomial in the engine ticks elapsed since <paramref name="Tick"/>, constant term first; absent for a clock held
/// still.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldClockAnchor(
    ulong Tick,
    ulong Phase,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Base = 0L,
    [property: DocumentText("exact rational coefficients")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Curve = null
) {
    /// <summary>The most coefficients a curve carries, the bound the state side composes under
    /// (<see cref="StateAdvance.MaxCurveCoefficients"/>): a curve past it is refused as malformed.</summary>
    public const int MaxCurveCoefficients = StateAdvance.MaxCurveCoefficients;
    /// <summary>The most bits the reduced numerator or denominator of a curve's coefficient may take
    /// (<see cref="StateAdvance.MaxCurveCoefficientBits"/>): a curve past it is refused as malformed.</summary>
    public const int MaxCurveCoefficientBits = StateAdvance.MaxCurveCoefficientBits;

    // 2^-64: one phase unit as a share of a turn.
    private const double TurnPerUnit = (1d / 18446744073709551616d);
    private static readonly BigInteger RawPerUnit = (BigInteger.One << FixedQ4816.FractionBitCount);

    // The parsed curve, keyed by the coefficients it was parsed from, so a `with` copy that replaces them parses again.
    private ParsedCurve? m_parsed;

    private sealed record ParsedCurve(IReadOnlyList<string>? Source, RationalPolynomial? Polynomial, RationalPolynomial Slope);

    /// <summary>Gets whether the anchor is well formed: no curve, or a curve of exact rational coefficients, at most
    /// <see cref="MaxCurveCoefficients"/>, each of at most <see cref="MaxCurveCoefficientBits"/> bits a part, whose value
    /// at the anchor's tick is the anchor's phase.</summary>
    [JsonIgnore]
    public bool IsWellFormed => ((Curve is null) || ((Parsed() is { } curve) && (PhaseOf(kind: CellKind.Fixed, raw: Truncated(curve: curve, elapsed: Rational.Zero)) == Phase)));

    /// <summary>Returns a value's phase: a Fixed value's fractional bits as a share of <c>2^64</c>, exactly; zero for
    /// every other kind, whose values are whole.</summary>
    /// <param name="kind">The row's kind.</param>
    /// <param name="raw">The raw value.</param>
    /// <returns>The phase.</returns>
    public static ulong PhaseOf(CellKind kind, long raw) => ((kind == CellKind.Fixed)
        ? unchecked((((ulong)raw) << (64 - FixedQ4816.FractionBitCount)))
        : 0UL
    );
    /// <summary>Returns a phase as a share of a turn, in <c>[0, 1)</c>.</summary>
    /// <param name="phase">The phase.</param>
    /// <returns>The share.</returns>
    public static double ToTurn(ulong phase) {
        var turn = (phase * TurnPerUnit);

        // The nearest double to a phase within half a unit of a whole turn rounds up to it.
        return ((turn >= 1d)
            ? 0d
            : turn
        );
    }
    /// <summary>Returns the curve a projection carries for a polynomial accumulation: each exact coefficient as
    /// <see cref="Rational.ToString"/> writes it, which <see cref="Rational.TryParse"/> reads back exactly.</summary>
    /// <param name="curve">The accumulation, in raw units, over the elapsed engine ticks.</param>
    /// <returns>The coefficients, constant term first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="curve"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Spell(RationalPolynomial curve) {
        ArgumentNullException.ThrowIfNull(argument: curve);

        var coefficients = new string[Math.Max(val1: 1, val2: curve.Coefficients.Length)];

        for (var power = 0; (power < coefficients.Length); power++) {
            coefficients[power] = curve[power].ToString();
        }

        return coefficients;
    }
    /// <summary>Predicts the phase at an engine tick at or after the anchor: the base plus the curve at the engine ticks
    /// elapsed, truncated toward zero, as a Fixed phase. Exact at every engine tick the curve's span covers.</summary>
    /// <param name="engineTick">The engine tick, at or after <see cref="Tick"/>.</param>
    /// <returns>The predicted phase.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="engineTick"/> stands before the anchor, where the
    /// anchors the recipient was not sent decided the phase.</exception>
    public ulong Predict(ulong engineTick) {
        if (engineTick < Tick) {
            throw new ArgumentOutOfRangeException(
                actualValue: engineTick,
                message: $"an anchor at engine tick {Tick} predicts no phase at engine tick {engineTick}: a recipient never seeks backward through anchors it was not sent.",
                paramName: nameof(engineTick)
            );
        }

        if (Parsed() is not { } curve) {
            return Phase;
        }

        return PhaseOf(kind: CellKind.Fixed, raw: Truncated(curve: curve, elapsed: new Rational(Numerator: (engineTick - Tick), Denominator: BigInteger.One)));
    }
    /// <summary>Returns the phase a frame presents at a presented tick, as a share of a turn: the prediction at its whole
    /// tick, moved on along the curve's slope over its fraction. A frame before the anchor, interpolating toward the
    /// delivery that brought it, presents the anchor's own phase: the latest authoritative tick the recipient was told
    /// about.</summary>
    /// <param name="tick">The presented tick.</param>
    /// <returns>The phase, in <c>[0, 1)</c>.</returns>
    public double PhaseAt(PresentedTick tick) {
        if (tick.Whole < Tick) {
            return ToTurn(phase: Phase);
        }
        if (Parsed() is null) {
            return ToTurn(phase: Phase);
        }

        // The whole tick presents the prediction, the authority's own read; only the sub-tick motion, along the curve's
        // slope there, is a double.
        var whole = ToTurn(phase: Predict(engineTick: tick.Whole));

        if (tick.Fraction == 0d) {
            return whole;
        }

        var slope = m_parsed!.Slope.Evaluate(x: new Rational(Numerator: (tick.Whole - Tick), Denominator: BigInteger.One)).ToDouble();
        var turn = (whole + ((slope / ((double)RawPerUnit)) * tick.Fraction));

        return (turn - Math.Floor(d: turn));
    }

    /// <summary>Tests equality over the authored members, the curve compared coefficient by coefficient; the parsed
    /// curve is a cache and never part of the record's identity.</summary>
    /// <param name="other">The other anchor.</param>
    /// <returns><see langword="true"/> when both anchors stand at one tick and phase and carry one base and curve.</returns>
    public bool Equals(WorldClockAnchor? other) =>
        ((other is not null) && (Tick == other.Tick) && (Phase == other.Phase) && (Base == other.Base) &&
            SequenceEqualityComparer<string>.Instance.Equals(x: Curve, y: other.Curve));
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        value1: Tick,
        value2: Phase,
        value3: Base,
        value4: (Curve?.Count ?? -1)
    );

    // The raw value the base and the curve read at an elapsed span: the base plus the curve truncated toward zero,
    // saturated into long.
    private long Truncated(RationalPolynomial curve, Rational elapsed) {
        var raw = (Base + curve.Evaluate(x: elapsed).Truncate());

        return ((raw > long.MaxValue)
            ? long.MaxValue
            : ((raw < long.MinValue)
                ? long.MinValue
                : ((long)raw)));
    }
    private RationalPolynomial? Parsed() {
        var parsed = m_parsed;

        if ((parsed is null) || !ReferenceEquals(objA: parsed.Source, objB: Curve)) {
            var polynomial = Parse(curve: Curve);

            parsed = new ParsedCurve(Polynomial: polynomial, Slope: (polynomial?.Derivative() ?? RationalPolynomial.Zero), Source: Curve);
            m_parsed = parsed;
        }

        return parsed.Polynomial;

        static RationalPolynomial? Parse(IReadOnlyList<string>? curve) {
            if (curve is not { Count: > 0 and <= MaxCurveCoefficients }) {
                return null;
            }

            var coefficients = new Rational[curve.Count];

            for (var power = 0; (power < curve.Count); power++) {
                if ((curve[power] is not { } text) || !Rational.TryParseBounded(text: text, maxBits: MaxCurveCoefficientBits, value: out coefficients[power])) {
                    return null;
                }
            }

            var polynomial = new RationalPolynomial(coefficients: coefficients);

            return (polynomial.Fits(maxBits: MaxCurveCoefficientBits, maxDegree: StateAdvance.MaxCurveDegree)
                ? polynomial
                : null);
        }
    }
}
