using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A function of a phase that repeats every whole turn, made of exact rational polynomial pieces: piece <c>i</c> holds
/// on <c>[start(i), start(i + 1))</c> of the turn <c>[0, 1)</c>, the last piece to the turn's end, each a polynomial in
/// the phase itself. Its antiderivative from zero (<see cref="Integral"/>) is answered in closed form at any rational,
/// however many turns away, as whole turns of <see cref="TurnIntegral"/> plus the integral into the turn reached.
/// </summary>
/// <remarks>
/// <para>This is the shape of a rate that follows another clock: a quantity whose rate is this function of a parent
/// clock's phase advances, between two parent values, by the difference of <see cref="Integral"/> at them, exactly, so
/// it is read at any time by evaluation rather than by stepping, and a lapse of any length costs one read.</para>
/// <para>The pieces need not meet where they join: a rate may jump, and its integral stays continuous across the
/// jump.</para>
/// </remarks>
public sealed class PeriodicPiecewisePolynomial {
    private readonly Rational[] m_starts;
    private readonly RationalPolynomial[] m_pieces;
    // Each piece's antiderivative from zero, and the integral of the whole function from the turn's start to each
    // piece's start, so an integral into a turn is one table read and one evaluation.
    private readonly RationalPolynomial[] m_antiderivatives;
    private readonly Rational[] m_before;

    /// <summary>Initializes a new instance of the <see cref="PeriodicPiecewisePolynomial"/> class from its pieces.</summary>
    /// <param name="starts">Where each piece starts, as a share of a turn: the first zero, each later one greater than
    /// the one before and less than one.</param>
    /// <param name="pieces">Each piece's polynomial in the phase, as many as <paramref name="starts"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="starts"/> is empty, does not begin at zero, does not rise, or
    /// reaches one; or the two spans differ in length.</exception>
    /// <exception cref="ArgumentNullException">A piece is <see langword="null"/>.</exception>
    public PeriodicPiecewisePolynomial(ReadOnlySpan<Rational> starts, ReadOnlySpan<RationalPolynomial> pieces) {
        if (starts.Length == 0) {
            throw new ArgumentException(message: "A periodic piecewise polynomial holds at least one piece.", paramName: nameof(starts));
        }
        if (starts.Length != pieces.Length) {
            throw new ArgumentException(message: $"{starts.Length} piece starts were given for {pieces.Length} pieces.", paramName: nameof(pieces));
        }
        if (!starts[0].IsZero) {
            throw new ArgumentException(message: $"The first piece starts at {starts[0]}; it must start at the turn's start, zero.", paramName: nameof(starts));
        }

        for (var index = 1; (index < starts.Length); ++index) {
            if ((starts[index] <= starts[(index - 1)]) || (starts[index] >= Rational.One)) {
                throw new ArgumentException(message: $"Piece {index} starts at {starts[index]}; each start must rise from the one before and stay below one turn.", paramName: nameof(starts));
            }
        }

        m_starts = starts.ToArray();
        m_pieces = pieces.ToArray();
        m_antiderivatives = new RationalPolynomial[m_pieces.Length];
        m_before = new Rational[(m_pieces.Length + 1)];

        for (var index = 0; (index < m_pieces.Length); ++index) {
            ArgumentNullException.ThrowIfNull(argument: m_pieces[index], paramName: nameof(pieces));

            var antiderivative = m_pieces[index].Antiderivative();
            var end = (((index + 1) < m_starts.Length) ? m_starts[(index + 1)] : Rational.One);

            m_antiderivatives[index] = antiderivative;
            m_before[(index + 1)] = (m_before[index] + (antiderivative.Evaluate(x: end) - antiderivative.Evaluate(x: m_starts[index])));
        }

        TurnIntegral = m_before[^1];
    }

    /// <summary>Gets the number of pieces in one turn.</summary>
    public int PieceCount => m_pieces.Length;
    /// <summary>Gets the integral of the function over one whole turn.</summary>
    public Rational TurnIntegral { get; }
    /// <summary>Gets whether the function is one constant over the whole turn.</summary>
    public bool IsConstant => ((m_pieces.Length == 1) && (m_pieces[0].Degree <= 0));

    /// <summary>Returns the function that is one value over the whole turn.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The constant function.</returns>
    public static PeriodicPiecewisePolynomial Constant(Rational value) => new(
        pieces: [RationalPolynomial.Constant(value: value)],
        starts: [Rational.Zero]
    );

    /// <summary>Evaluates the function at a phase: the piece holding the phase's fraction of a turn, at that
    /// fraction.</summary>
    /// <param name="x">The phase, in turns, at any value.</param>
    /// <returns>The exact value.</returns>
    public Rational Evaluate(Rational x) {
        var fraction = x.Fraction();

        return m_pieces[PieceAt(fraction: fraction)].Evaluate(x: fraction);
    }
    /// <summary>Returns the integral of the function from zero to a phase, exactly: the whole turns before the phase
    /// times <see cref="TurnIntegral"/>, plus the integral from the start of the phase's turn to the phase. A phase below
    /// zero gives the negated integral back to it.</summary>
    /// <param name="x">The phase, in turns, at any value.</param>
    /// <returns>The exact integral.</returns>
    public Rational Integral(Rational x) {
        var turn = x.Floor();
        var fraction = x.Fraction();
        var piece = PieceAt(fraction: fraction);
        var antiderivative = m_antiderivatives[piece];

        return (((TurnIntegral * turn) + m_before[piece]) + (antiderivative.Evaluate(x: fraction) - antiderivative.Evaluate(x: m_starts[piece])));
    }
    /// <summary>Returns the polynomial <see cref="Integral"/> equals across one piece of one turn: for every phase
    /// <c>x</c> in <c>[turn + start(piece), turn + start(piece + 1))</c>, <c>Integral(x)</c> is this polynomial at
    /// <c>x</c>. A quantity that composes the integral with a polynomial of its own variable stays one polynomial there.</summary>
    /// <param name="turn">The whole turn.</param>
    /// <param name="piece">The piece, from zero to <see cref="PieceCount"/> less one.</param>
    /// <returns>The polynomial in the phase.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="piece"/> names no piece.</exception>
    public RationalPolynomial IntegralOn(BigInteger turn, int piece) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: piece);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: m_pieces.Length, value: piece);

        var antiderivative = m_antiderivatives[piece];
        var offset = (((TurnIntegral * turn) + m_before[piece]) - antiderivative.Evaluate(x: m_starts[piece]));

        return (antiderivative.Shift(offset: -((Rational)turn)) + RationalPolynomial.Constant(value: offset));
    }
    /// <summary>Returns the piece a fraction of a turn falls in: the last piece starting at or before it.</summary>
    /// <param name="fraction">The fraction of a turn, in <c>[0, 1)</c>.</param>
    /// <returns>The piece, from zero to <see cref="PieceCount"/> less one.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fraction"/> is below zero or at or above one.</exception>
    public int PieceAt(Rational fraction) {
        if ((fraction.Sign < 0) || (fraction >= Rational.One)) {
            throw new ArgumentOutOfRangeException(actualValue: fraction, message: "A piece is found from a fraction of a turn, in [0, 1).", paramName: nameof(fraction));
        }

        return (PartitionPoint.Of(
            isBefore: static (fraction, start) => (start <= fraction),
            span: (ReadOnlySpan<Rational>)m_starts,
            state: fraction
        ) - 1);
    }
    /// <summary>Returns the polynomial of one piece, in the phase.</summary>
    /// <param name="piece">The piece, from zero to <see cref="PieceCount"/> less one.</param>
    /// <returns>The piece's polynomial.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="piece"/> names no piece.</exception>
    public RationalPolynomial Piece(int piece) => m_pieces[piece];
    /// <summary>Returns where one piece ends within its turn: the next piece's start, or one for the last piece.</summary>
    /// <param name="piece">The piece, from zero to <see cref="PieceCount"/> less one.</param>
    /// <returns>The end, as a share of a turn, in <c>(0, 1]</c>.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="piece"/> names no piece.</exception>
    public Rational PieceEnd(int piece) => (((piece + 1) < m_starts.Length)
        ? m_starts[(piece + 1)]
        : Rational.One
    );
    /// <summary>Returns where one piece starts within its turn.</summary>
    /// <param name="piece">The piece, from zero to <see cref="PieceCount"/> less one.</param>
    /// <returns>The start, as a share of a turn, in <c>[0, 1)</c>.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="piece"/> names no piece.</exception>
    public Rational PieceStart(int piece) => m_starts[piece];
}
