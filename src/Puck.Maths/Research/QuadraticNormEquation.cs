using System.Numerics;

namespace Puck.Maths.Research;

/// <summary>A solution of a real quadratic order's norm equation <c>X² − Δ·Y² = 4·N</c>.</summary>
/// <param name="X">The non-negative rational coordinate.</param>
/// <param name="Y">The positive square-root coordinate.</param>
/// <param name="NormSign">The sign of <c>N</c> — exactly <c>1</c> or <c>-1</c>.</param>
internal readonly record struct QuadraticNormSolution(BigInteger X, BigInteger Y, int NormSign);
/// <summary>
/// The norm equation <c>X² − Δ·Y² = 4·N</c> of a real quadratic order — the one primitive beneath every fundamental-unit
/// and prime-norm question this library asks, solved by the continued fraction of the ideal of norm <c>|N|</c>.
/// </summary>
/// <remarks>
/// <para>
/// The discriminant alone determines the answer, so the descriptor's linear coefficient stays at the call site: the
/// descriptors <c>(P, Q) = (1, 1)</c> and <c>(3, −1)</c> both name <c>Δ = 5</c> and share the unit solution
/// <c>(X, Y) = (1, 1)</c>, differing only in the scalar coordinate <c>(X − P·Y)/2</c> the caller reconstitutes.
/// </para>
/// <para>
/// Mechanism. Every ideal of norm <c>m</c> in the order of discriminant <c>Δ</c> is <c>[m, (b + √Δ)/2]</c> for a root of
/// <c>b² ≡ Δ (mod 4m)</c>, and it is principal exactly when the order carries an element of norm <c>±m</c>. Walking the
/// exact surd recurrence from <c>(P₀, Q₀) = (b, 2m)</c> and accumulating the convergents <c>hᵢ/kᵢ</c>, the classical
/// identity <c>(Q₀·hᵢ − P₀·kᵢ)² − Δ·kᵢ² = (−1)^(i+1)·Q₀·Q₍ᵢ₊₁₎</c> reads <c>Xᵢ² − Δ·kᵢ² = ±2m·Q₍ᵢ₊₁₎</c>, so the
/// magnitude reaches <c>4m</c> exactly when <c>|Q₍ᵢ₊₁₎| = 2</c>. The states of that recurrence are the ideals equivalent
/// to the start, each of norm <c>|Q|/2</c>, so <c>|Q| = 2</c> is arrival at an ideal of norm one: the test is the
/// principality test, and the convergent that reaches it exhibits the generator.
/// </para>
/// <para>
/// Termination rests on the validations rather than on a step budget. A positive nonsquare <c>Δ</c> congruent to zero or
/// one modulo four makes every expansion of discriminant <c>Δ</c> eventually periodic on the finite set of reduced surds
/// (<c>0 &lt; P &lt; √Δ</c>, <c>0 &lt; Q &lt; 2√Δ</c>), and the step map is invertible there, so the first reduced state
/// reached lies on the cycle and recurs. Cost is linear in that cycle, with operands as large as the answer.
/// </para>
/// <para>
/// The unit case is the norm-one ideal — the order itself — whose <c>b</c> is the largest integer of <c>Δ</c>'s parity
/// not exceeding <c>⌊√Δ⌋</c>. There <c>ω = (b + √Δ)/2</c> is reduced (<c>ω &gt; 1</c>, conjugate strictly between
/// <c>−1</c> and <c>0</c>) and satisfies <c>ω² − b·ω + (b² − Δ)/4 = 0</c>, whose discriminant is <c>Δ</c>, so
/// <c>Z[ω]</c> is exactly that order. A reduced start makes the expansion purely periodic with no preperiod, and the
/// start is the only reduced state whose denominator is two, so the first <c>|Q| = 2</c> is the first period closure:
/// the walk always answers, and the norm sign is the period length's parity.
/// </para>
/// <para>
/// That first closure is the smallest unit above one outright, so no "prefer the norm-minus-one branch at the same
/// <c>Y</c>" step appears here — that preference is subsumed, not omitted. It mattered only to an ascending-<c>Y</c>
/// search, and only at <c>Δ = 5</c>, the single discriminant where both signs solve at one <c>Y</c> (<c>1</c> and
/// <c>9</c> are the only squares eight apart); the walk answers <c>X = 1</c> there with no branch.
/// </para>
/// <para>
/// The walk drives the shared <see cref="QuadraticSurdRecurrence"/>, so the sign-branched floor and the step are the
/// same arithmetic the <c>ContinuedFraction</c> expander runs, and only the policy around them is owned here: the walk
/// carries the convergents, and it detects the cycle with the first reduced state rather than a dictionary of seen
/// states, which costs one state instead of the whole expansion. The start needs no canonicalization either — the
/// discriminant IS the radicand, its floor square root comes from <c>Validate</c>, and <c>b² ≡ Δ (mod 4m)</c> already
/// gives the divisibility a rescale would otherwise establish.
/// </para>
/// </remarks>
internal static class QuadraticNormEquation {
    /// <summary>Returns the fundamental solution of <c>X² − Δ·Y² = ±4</c> — the minimal positive <c>Y</c> admitting either sign.</summary>
    /// <param name="discriminant">The discriminant <c>Δ</c> of a real quadratic order: positive, not a perfect square, and congruent to zero or one modulo four.</param>
    /// <returns>The solution with minimal positive <c>Y</c>, its positive <c>X</c>, and the norm sign that <c>Y</c> realizes — equivalently the fundamental unit <c>ε = (X + Y·√Δ)/2</c> of the order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="discriminant"/> is not positive, is a perfect square, or is congruent to two or three modulo four, so it names no real quadratic order and the equation has no solution.</exception>
    internal static QuadraticNormSolution FundamentalUnit(BigInteger discriminant) {
        var root = Validate(discriminant: discriminant);
        // The reduced root ω = (b + √Δ)/2 generates the order of discriminant Δ exactly when b carries Δ's parity, which
        // also makes Δ − b² divisible by four — the divisibility the recurrence's first step needs.
        var b = ((root.IsEven == discriminant.IsEven)
            ? root
            : (root - BigInteger.One)
        );

        // The walk cannot come back empty here: the start is reduced, so the expansion is purely periodic and its first
        // period closure is the ±4 hit itself.
        return Walk(
            discriminant: discriminant,
            root: root,
            startP: b,
            startQ: new BigInteger(value: 2)
        )!.Value;
    }
    /// <summary>Attempts to solve <c>X² − Δ·Y² = ±4·ℓ</c> for a rational prime <c>ℓ</c> — equivalently, to find an element of norm <c>±ℓ</c> in the order.</summary>
    /// <param name="discriminant">The discriminant <c>Δ</c> of a real quadratic order: positive, not a perfect square, and congruent to zero or one modulo four.</param>
    /// <param name="rationalPrime">The rational prime <c>ℓ</c>. It must be prime: the ideal search takes a square root modulo <c>ℓ</c> by a descent that presumes primality.</param>
    /// <returns>A solution when one exists; otherwise <see langword="null"/>, which certifies that no element of the order has norm <c>±ℓ</c> — the ideals above <c>ℓ</c> are not principal, or <c>ℓ</c> is inert.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="discriminant"/> is not positive, is a perfect square, or is congruent to two or three modulo four; or <paramref name="rationalPrime"/> is neither two nor an odd value of at least three, which <see cref="BigIntegerFunctions.TrySquareRootModuloOddPrime(BigInteger, BigInteger, out BigInteger)"/> refuses.</exception>
    internal static QuadraticNormSolution? SolveForPrimeNorm(BigInteger discriminant, BigInteger rationalPrime) {
        var root = Validate(discriminant: discriminant);
        var coefficient = IdealCoefficient(
            discriminant: discriminant,
            rationalPrime: rationalPrime
        );

        // No coefficient means the order holds no ideal of norm ℓ at all — ℓ is inert — so no element carries that norm.
        if (coefficient is null) { return null; }

        // A split ℓ carries two ideals, conjugate to each other and generated by conjugate elements when generated at
        // all, so walking one settles both.
        return Walk(
            discriminant: discriminant,
            root: root,
            startP: coefficient.Value,
            startQ: (2 * rationalPrime)
        );
    }

    /// <summary>Returns the coefficient <c>b</c> of an ideal <c>[ℓ, (b + √Δ)/2]</c> of norm <c>ℓ</c>, or <see langword="null"/> when the order holds none.</summary>
    /// <remarks>
    /// The ideals of norm <c>ℓ</c> are indexed by the roots of <c>b² ≡ Δ (mod 4ℓ)</c>, which the coprime pair of
    /// congruences modulo four and modulo <c>ℓ</c> resolves: modulo four the condition is exactly that <c>b</c> carries
    /// <c>Δ</c>'s parity, since a discriminant is congruent to zero or one there.
    /// </remarks>
    private static BigInteger? IdealCoefficient(BigInteger discriminant, BigInteger rationalPrime) {
        if (rationalPrime == 2) {
            // Four candidates, so b² ≡ Δ (mod 8) is settled by inspection rather than by a descent that wants odd input.
            for (var candidate = BigInteger.Zero; (candidate < 4); ++candidate) {
                if ((((candidate * candidate) - discriminant) % 8).IsZero) { return candidate; }
            }

            return null;
        }

        if (!BigIntegerFunctions.TrySquareRootModuloOddPrime(
            oddPrime: rationalPrime,
            root: out var residue,
            value: discriminant
        )) { return null; }

        return ((residue.IsEven == discriminant.IsEven)
            ? residue
            : (residue + rationalPrime)
        );
    }
    /// <summary>Reports whether a surd state is reduced, the property that makes the expansion purely periodic from there on.</summary>
    /// <remarks>
    /// The state <c>(P + √Δ)/Q</c> is reduced when it exceeds one and its conjugate lies strictly between <c>−1</c> and
    /// <c>0</c>, which for integers is <c>0 &lt; P &lt; √Δ</c> and <c>√Δ − P &lt; Q &lt; √Δ + P</c>. A nonsquare
    /// <c>Δ</c> makes <c>√Δ</c> irrational, so each strict comparison against it collapses to one against <c>⌊√Δ⌋</c>.
    /// </remarks>
    private static bool IsReduced(BigInteger root, BigInteger stateP, BigInteger stateQ) =>
        ((0 < stateP.Sign) && (stateP <= root) && (0 < stateQ.Sign) && (root < (stateP + stateQ)) && ((stateQ - stateP) <= root));
    /// <summary>Rejects every discriminant the walk cannot terminate on, returning the exact floor square root it needs.</summary>
    private static BigInteger Validate(BigInteger discriminant) {
        if (discriminant.Sign <= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(discriminant),
                message: "The norm equation is solved here only for a real order with positive discriminant."
            );
        }

        var residue = discriminant & 3;

        if (!(residue.IsZero || residue.IsOne)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(discriminant),
                message: "A quadratic-order discriminant is congruent to zero or one modulo four."
            );
        }

        var root = BigIntegerFunctions.SquareRoot(value: discriminant);

        if ((root * root) == discriminant) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(discriminant),
                message: "The discriminant must be a nonsquare; a square discriminant names a split or degenerate ring with no fundamental unit, and its rational square root leaves the walk with no periodic cycle to close on."
            );
        }

        return root;
    }
    /// <summary>Walks an ideal's continued fraction, returning the first convergent whose norm magnitude is <c>2·|Q₀|</c>, or <see langword="null"/> when the cycle closes without one.</summary>
    private static QuadraticNormSolution? Walk(BigInteger discriminant, BigInteger root, BigInteger startP, BigInteger startQ) {
        var previousPreviousDenominator = BigInteger.One;
        var previousPreviousNumerator = BigInteger.Zero;
        var previousDenominator = BigInteger.Zero;
        var previousNumerator = BigInteger.One;
        var recurrence = QuadraticSurdRecurrence.FromDividingState(
            radicandProduct: discriminant,
            radicandRoot: root,
            stateP: startP,
            stateQ: startQ
        );
        var cycleP = BigInteger.Zero;
        var cycleQ = BigInteger.Zero;
        var anchored = false;

        while (true) {
            // The first reduced state lies on the cycle, so its return closes the expansion: every state the walk will
            // ever hold has been held by then, preperiod included.
            if (anchored) {
                if (
                    (recurrence.StateP == cycleP) &&
                    (recurrence.StateQ == cycleQ)
                ) { return null; }
            } else if (IsReduced(
                root: root,
                stateP: recurrence.StateP,
                stateQ: recurrence.StateQ
            )) {
                anchored = true;
                cycleP = recurrence.StateP;
                cycleQ = recurrence.StateQ;
            }

            var quotient = recurrence.Step();
            var convergentDenominator = ((quotient * previousDenominator) + previousPreviousDenominator);
            var convergentNumerator = ((quotient * previousNumerator) + previousPreviousNumerator);

            // The step left the recurrence on Q₍ᵢ₊₁₎, and an ideal of norm one is where the convergent identity puts
            // the norm magnitude at exactly 2·|Q₀|. Testing that denominator rather than the value keeps the operands
            // at surd size until the answer is actually here.
            if (BigInteger.Abs(value: recurrence.StateQ) == 2) {
                var x = ((startQ * convergentNumerator) - (startP * convergentDenominator));
                var value = ((x * x) - ((discriminant * convergentDenominator) * convergentDenominator));

                // Either sign of a coordinate solves, so the pair is reported non-negative.
                return new QuadraticNormSolution(
                    X: BigInteger.Abs(value: x),
                    Y: BigInteger.Abs(value: convergentDenominator),
                    NormSign: value.Sign
                );
            }

            previousPreviousDenominator = previousDenominator;
            previousPreviousNumerator = previousNumerator;
            previousDenominator = convergentDenominator;
            previousNumerator = convergentNumerator;
        }
    }
}
