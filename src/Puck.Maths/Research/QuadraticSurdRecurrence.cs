using System.Numerics;

namespace Puck.Maths.Research;

/// <summary>
/// The classical surd recurrence on the canonical form <c>(P + √N) / Q</c> — one step of the continued-fraction
/// expansion of a quadratic irrational, in exact integer arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// The state <c>(P, Q)</c> is finite for a fixed <c>N</c>, so every expansion is eventually periodic; which repeated
/// state a caller watches for is its own policy, and no policy lives here. This carries the arithmetic alone: the
/// canonicalization, the floor of the current value, and the step to the next state.
/// </para>
/// <para>
/// The recurrence is a value: a copy advances independently of the original, which is how a caller branches a walk.
/// </para>
/// <para>
/// <see cref="BigInteger"/> is part of the exactness contract, not a fallback. Callers whose parameters are
/// individually 64-bit still reach 315 bits here, because <see cref="FromQuadraticIrrational"/> forms
/// <c>surdCoefficient² · radicand</c> and then rescales it by <c>denominator²</c>; narrowing those products to
/// <see cref="Int128"/> silently changed the represented irrational for otherwise valid inputs.
/// </para>
/// </remarks>
internal struct QuadraticSurdRecurrence {
    // N and ⌊√N⌋ of the canonical form, fixed for the whole expansion.
    private readonly BigInteger m_radicandProduct;
    private readonly BigInteger m_radicandRoot;

    private QuadraticSurdRecurrence(BigInteger radicandProduct, BigInteger radicandRoot, BigInteger stateP, BigInteger stateQ) {
        m_radicandProduct = radicandProduct;
        m_radicandRoot = radicandRoot;
        StateP = stateP;
        StateQ = stateQ;
    }

    /// <summary>Gets <c>P</c>, the numerator offset of the current state.</summary>
    internal BigInteger StateP { get; private set; }
    /// <summary>Gets <c>Q</c>, the denominator of the current state.</summary>
    internal BigInteger StateQ { get; private set; }

    /// <summary>Starts the recurrence at a state that already satisfies the divisibility invariant.</summary>
    /// <param name="radicandProduct">The radicand <c>N</c> of the canonical form.</param>
    /// <param name="radicandRoot"><c>⌊√N⌋</c>.</param>
    /// <param name="stateP">The numerator offset <c>P</c> of the start state.</param>
    /// <param name="stateQ">The denominator <c>Q</c> of the start state; it must divide <c>N − P²</c>.</param>
    /// <returns>The recurrence positioned at that state, unscaled.</returns>
    /// <remarks>
    /// The caller supplies <c>⌊√N⌋</c> because a start built from an ideal already has it, and asserts that <c>Q</c>
    /// divides <c>N − P²</c>, which the ideal's defining congruence guarantees. No scaling happens, so the caller's
    /// <c>(P, Q)</c> stay the ones its own identities are written against.
    /// </remarks>
    internal static QuadraticSurdRecurrence FromDividingState(
        BigInteger radicandProduct,
        BigInteger radicandRoot,
        BigInteger stateP,
        BigInteger stateQ) =>
        new(
            radicandProduct: radicandProduct,
            radicandRoot: radicandRoot,
            stateP: stateP,
            stateQ: stateQ
        );
    /// <summary>Starts the recurrence at the quadratic irrational <c>(p + q·√d) / r</c>.</summary>
    /// <param name="rationalNumerator">The rational part <c>p</c> of the numerator.</param>
    /// <param name="surdCoefficient">The coefficient <c>q</c> of the surd.</param>
    /// <param name="radicand">The radicand <c>d</c>.</param>
    /// <param name="denominator">The denominator <c>r</c>; it must be non-zero.</param>
    /// <returns>The recurrence positioned at the canonical form of that irrational.</returns>
    /// <remarks>
    /// The canonical form is <c>(P + √N) / Q</c> with <c>N = surdCoefficient² · radicand</c>, scaled when needed so that
    /// <c>Q</c> divides <c>N − P²</c> — the invariant that makes every later step divide exactly. The scale multiplies
    /// <c>P</c> and <c>Q</c> by <c>|Q|</c> and <c>N</c> by <c>Q²</c>, which leaves the value unchanged and carries the
    /// invariant forward: <c>N' − P'² = Q²·(N − P²)</c> and <c>Q' = ±Q²</c>.
    /// </remarks>
    internal static QuadraticSurdRecurrence FromQuadraticIrrational(
        BigInteger rationalNumerator,
        BigInteger surdCoefficient,
        BigInteger radicand,
        BigInteger denominator) {
        var stateP = rationalNumerator;
        var stateN = ((surdCoefficient * surdCoefficient) * radicand);
        var stateQ = denominator;

        if (BigInteger.Zero != ((stateN - (stateP * stateP)) % stateQ)) {
            var magnitude = BigInteger.Abs(value: stateQ);

            stateP *= magnitude;
            stateN *= (stateQ * stateQ);
            stateQ *= magnitude;
        }

        return new QuadraticSurdRecurrence(
            radicandProduct: stateN,
            radicandRoot: BigIntegerFunctions.SquareRoot(value: stateN),
            stateP: stateP,
            stateQ: stateQ
        );
    }
    /// <summary>Returns the partial quotient of the current state and advances to the next one.</summary>
    /// <returns>The partial quotient <c>⌊(P + √N) / Q⌋</c> of the state held on entry.</returns>
    internal BigInteger Step() {
        // Floor of (P + √N) / Q: for a positive denominator the numerator floors with ⌊√N⌋; for a negative one the surd
        // sits just below ⌊√N⌋ + 1, which is the bound that floors correctly once the sign flips the inequality.
        var quotient = ((0 < StateQ.Sign)
            ? (StateP + m_radicandRoot).FloorDivide(divisor: StateQ)
            : ((StateP + m_radicandRoot) + BigInteger.One).FloorDivide(divisor: StateQ)
        );
        var nextP = ((quotient * StateQ) - StateP);

        StateQ = ((m_radicandProduct - (nextP * nextP)) / StateQ);
        StateP = nextP;

        return quotient;
    }
}
/// <summary>
/// The eventually periodic continued-fraction expansion of a quadratic irrational, driven one partial quotient at a
/// time until the state repeats.
/// </summary>
/// <remarks>
/// The caller owns the terms: each <see cref="MoveNext"/> exposes one partial quotient and its index, and the enumerator
/// stops at the first repeated <c>(P, Q)</c>, which is where the period begins. A caller that needs the recurrence
/// without this bookkeeping — a constant-space cycle test, or convergents rather than terms — drives
/// <see cref="QuadraticSurdRecurrence"/> directly. This is a reference type because the seen-set and the recurrence
/// advance as one: a copied value would fork the recurrence while sharing the seen-set.
/// </remarks>
internal sealed class QuadraticSurdExpansion {
    private readonly Dictionary<(BigInteger P, BigInteger Q), int> m_seen;

    private QuadraticSurdRecurrence m_recurrence;

    /// <summary>Starts the expansion of the quadratic irrational <c>(p + q·√d) / r</c>.</summary>
    /// <param name="rationalNumerator">The rational part <c>p</c> of the numerator.</param>
    /// <param name="surdCoefficient">The coefficient <c>q</c> of the surd.</param>
    /// <param name="radicand">The radicand <c>d</c>.</param>
    /// <param name="denominator">The denominator <c>r</c>; it must be non-zero.</param>
    internal QuadraticSurdExpansion(
        BigInteger rationalNumerator,
        BigInteger surdCoefficient,
        BigInteger radicand,
        BigInteger denominator) {
        m_recurrence = QuadraticSurdRecurrence.FromQuadraticIrrational(
            denominator: denominator,
            radicand: radicand,
            rationalNumerator: rationalNumerator,
            surdCoefficient: surdCoefficient
        );
        m_seen = [];
        Quotient = BigInteger.Zero;
        Index = -1;
        PeriodStart = -1;
        PeriodLength = -1;
    }

    /// <summary>Gets the number of partial quotients in the pre-period and one period block.</summary>
    internal int Count => (PeriodStart + PeriodLength);
    /// <summary>Gets the index of the current position.</summary>
    internal int Index { get; private set; }
    /// <summary>Gets the length of the repeating block, once the expansion has closed.</summary>
    internal int PeriodLength { get; private set; }
    /// <summary>Gets the index where the repeating block begins, once the expansion has closed.</summary>
    internal int PeriodStart { get; private set; }
    /// <summary>Gets the partial quotient of the current position.</summary>
    internal BigInteger Quotient { get; private set; }

    /// <summary>Advances to the next partial quotient.</summary>
    /// <returns><see langword="true"/> when a further partial quotient is available; <see langword="false"/> once the state repeats, which is where the period is settled.</returns>
    internal bool MoveNext() {
        if (m_seen.TryGetValue(
            key: (m_recurrence.StateP, m_recurrence.StateQ),
            value: out var repeatAt
        )) {
            PeriodStart = repeatAt;
            PeriodLength = (m_seen.Count - repeatAt);

            return false;
        }

        Index = m_seen.Count;
        m_seen.Add(
            key: (m_recurrence.StateP, m_recurrence.StateQ),
            value: Index
        );
        Quotient = m_recurrence.Step();

        return true;
    }
}
