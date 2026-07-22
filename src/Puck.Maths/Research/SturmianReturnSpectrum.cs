using System.Numerics;

namespace Puck.Maths.Research;

/// <summary>One adjacent-convergent congruence state at a directive phase.</summary>
public readonly record struct SturmianCongruencePhase(
    int PPrevious,
    int PCurrent,
    int QPrevious,
    int QCurrent,
    int NextPhase
);

/// <summary>An admissible semiconvergent return vector <c>(m,ℓ,k)</c>.</summary>
public readonly record struct SturmianReturnVector(int M, int Ell, int K);

/// <summary>The exact colored and uncolored return maxima at one eventual phase.</summary>
public readonly record struct SturmianReturnPhaseValue(
    SturmianCongruencePhase Matrix,
    QuadraticSurd Delta,
    QuadraticSurd X,
    QuadraticSurd Colored,
    QuadraticSurd Uncolored,
    SturmianReturnVector ColoredWitness,
    SturmianReturnVector UncoloredWitness
);

/// <summary>An exact eventual return spectrum for one congruence component.</summary>
public sealed record SturmianReturnSpectrum(
    IReadOnlyList<SturmianReturnPhaseValue> Phases,
    QuadraticSurd ColoredLimsup,
    QuadraticSurd UncoloredLimsup
);

/// <summary>The least spectrum among every finite-preperiod congruence component.</summary>
public sealed record SturmianComponentMinimum(
    int CycleCount,
    SturmianReturnSpectrum Minimum,
    SturmianCongruencePhase Representative,
    IReadOnlyDictionary<QuadraticSurd, int> Histogram
);

/// <summary>A Fibonacci component minimum computed by the optimized phase-independent path.</summary>
public sealed record FibonacciFastComponentMinimum(
    int CycleCount,
    QuadraticSurd Minimum,
    IReadOnlyDictionary<QuadraticSurd, int> Histogram
);

/// <summary>
/// Exact return-spectrum analysis for eventually periodic Sturmian directives under two
/// constant-gap coloring periods.
/// </summary>
/// <remarks>
/// The implementation evaluates Proposition 20's finite phase maximum in exact quadratic-surd
/// arithmetic. <see cref="ComponentMinimum"/> enumerates every determinant-compatible congruence
/// component, so its result includes all possible finite continued-fraction preperiods rather than
/// only the component reached from a chosen prefix.
/// </remarks>
public static class SturmianReturnSpectrumResearch {
    /// <summary>Evaluates the component selected by one finite prefix and periodic directive tail.</summary>
    public static SturmianReturnSpectrum Evaluate(
        IReadOnlyList<int> prefix,
        IReadOnlyList<int> period,
        int leftColoringPeriod,
        int rightColoringPeriod
    ) {
        ValidateDirective(prefix, period);
        ValidateColoringPeriods(leftColoringPeriod, rightColoringPeriod);
        var matrices = EventualMatrixCycle(
            prefix,
            period,
            leftColoringPeriod,
            rightColoringPeriod
        );
        return EvaluateMatrices(matrices, period, leftColoringPeriod, rightColoringPeriod);
    }

    /// <summary>
    /// Finds the least colored limsup over every determinant-compatible finite-preperiod component.
    /// </summary>
    public static SturmianComponentMinimum ComponentMinimum(
        IReadOnlyList<int> period,
        int leftColoringPeriod,
        int rightColoringPeriod
    ) {
        ValidateDirective([], period);
        ValidateColoringPeriods(leftColoringPeriod, rightColoringPeriod);

        var common = GreatestCommonDivisor(leftColoringPeriod, rightColoringPeriod);
        var visited = new HashSet<CongruenceState>();
        SturmianReturnSpectrum? minimum = null;
        var representative = default(SturmianCongruencePhase);
        var cycleCount = 0;
        var histogram = new Dictionary<QuadraticSurd, int>();
        var phaseData = BuildPhaseData(
            period,
            checked(leftColoringPeriod * rightColoringPeriod)
        );

        for (var phase = 0; phase < period.Count; ++phase) {
            for (var a = 0; a < leftColoringPeriod; ++a) {
                for (var b = 0; b < leftColoringPeriod; ++b) {
                    if ((leftColoringPeriod > 1) &&
                        (GreatestCommonDivisor(
                            GreatestCommonDivisor(a, b),
                            leftColoringPeriod
                        ) != 1)) {
                        continue;
                    }
                    for (var c = 0; c < rightColoringPeriod; ++c) {
                        for (var d = 0; d < rightColoringPeriod; ++d) {
                            if ((rightColoringPeriod > 1) &&
                                (GreatestCommonDivisor(
                                    GreatestCommonDivisor(c, d),
                                    rightColoringPeriod
                                ) != 1)) {
                                continue;
                            }

                            var determinant = PositiveRemainder(
                                ((BigInteger)a * d) - ((BigInteger)b * c),
                                common
                            );
                            if ((common > 1) && (determinant != 1) &&
                                (determinant != (common - 1))) {
                                continue;
                            }

                            var start = new CongruenceState(a, b, c, d, phase);
                            if (visited.Contains(start)) { continue; }

                            var cycle = new List<SturmianCongruencePhase>();
                            var state = start;
                            do {
                                visited.Add(state);
                                cycle.Add(state.ToPublic());
                                state = Advance(state, period, leftColoringPeriod, rightColoringPeriod);
                            } while (state != start);

                            ++cycleCount;
                            var spectrum = EvaluateMatrices(
                                cycle,
                                leftColoringPeriod,
                                rightColoringPeriod,
                                phaseData
                            );
                            histogram.TryGetValue(spectrum.ColoredLimsup, out var multiplicity);
                            histogram[spectrum.ColoredLimsup] = multiplicity + 1;
                            if ((minimum is null) ||
                                (spectrum.ColoredLimsup < minimum.ColoredLimsup)) {
                                minimum = spectrum;
                                representative = cycle[0];
                            }
                        }
                    }
                }
            }
        }

        return new SturmianComponentMinimum(
            cycleCount,
            minimum ?? throw new InvalidOperationException(
                "no determinant-compatible congruence component exists"
            ),
            representative,
            histogram
        );
    }

    /// <summary>
    /// Finds an explicit positive continued-fraction prefix whose eventual tail enters the
    /// requested congruence component.
    /// </summary>
    /// <remarks>
    /// Breadth-first search is exact in the finite matrix-residue state space. Digits through the
    /// least common multiple of the coloring periods represent every positive digit residue.
    /// </remarks>
    public static bool TryFindPrefix(
        IReadOnlyList<int> period,
        int leftColoringPeriod,
        int rightColoringPeriod,
        SturmianCongruencePhase componentPhase,
        out IReadOnlyList<int>? prefix
    ) {
        ValidateDirective([], period);
        ValidateColoringPeriods(leftColoringPeriod, rightColoringPeriod);
        if ((componentPhase.PPrevious < 0) ||
            (componentPhase.PPrevious >= leftColoringPeriod) ||
            (componentPhase.PCurrent < 0) ||
            (componentPhase.PCurrent >= leftColoringPeriod) ||
            (componentPhase.QPrevious < 0) ||
            (componentPhase.QPrevious >= rightColoringPeriod) ||
            (componentPhase.QCurrent < 0) ||
            (componentPhase.QCurrent >= rightColoringPeriod) ||
            (componentPhase.NextPhase < 0) ||
            (componentPhase.NextPhase >= period.Count)) {
            throw new ArgumentOutOfRangeException(nameof(componentPhase));
        }

        var firstTailPhase = (1 % period.Count);
        var aligned = componentPhase;
        for (var step = 0; aligned.NextPhase != firstTailPhase; ++step) {
            if (step >= period.Count) {
                prefix = null;
                return false;
            }
            aligned = Advance(
                new CongruenceState(
                    aligned.PPrevious,
                    aligned.PCurrent,
                    aligned.QPrevious,
                    aligned.QCurrent,
                    aligned.NextPhase
                ),
                period,
                leftColoringPeriod,
                rightColoringPeriod
            ).ToPublic();
        }

        var firstTailDigit = period[0];
        var target = new PrefixState(
            PositiveRemainder(
                (BigInteger)aligned.PCurrent -
                    ((BigInteger)firstTailDigit * aligned.PPrevious),
                leftColoringPeriod
            ),
            aligned.PPrevious,
            PositiveRemainder(
                (BigInteger)aligned.QCurrent -
                    ((BigInteger)firstTailDigit * aligned.QPrevious),
                rightColoringPeriod
            ),
            aligned.QPrevious
        );
        var initial = new PrefixState(
            Mod(1, leftColoringPeriod),
            0,
            0,
            Mod(1, rightColoringPeriod)
        );
        var queue = new Queue<PrefixState>();
        var parent = new Dictionary<PrefixState, PrefixParent>();
        queue.Enqueue(initial);
        parent.Add(initial, default);
        var maximumDigit = LeastCommonMultiple(leftColoringPeriod, rightColoringPeriod);

        while (queue.Count != 0) {
            var state = queue.Dequeue();
            if (state == target) {
                var digits = new List<int>();
                while (state != initial) {
                    var edge = parent[state];
                    digits.Add(edge.Digit);
                    state = edge.Previous;
                }
                digits.Reverse();
                var result = (IReadOnlyList<int>)digits.AsReadOnly();
                var check = Evaluate(
                    result,
                    period,
                    leftColoringPeriod,
                    rightColoringPeriod
                );
                if (!check.Phases.Any(item => item.Matrix == aligned)) {
                    throw new InvalidOperationException(
                        "the reconstructed finite prefix missed its congruence component"
                    );
                }
                prefix = result;
                return true;
            }

            for (var digit = 1; digit <= maximumDigit; ++digit) {
                var next = new PrefixState(
                    state.PNm1,
                    AddMultiplyMod(state.PNm2, digit, state.PNm1, leftColoringPeriod),
                    state.QNm1,
                    AddMultiplyMod(state.QNm2, digit, state.QNm1, rightColoringPeriod)
                );
                if (parent.TryAdd(next, new PrefixParent(state, digit))) {
                    queue.Enqueue(next);
                }
            }
        }

        prefix = null;
        return false;
    }

    /// <summary>Specialized name retained for Fibonacci component studies.</summary>
    public static SturmianComponentMinimum FibonacciComponentMinimum(
        int leftColoringPeriod,
        int rightColoringPeriod
    ) => ComponentMinimum([1], leftColoringPeriod, rightColoringPeriod);

    /// <summary>
    /// Optimized all-component minimum for the phase-independent Fibonacci directive.
    /// </summary>
    public static FibonacciFastComponentMinimum FibonacciComponentMinimumFast(
        int leftColoringPeriod,
        int rightColoringPeriod
    ) {
        ValidateColoringPeriods(leftColoringPeriod, rightColoringPeriod);
        var determinant = checked(leftColoringPeriod * rightColoringPeriod);
        var tau = FibonacciResearch.GoldenRatio;
        var tauSquared = (tau * tau);
        var candidates = new List<(SturmianReturnVector Vector, QuadraticSurd Value)>();
        var maximumTotal = WeightedReturnSearchBound(
            determinant,
            QuadraticSurd.One,
            tau
        );
        for (var ell = 0; ell <= maximumTotal; ++ell) {
            for (var k = 0; k <= (maximumTotal - ell); ++k) {
                if ((ell + k) == 0) { continue; }
                var stripError = ((QuadraticSurd.Rational(ell) * tau) -
                    QuadraticSurd.Rational(k)).Abs();
                if (!(stripError < tauSquared)) { continue; }
                candidates.Add((
                    new SturmianReturnVector(0, ell, k),
                    tauSquared / (QuadraticSurd.Rational(ell) +
                        (QuadraticSurd.Rational(k) * tau))
                ));
            }
        }
        candidates.Sort((left, right) => right.Value.CompareTo(left.Value));

        var common = GreatestCommonDivisor(leftColoringPeriod, rightColoringPeriod);
        var visited = new HashSet<CongruenceState>();
        QuadraticSurd? minimum = null;
        var cycleCount = 0;
        var histogram = new Dictionary<QuadraticSurd, int>();

        for (var a = 0; a < leftColoringPeriod; ++a) {
            for (var b = 0; b < leftColoringPeriod; ++b) {
                for (var c = 0; c < rightColoringPeriod; ++c) {
                    for (var d = 0; d < rightColoringPeriod; ++d) {
                        if ((leftColoringPeriod > 1) &&
                            (GreatestCommonDivisor(
                                GreatestCommonDivisor(a, b), leftColoringPeriod
                            ) != 1)) {
                            continue;
                        }
                        if ((rightColoringPeriod > 1) &&
                            (GreatestCommonDivisor(
                                GreatestCommonDivisor(c, d), rightColoringPeriod
                            ) != 1)) {
                            continue;
                        }

                        var matrixDeterminant = PositiveRemainder(
                            ((BigInteger)a * d) - ((BigInteger)b * c),
                            common
                        );
                        if ((common > 1) && (matrixDeterminant != 1) &&
                            (matrixDeterminant != (common - 1))) {
                            continue;
                        }

                        var start = new CongruenceState(a, b, c, d, 0);
                        if (visited.Contains(start)) { continue; }

                        QuadraticSurd? cycleMaximum = null;
                        var state = start;
                        do {
                            visited.Add(state);
                            QuadraticSurd? phaseValue = null;
                            foreach (var candidate in candidates) {
                                var ell = candidate.Vector.Ell;
                                var k = candidate.Vector.K;
                                var first = (((BigInteger)state.PPrevious * ell) +
                                    ((BigInteger)state.PCurrent * k)) % leftColoringPeriod;
                                if (!first.IsZero) { continue; }
                                var second = (((BigInteger)state.QPrevious * ell) +
                                    ((BigInteger)state.QCurrent * k)) % rightColoringPeriod;
                                if (!second.IsZero) { continue; }
                                phaseValue = candidate.Value;
                                break;
                            }
                            if (phaseValue is null) {
                                throw new InvalidOperationException(
                                    "no admissible Fibonacci return vector was found"
                                );
                            }
                            if ((cycleMaximum is null) ||
                                (phaseValue > cycleMaximum.Value)) {
                                cycleMaximum = phaseValue;
                            }
                            state = Advance(state, [1], leftColoringPeriod, rightColoringPeriod);
                        } while (state != start);

                        ++cycleCount;
                        var value = cycleMaximum ?? throw new InvalidOperationException(
                            "empty Fibonacci congruence component"
                        );
                        histogram.TryGetValue(value, out var multiplicity);
                        histogram[value] = multiplicity + 1;
                        if ((minimum is null) || (value < minimum.Value)) { minimum = value; }
                    }
                }
            }
        }

        return new FibonacciFastComponentMinimum(
            cycleCount,
            minimum ?? throw new InvalidOperationException(
                "no determinant-compatible congruence component exists"
            ),
            histogram
        );
    }

    /// <summary>Checks the two period congruences for one proposed return vector.</summary>
    public static bool CongruenceHolds(
        SturmianCongruencePhase matrix,
        SturmianReturnVector vector,
        int leftColoringPeriod,
        int rightColoringPeriod
    ) {
        ValidateColoringPeriods(leftColoringPeriod, rightColoringPeriod);
        return CongruenceHoldsUnchecked(
            matrix,
            vector,
            leftColoringPeriod,
            rightColoringPeriod
        );
    }

    private static bool CongruenceHoldsUnchecked(
        SturmianCongruencePhase matrix,
        SturmianReturnVector vector,
        int leftColoringPeriod,
        int rightColoringPeriod
    ) {
        var first = (((BigInteger)matrix.PPrevious +
                ((BigInteger)vector.M * matrix.PCurrent)) * vector.Ell) +
            ((BigInteger)matrix.PCurrent * vector.K);
        var second = (((BigInteger)matrix.QPrevious +
                ((BigInteger)vector.M * matrix.QCurrent)) * vector.Ell) +
            ((BigInteger)matrix.QCurrent * vector.K);
        return (first % leftColoringPeriod).IsZero &&
            (second % rightColoringPeriod).IsZero;
    }

    /// <summary>Returns the exact positive periodic continued-fraction tail at a phase.</summary>
    public static QuadraticSurd PeriodicTail(
        IReadOnlyList<int> period,
        int start,
        int step = 1
    ) {
        ValidateDirective([], period);
        if ((step != 1) && (step != -1)) {
            throw new ArgumentOutOfRangeException(nameof(step));
        }
        if ((start < 0) || (start >= period.Count)) {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        var product = IntegerMatrix2.Identity;
        var phase = start;
        for (var index = 0; index < period.Count; ++index) {
            product *= IntegerMatrix2.Digit(period[phase]);
            phase = Mod(phase + step, period.Count);
        }

        var discriminant = ((product.D - product.A) * (product.D - product.A)) +
            (4 * product.B * product.C);
        if ((product.C <= 0) || (discriminant <= 0)) {
            throw new InvalidOperationException(
                "period matrix did not define a positive quadratic tail"
            );
        }
        return QuadraticSurd.Create(
            product.A - product.D,
            1,
            discriminant,
            2 * product.C
        );
    }

    private static SturmianReturnSpectrum EvaluateMatrices(
        IReadOnlyList<SturmianCongruencePhase> matrices,
        IReadOnlyList<int> period,
        int leftColoringPeriod,
        int rightColoringPeriod
    ) => EvaluateMatrices(
        matrices,
        leftColoringPeriod,
        rightColoringPeriod,
        BuildPhaseData(period, checked(leftColoringPeriod * rightColoringPeriod))
    );

    private static SturmianReturnSpectrum EvaluateMatrices(
        IReadOnlyList<SturmianCongruencePhase> matrices,
        int leftColoringPeriod,
        int rightColoringPeriod,
        IReadOnlyList<PhaseData> phaseData
    ) {
        if (matrices.Count == 0) {
            throw new ArgumentException("a congruence component cannot be empty", nameof(matrices));
        }
        var phases = new List<SturmianReturnPhaseValue>(matrices.Count);
        foreach (var matrix in matrices) {
            var data = phaseData[matrix.NextPhase];
            var colored = data.ColoredCandidates.First(candidate =>
                CongruenceHoldsUnchecked(
                    matrix,
                    candidate.Vector,
                    leftColoringPeriod,
                    rightColoringPeriod
                ));
            phases.Add(new SturmianReturnPhaseValue(
                matrix,
                data.Delta,
                data.X,
                colored.Value,
                data.Uncolored.Value,
                colored.Vector,
                data.Uncolored.Vector
            ));
        }
        return new SturmianReturnSpectrum(
            phases,
            phases.Max(phase => phase.Colored),
            phases.Max(phase => phase.Uncolored)
        );
    }

    private static IReadOnlyList<SturmianCongruencePhase> EventualMatrixCycle(
        IReadOnlyList<int> prefix,
        IReadOnlyList<int> period,
        int leftColoringPeriod,
        int rightColoringPeriod
    ) {
        var pNm2 = Mod(1, leftColoringPeriod);
        var pNm1 = 0;
        var qNm2 = 0;
        var qNm1 = Mod(1, rightColoringPeriod);
        var seen = new Dictionary<CongruenceState, int>();
        var phases = new List<SturmianCongruencePhase>();

        for (var n = 1; ; ++n) {
            var digit = (n <= prefix.Count)
                ? prefix[n - 1]
                : period[Mod(n - prefix.Count - 1, period.Count)];
            var pN = AddMultiplyMod(pNm2, digit, pNm1, leftColoringPeriod);
            var qN = AddMultiplyMod(qNm2, digit, qNm1, rightColoringPeriod);
            if (n > prefix.Count) {
                var nextPhase = Mod(n - prefix.Count, period.Count);
                var key = new CongruenceState(pNm1, pN, qNm1, qN, nextPhase);
                if (seen.TryGetValue(key, out var cycleStart)) {
                    return phases.GetRange(cycleStart, phases.Count - cycleStart);
                }
                seen.Add(key, phases.Count);
                phases.Add(key.ToPublic());
            }
            pNm2 = pNm1;
            pNm1 = pN;
            qNm2 = qNm1;
            qNm1 = qN;
            if (phases.Count > 10_000_000) {
                throw new InvalidOperationException(
                    "congruence-state cycle exceeded the safety limit"
                );
            }
        }
    }

    private static IReadOnlyList<PhaseData> BuildPhaseData(
        IReadOnlyList<int> period,
        int determinant
    ) {
        var result = new PhaseData[period.Count];
        for (var nextPhase = 0; nextPhase < period.Count; ++nextPhase) {
            var delta = PeriodicTail(period, nextPhase);
            var currentPhase = Mod(nextPhase - 1, period.Count);
            var reversed = PeriodicTail(period, currentPhase, step: -1);
            var x = (QuadraticSurd.One / reversed);
            var colored = BuildCandidates(delta, x, period[nextPhase], determinant);
            var uncolored = BuildCandidates(delta, x, period[nextPhase], 1)[0];
            result[nextPhase] = new PhaseData(delta, x, colored, uncolored);
        }
        return result;
    }

    private static IReadOnlyList<ReturnCandidate> BuildCandidates(
        QuadraticSurd delta,
        QuadraticSurd x,
        int nextDigit,
        int determinant
    ) {
        var candidates = new List<ReturnCandidate>();
        for (var m = 0; m < nextDigit; ++m) {
            var numerator = QuadraticSurd.Rational(1 + m) + x;
            var tail = delta - QuadraticSurd.Rational(m);
            var firstWeight = QuadraticSurd.Rational(m) + x;
            var maximumTotal = WeightedReturnSearchBound(
                determinant,
                firstWeight,
                QuadraticSurd.One
            );
            for (var ell = 0; ell <= maximumTotal; ++ell) {
                for (var k = 0; k <= (maximumTotal - ell); ++k) {
                    if ((ell + k) == 0) { continue; }
                    var vector = new SturmianReturnVector(m, ell, k);
                    var stripError = ((QuadraticSurd.Rational(ell) * tail) -
                        QuadraticSurd.Rational(k)).Abs();
                    if (!(stripError < (tail + QuadraticSurd.One))) { continue; }
                    var denominator = QuadraticSurd.Rational(k + (ell * m)) +
                        (QuadraticSurd.Rational(ell) * x);
                    candidates.Add(new ReturnCandidate(
                        vector,
                        numerator / denominator
                    ));
                }
            }
        }
        candidates.Sort((left, right) => right.Value.CompareTo(left.Value));
        if (candidates.Count == 0) {
            throw new InvalidOperationException("no admissible return vector was found");
        }
        return candidates;
    }

    private static int WeightedReturnSearchBound(
        int determinant,
        QuadraticSurd firstWeight,
        QuadraticSurd secondWeight
    ) {
        // Among D+1 consecutive factor boundaries, two have the same value in the
        // order-D coloring group. Their intervening factor has at most D letters and
        // weight at most D*maxWeight. Any lighter return therefore has fewer than
        // D*maxWeight/minWeight letters, giving a rigorous finite search cutoff.
        var minimum = firstWeight < secondWeight ? firstWeight : secondWeight;
        var maximum = firstWeight > secondWeight ? firstWeight : secondWeight;
        if (minimum.Sign <= 0) {
            throw new InvalidOperationException("return weights must be positive");
        }
        var bound = (QuadraticSurd.Rational(determinant) * maximum / minimum).Ceiling();
        if (bound > int.MaxValue) {
            throw new InvalidOperationException("weighted return search bound exceeds Int32");
        }
        return (int)bound;
    }

    private static CongruenceState Advance(
        CongruenceState state,
        IReadOnlyList<int> period,
        int leftColoringPeriod,
        int rightColoringPeriod
    ) {
        var digit = period[state.NextPhase];
        return new CongruenceState(
            state.PCurrent,
            AddMultiplyMod(state.PPrevious, digit, state.PCurrent, leftColoringPeriod),
            state.QCurrent,
            AddMultiplyMod(state.QPrevious, digit, state.QCurrent, rightColoringPeriod),
            Mod(state.NextPhase + 1, period.Count)
        );
    }

    private static void ValidateDirective(
        IReadOnlyList<int> prefix,
        IReadOnlyList<int> period
    ) {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(period);
        if (prefix.Any(value => value <= 0)) {
            throw new ArgumentOutOfRangeException(nameof(prefix));
        }
        if ((period.Count == 0) || period.Any(value => value <= 0)) {
            throw new ArgumentOutOfRangeException(nameof(period));
        }
    }

    private static void ValidateColoringPeriods(int left, int right) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(left);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(right);
        _ = checked(left * right);
    }

    private static int AddMultiplyMod(int previous, int digit, int current, int modulus) =>
        (modulus == 1)
            ? 0
            : (int)((previous + ((BigInteger)digit * current)) % modulus);

    private static int Mod(int value, int modulus) {
        var result = (value % modulus);
        return (result < 0) ? (result + modulus) : result;
    }

    private static int PositiveRemainder(BigInteger value, int modulus) {
        if (modulus == 1) { return 0; }
        var result = (int)(value % modulus);
        return (result < 0) ? (result + modulus) : result;
    }

    private static int GreatestCommonDivisor(int left, int right) {
        while (right != 0) { (left, right) = (right, left % right); }
        return Math.Abs(left);
    }

    private static int LeastCommonMultiple(int left, int right) =>
        checked((left / GreatestCommonDivisor(left, right)) * right);

    private readonly record struct CongruenceState(
        int PPrevious,
        int PCurrent,
        int QPrevious,
        int QCurrent,
        int NextPhase
    ) {
        public SturmianCongruencePhase ToPublic() =>
            new(PPrevious, PCurrent, QPrevious, QCurrent, NextPhase);
    }

    private readonly record struct PrefixState(int PNm2, int PNm1, int QNm2, int QNm1);
    private readonly record struct PrefixParent(PrefixState Previous, int Digit);

    private sealed record PhaseData(
        QuadraticSurd Delta,
        QuadraticSurd X,
        IReadOnlyList<ReturnCandidate> ColoredCandidates,
        ReturnCandidate Uncolored
    );

    private readonly record struct ReturnCandidate(
        SturmianReturnVector Vector,
        QuadraticSurd Value
    );

    private readonly record struct IntegerMatrix2(
        BigInteger A,
        BigInteger B,
        BigInteger C,
        BigInteger D
    ) {
        public static IntegerMatrix2 Identity => new(1, 0, 0, 1);
        public static IntegerMatrix2 Digit(int value) => new(value, 1, 1, 0);
        public static IntegerMatrix2 operator *(
            IntegerMatrix2 left,
            IntegerMatrix2 right
        ) => new(
            (left.A * right.A) + (left.B * right.C),
            (left.A * right.B) + (left.B * right.D),
            (left.C * right.A) + (left.D * right.C),
            (left.C * right.B) + (left.D * right.D)
        );
    }
}
