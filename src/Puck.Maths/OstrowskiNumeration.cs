using System.Numerics;

namespace Puck.Maths;

/// <summary>The exact eventually-periodic Ostrowski numeration system of a positive quadratic irrational.</summary>
public sealed class QuadraticOstrowskiSystem {
    private QuadraticOstrowskiSystem(
        QuadraticSurd basis,
        BigInteger[] continuedFractionPrefix,
        BigInteger[] continuedFractionPeriod) {
        Basis = basis;
        ContinuedFractionPrefix = continuedFractionPrefix;
        ContinuedFractionPeriod = continuedFractionPeriod;
    }

    /// <summary>Gets the quadratic irrational defining the convergent-denominator basis.</summary>
    public QuadraticSurd Basis { get; }
    /// <summary>Gets the non-repeating continued-fraction prefix, including the integral partial quotient.</summary>
    public IReadOnlyList<BigInteger> ContinuedFractionPrefix { get; }
    /// <summary>Gets one nonempty repeating block of partial quotients.</summary>
    public IReadOnlyList<BigInteger> ContinuedFractionPeriod { get; }
    /// <summary>Gets the index of the first periodic partial quotient.</summary>
    public int PeriodStart => ContinuedFractionPrefix.Count;
    /// <summary>Gets the continued-fraction period length.</summary>
    public int PeriodLength => ContinuedFractionPeriod.Count;

    /// <summary>Constructs the exact system of a positive irrational quadratic surd.</summary>
    public static QuadraticOstrowskiSystem Create(QuadraticSurd basis) {
        if ((basis.Sign <= 0) || basis.IsRational || (basis.SurdNumerator <= 0)) {
            throw new ArgumentOutOfRangeException(nameof(basis), "the Ostrowski basis must be a positive quadratic irrational");
        }

        var stateP = basis.RationalNumerator;
        var stateN = (basis.SurdNumerator * basis.SurdNumerator * basis.Radicand);
        var stateQ = basis.Denominator;
        if (((stateN - (stateP * stateP)) % stateQ) != 0) {
            var magnitude = BigInteger.Abs(stateQ);
            stateP *= magnitude;
            stateN *= (stateQ * stateQ);
            stateQ *= magnitude;
        }

        var root = BigIntegerMath.SquareRoot(stateN);
        var seen = new Dictionary<(BigInteger P, BigInteger Q), int>();
        var terms = new List<BigInteger>();

        while (true) {
            if (seen.TryGetValue((stateP, stateQ), out var periodStart)) {
                return new QuadraticOstrowskiSystem(
                    basis,
                    terms.Take(periodStart).ToArray(),
                    terms.Skip(periodStart).ToArray()
                );
            }

            seen[(stateP, stateQ)] = terms.Count;
            var quotient = (stateQ.Sign > 0)
                ? BigIntegerMath.FloorDivide(stateP + root, stateQ)
                : BigIntegerMath.FloorDivide(stateP + root + 1, stateQ);
            terms.Add(quotient);
            var nextP = ((quotient * stateQ) - stateP);
            stateQ = ((stateN - (nextP * nextP)) / stateQ);
            stateP = nextP;
        }
    }

    /// <summary>Returns partial quotient <c>a_index</c>, extending the periodic block as needed.</summary>
    public BigInteger PartialQuotient(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index < ContinuedFractionPrefix.Count) { return ContinuedFractionPrefix[index]; }
        return ContinuedFractionPeriod[(index - ContinuedFractionPrefix.Count) % ContinuedFractionPeriod.Count];
    }

    /// <summary>Returns the canonical most-significant-digit-first Ostrowski representation of a non-negative integer.</summary>
    public IReadOnlyList<BigInteger> Represent(BigInteger value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value.IsZero) { return [BigInteger.Zero]; }

        var denominators = DenominatorsThrough(value);
        var digitsLeastFirst = new BigInteger[denominators.Count];
        var remainder = value;

        for (var index = denominators.Count - 1; index >= 0; --index) {
            digitsLeastFirst[index] = (remainder / denominators[index]);
            remainder -= (digitsLeastFirst[index] * denominators[index]);
        }
        if (!remainder.IsZero) {
            throw new InvalidOperationException("the greedy Ostrowski representation left a remainder");
        }

        var result = digitsLeastFirst.Reverse().ToArray();
        if (!IsCanonical(result)) {
            throw new InvalidOperationException("the greedy Ostrowski representation violated the digit constraints");
        }

        return result;
    }

    /// <summary>Evaluates a most-significant-digit-first representation exactly.</summary>
    public BigInteger Evaluate(IReadOnlyList<BigInteger> digits) {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Count == 0) { throw new ArgumentException("an Ostrowski representation cannot be empty", nameof(digits)); }

        var denominators = Denominators(digits.Count);
        var value = BigInteger.Zero;
        for (var mostIndex = 0; mostIndex < digits.Count; ++mostIndex) {
            var leastIndex = (digits.Count - 1 - mostIndex);
            value += (digits[mostIndex] * denominators[leastIndex]);
        }
        return value;
    }

    /// <summary>Checks the canonical Ostrowski digit constraints.</summary>
    public bool IsCanonical(IReadOnlyList<BigInteger> digits) {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Count == 0) { return false; }
        if ((digits.Count > 1) && digits[0].IsZero) { return false; }

        var leastFirst = digits.Reverse().ToArray();
        if ((leastFirst[0] < 0) || (leastFirst[0] >= PartialQuotient(1))) { return false; }

        for (var index = 1; index < leastFirst.Length; ++index) {
            var maximum = PartialQuotient(index + 1);
            if ((leastFirst[index] < 0) || (leastFirst[index] > maximum)) { return false; }
            if ((leastFirst[index] == maximum) && !leastFirst[index - 1].IsZero) { return false; }
        }

        return true;
    }

    internal (BigInteger A, BigInteger B, BigInteger C, BigInteger D) DenominatorShiftMatrix(
        int firstDenominatorIndex,
        int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(firstDenominatorIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var a = BigInteger.One;
        var b = BigInteger.Zero;
        var c = BigInteger.Zero;
        var d = BigInteger.One;

        for (var offset = 1; offset <= length; ++offset) {
            var quotient = PartialQuotient(firstDenominatorIndex + offset);
            (a, b, c, d) = (
                ((quotient * a) + c),
                ((quotient * b) + d),
                a,
                b
            );
        }

        return (a, b, c, d);
    }

    private List<BigInteger> DenominatorsThrough(BigInteger value) {
        var result = new List<BigInteger> { BigInteger.One };
        var previousPrevious = BigInteger.Zero;
        var previous = BigInteger.One;

        for (var index = 1; ; ++index) {
            var next = ((PartialQuotient(index) * previous) + previousPrevious);
            if (next > value) { return result; }
            result.Add(next);
            previousPrevious = previous;
            previous = next;
        }
    }

    private List<BigInteger> Denominators(int count) {
        var result = new List<BigInteger>(count) { BigInteger.One };
        var previousPrevious = BigInteger.Zero;
        var previous = BigInteger.One;

        for (var index = 1; index < count; ++index) {
            var next = ((PartialQuotient(index) * previous) + previousPrevious);
            result.Add(next);
            previousPrevious = previous;
            previous = next;
        }
        return result;
    }
}

/// <summary>
/// A proof that the positive tail of one Pell channel has canonical Ostrowski language
/// <c>Prefix (RepeatedBlock)* Suffix</c>.
/// </summary>
public sealed class OstrowskiPellChannelCertificate {
    internal OstrowskiPellChannelCertificate(
        QuadraticOstrowskiSystem system,
        PolynomialBeattyShadowPellChannel channel,
        int startingExponent,
        BigInteger[] prefixDigits,
        BigInteger[] repeatedBlock,
        BigInteger[] suffixDigits) {
        System = system;
        Channel = channel;
        StartingExponent = startingExponent;
        PrefixDigits = prefixDigits;
        RepeatedBlock = repeatedBlock;
        SuffixDigits = suffixDigits;
    }

    public QuadraticOstrowskiSystem System { get; }
    public PolynomialBeattyShadowPellChannel Channel { get; }
    public int StartingExponent { get; }
    public IReadOnlyList<BigInteger> PrefixDigits { get; }
    public IReadOnlyList<BigInteger> RepeatedBlock { get; }
    public IReadOnlyList<BigInteger> SuffixDigits { get; }

    /// <summary>Compiles <c>Prefix (RepeatedBlock)* Suffix</c> into an explicit deterministic digit automaton.</summary>
    public OstrowskiDigitAutomaton CompileAutomaton() =>
        OstrowskiDigitAutomaton.FromPeriodicPattern(PrefixDigits, RepeatedBlock, SuffixDigits);

    /// <summary>Restricts the language to words with at least <paramref name="repeatCount"/> repeated blocks.</summary>
    public OstrowskiPellChannelCertificate Advance(int repeatCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(repeatCount);
        if (repeatCount == 0) { return this; }
        var prefix = new List<BigInteger>(PrefixDigits.Count + (repeatCount * RepeatedBlock.Count));
        prefix.AddRange(PrefixDigits);
        for (var repeat = 0; repeat < repeatCount; ++repeat) { prefix.AddRange(RepeatedBlock); }
        var result = new OstrowskiPellChannelCertificate(
            System,
            Channel,
            checked(StartingExponent + repeatCount),
            prefix.ToArray(),
            RepeatedBlock.ToArray(),
            SuffixDigits.ToArray()
        );
        if (!result.Verify()) { throw new InvalidOperationException("the advanced channel language failed to verify"); }
        return result;
    }

    /// <summary>Returns the canonical word for <c>StartingExponent+repeatCount</c>.</summary>
    public IReadOnlyList<BigInteger> Word(int repeatCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(repeatCount);
        var result = new List<BigInteger>(
            PrefixDigits.Count + (repeatCount * RepeatedBlock.Count) + SuffixDigits.Count
        );
        result.AddRange(PrefixDigits);
        for (var repeat = 0; repeat < repeatCount; ++repeat) { result.AddRange(RepeatedBlock); }
        result.AddRange(SuffixDigits);
        return result;
    }

    /// <summary>Rechecks the finite recurrence and digit constraints proving every word in the language.</summary>
    public bool Verify() {
        if ((StartingExponent < 0) || (RepeatedBlock.Count == 0) ||
            ((RepeatedBlock.Count % System.PeriodLength) != 0) ||
            (SuffixDigits.Count < System.PeriodStart)) {
            return false;
        }

        for (var repeat = 0; repeat <= 3; ++repeat) {
            var word = Word(repeat);
            if (!System.IsCanonical(word)) { return false; }
        }

        var shift = System.DenominatorShiftMatrix(
            firstDenominatorIndex: (SuffixDigits.Count - 1),
            length: RepeatedBlock.Count
        );
        var determinant = ((shift.A * shift.D) - (shift.B * shift.C));
        var trace = (shift.A + shift.D);
        if ((determinant != BigInteger.One) || (trace != (2 * Channel.PeriodUnit.X))) { return false; }

        var values = new BigInteger[4];
        for (var repeat = 0; repeat < values.Length; ++repeat) {
            values[repeat] = System.Evaluate(Word(repeat));
            var point = Channel.Point(checked(StartingExponent + repeat));
            var decoded = Channel.Decode(checked(StartingExponent + repeat));
            if ((point.X <= 0) || (decoded.TailIndex != values[repeat])) { return false; }
        }

        var offset = Channel.Certificate.OffsetSurdNumerator;
        var slopeSurd = Channel.Certificate.SlopeSurdNumerator;
        var affineConstantNumerator = ((trace - 2) * offset);
        var affineConstant = BigInteger.DivRem(
            affineConstantNumerator,
            slopeSurd,
            out var affineRemainder
        );
        if (!affineRemainder.IsZero) { return false; }

        for (var index = 0; index <= 1; ++index) {
            if ((values[index + 2] - (trace * values[index + 1]) + values[index]) != affineConstant) {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A deterministic finite automaton over exact non-negative Ostrowski digits.</summary>
public sealed class OstrowskiDigitAutomaton {
    private readonly Dictionary<(int State, BigInteger Digit), int> m_transitions;
    private readonly bool[] m_accepting;

    private OstrowskiDigitAutomaton(
        int startState,
        int deadState,
        BigInteger[] alphabet,
        Dictionary<(int State, BigInteger Digit), int> transitions,
        bool[] accepting) {
        StartState = startState;
        DeadState = deadState;
        Alphabet = alphabet;
        m_transitions = transitions;
        m_accepting = accepting;
    }

    public int StartState { get; }
    public int DeadState { get; }
    public int StateCount => m_accepting.Length;
    public IReadOnlyList<BigInteger> Alphabet { get; }

    public int Transition(int state, BigInteger digit) {
        if ((state < 0) || (state >= StateCount)) {
            throw new ArgumentOutOfRangeException(nameof(state), "the automaton state is out of range");
        }
        return m_transitions.GetValueOrDefault((state, digit), DeadState);
    }

    public bool IsAccepting(int state) {
        if ((state < 0) || (state >= StateCount)) {
            throw new ArgumentOutOfRangeException(nameof(state), "the automaton state is out of range");
        }
        return m_accepting[state];
    }

    public bool Accepts(IReadOnlyList<BigInteger> digits) {
        ArgumentNullException.ThrowIfNull(digits);
        var state = StartState;
        foreach (var digit in digits) { state = Transition(state, digit); }
        return IsAccepting(state);
    }

    internal static OstrowskiDigitAutomaton FromPeriodicPattern(
        IReadOnlyList<BigInteger> prefix,
        IReadOnlyList<BigInteger> block,
        IReadOnlyList<BigInteger> suffix) {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(suffix);
        if (block.Count == 0) { throw new ArgumentException("the repeated block cannot be empty", nameof(block)); }

        var symbolTransitions = new Dictionary<(int State, BigInteger Digit), HashSet<int>>();
        var epsilonTransitions = new Dictionary<int, HashSet<int>>();
        var nextState = 1;
        var cursor = 0;

        foreach (var digit in prefix) {
            var target = nextState++;
            AddTransition(symbolTransitions, cursor, digit, target);
            cursor = target;
        }

        var loopState = cursor;
        cursor = loopState;
        for (var index = 0; index < block.Count; ++index) {
            var target = (index == (block.Count - 1)) ? loopState : nextState++;
            AddTransition(symbolTransitions, cursor, block[index], target);
            cursor = target;
        }

        var suffixStart = nextState++;
        AddEpsilon(epsilonTransitions, loopState, suffixStart);
        cursor = suffixStart;
        foreach (var digit in suffix) {
            var target = nextState++;
            AddTransition(symbolTransitions, cursor, digit, target);
            cursor = target;
        }
        var acceptingNfaState = cursor;
        var alphabet = prefix.Concat(block).Concat(suffix).Distinct().Order().ToArray();

        var dfaSets = new List<int[]>();
        var dfaIndex = new Dictionary<string, int>();
        var pending = new Queue<int>();
        var startSet = EpsilonClosure([0], epsilonTransitions);
        AddDfaSet(startSet);
        var dfaTransitions = new Dictionary<(int State, BigInteger Digit), int>();

        while (pending.Count > 0) {
            var state = pending.Dequeue();
            var set = dfaSets[state];
            foreach (var digit in alphabet) {
                var moved = new HashSet<int>();
                foreach (var nfaState in set) {
                    if (symbolTransitions.TryGetValue((nfaState, digit), out var targets)) {
                        moved.UnionWith(targets);
                    }
                }
                var closure = EpsilonClosure(moved, epsilonTransitions);
                var key = SetKey(closure);
                if (!dfaIndex.TryGetValue(key, out var targetState)) {
                    targetState = AddDfaSet(closure);
                }
                dfaTransitions[(state, digit)] = targetState;
            }
        }

        var deadKey = SetKey([]);
        var deadState = dfaIndex[deadKey];
        var accepting = dfaSets.Select(set => Array.BinarySearch(set, acceptingNfaState) >= 0).ToArray();
        return new OstrowskiDigitAutomaton(0, deadState, alphabet, dfaTransitions, accepting);

        int AddDfaSet(int[] set) {
            var key = SetKey(set);
            if (dfaIndex.TryGetValue(key, out var existing)) { return existing; }
            var index = dfaSets.Count;
            dfaSets.Add(set);
            dfaIndex[key] = index;
            pending.Enqueue(index);
            return index;
        }
    }

    internal static OstrowskiDigitAutomaton FromLiteral(IReadOnlyList<BigInteger> word) {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Count == 0) { throw new ArgumentException("the literal word cannot be empty", nameof(word)); }
        var deadState = (word.Count + 1);
        var accepting = new bool[word.Count + 2];
        accepting[word.Count] = true;
        var alphabet = word.Distinct().Order().ToArray();
        var transitions = new Dictionary<(int State, BigInteger Digit), int>();

        for (var state = 0; state <= deadState; ++state) {
            foreach (var digit in alphabet) { transitions[(state, digit)] = deadState; }
        }
        for (var index = 0; index < word.Count; ++index) {
            transitions[(index, word[index])] = (index + 1);
        }

        return new OstrowskiDigitAutomaton(0, deadState, alphabet, transitions, accepting);
    }

    private static void AddTransition(
        Dictionary<(int State, BigInteger Digit), HashSet<int>> transitions,
        int state,
        BigInteger digit,
        int target) {
        if (!transitions.TryGetValue((state, digit), out var targets)) {
            targets = [];
            transitions[(state, digit)] = targets;
        }
        targets.Add(target);
    }

    private static void AddEpsilon(Dictionary<int, HashSet<int>> transitions, int state, int target) {
        if (!transitions.TryGetValue(state, out var targets)) {
            targets = [];
            transitions[state] = targets;
        }
        targets.Add(target);
    }

    private static int[] EpsilonClosure(
        IEnumerable<int> states,
        Dictionary<int, HashSet<int>> epsilonTransitions) {
        var closure = new HashSet<int>(states);
        var pending = new Stack<int>(closure);
        while (pending.Count > 0) {
            var state = pending.Pop();
            if (!epsilonTransitions.TryGetValue(state, out var targets)) { continue; }
            foreach (var target in targets) {
                if (closure.Add(target)) { pending.Push(target); }
            }
        }
        return closure.Order().ToArray();
    }

    private static string SetKey(IEnumerable<int> states) => string.Join(',', states);
}

/// <summary>A DFA with integer outputs, formed as the product of finitely many Ostrowski channel automata.</summary>
public sealed class OstrowskiOutputAutomaton {
    private readonly Dictionary<(int State, BigInteger Digit), int> m_transitions;
    private readonly BigInteger[] m_outputs;

    private OstrowskiOutputAutomaton(
        QuadraticOstrowskiSystem system,
        BigInteger[] alphabet,
        Dictionary<(int State, BigInteger Digit), int> transitions,
        BigInteger[] outputs) {
        System = system;
        Alphabet = alphabet;
        m_transitions = transitions;
        m_outputs = outputs;
    }

    public QuadraticOstrowskiSystem System { get; }
    public int StartState => 0;
    public int StateCount => m_outputs.Length;
    public IReadOnlyList<BigInteger> Alphabet { get; }

    public BigInteger Output(BigInteger value) {
        var digits = System.Represent(value);
        var state = StartState;
        foreach (var digit in digits) {
            if (!m_transitions.TryGetValue((state, digit), out state)) { return 0; }
        }
        return m_outputs[state];
    }

    public static OstrowskiOutputAutomaton Build(
        QuadraticOstrowskiSystem system,
        IReadOnlyList<(OstrowskiDigitAutomaton Automaton, BigInteger Output)> components) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(components);
        var alphabet = components.SelectMany(component => component.Automaton.Alphabet)
            .Distinct().Order().ToArray();
        var states = new List<int[]>();
        var indexes = new Dictionary<string, int>();
        var pending = new Queue<int>();
        var transitions = new Dictionary<(int State, BigInteger Digit), int>();
        AddState(components.Select(component => component.Automaton.StartState).ToArray());

        while (pending.Count > 0) {
            var stateIndex = pending.Dequeue();
            var state = states[stateIndex];
            foreach (var digit in alphabet) {
                var target = new int[components.Count];
                for (var index = 0; index < components.Count; ++index) {
                    target[index] = components[index].Automaton.Transition(state[index], digit);
                }
                transitions[(stateIndex, digit)] = AddState(target);
            }
        }

        var outputs = new BigInteger[states.Count];
        for (var stateIndex = 0; stateIndex < states.Count; ++stateIndex) {
            var output = BigInteger.Zero;
            for (var componentIndex = 0; componentIndex < components.Count; ++componentIndex) {
                if (!components[componentIndex].Automaton.IsAccepting(states[stateIndex][componentIndex])) { continue; }
                var candidate = components[componentIndex].Output;
                if (!output.IsZero && (candidate != output)) {
                    throw new InvalidOperationException("overlapping Ostrowski channel automata assign conflicting outputs");
                }
                output = candidate;
            }
            outputs[stateIndex] = output;
        }

        return new OstrowskiOutputAutomaton(system, alphabet, transitions, outputs);

        int AddState(int[] state) {
            var key = string.Join(',', state);
            if (indexes.TryGetValue(key, out var existing)) { return existing; }
            var index = states.Count;
            states.Add(state);
            indexes[key] = index;
            pending.Enqueue(index);
            return index;
        }
    }
}

/// <summary>Constructs regular Ostrowski languages for positive generalized-Pell channels.</summary>
public static class OstrowskiPellChannel {
    public static OstrowskiPellChannelCertificate Build(
        PolynomialContinuedFractionAnalysis analysis,
        PolynomialBeattyShadowPellChannel channel,
        BigInteger minimumTailIndex) {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumTailIndex);
        var system = QuadraticOstrowskiSystem.Create(analysis.Slope);
        var exponent = 0;

        while (true) {
            var words = new BigInteger[4][];
            var usable = true;
            for (var offset = 0; offset < words.Length; ++offset) {
                var point = channel.Point(checked(exponent + offset));
                var decoded = channel.Decode(checked(exponent + offset));
                if ((point.X <= 0) || (decoded.TailIndex < minimumTailIndex)) {
                    usable = false;
                    break;
                }
                words[offset] = system.Represent(decoded.TailIndex).ToArray();
            }

            if (usable && TryInfer(words, out var prefix, out var block, out var suffix)) {
                var certificate = new OstrowskiPellChannelCertificate(
                    system,
                    channel,
                    exponent,
                    prefix,
                    block,
                    suffix
                );
                if (certificate.Verify()) { return certificate; }
            }

            exponent = checked(exponent + 1);
        }
    }

    private static bool TryInfer(
        BigInteger[][] words,
        out BigInteger[] prefix,
        out BigInteger[] block,
        out BigInteger[] suffix) {
        prefix = [];
        block = [];
        suffix = [];
        var blockLength = (words[1].Length - words[0].Length);
        if ((blockLength <= 0) ||
            ((words[2].Length - words[1].Length) != blockLength) ||
            ((words[3].Length - words[2].Length) != blockLength)) {
            return false;
        }

        for (var prefixLength = 0; prefixLength <= words[0].Length; ++prefixLength) {
            var candidatePrefix = words[0][..prefixLength];
            var candidateSuffix = words[0][prefixLength..];
            var candidateBlock = words[1][prefixLength..(prefixLength + blockLength)];
            if (!words[1].SequenceEqual(Compose(candidatePrefix, candidateBlock, 1, candidateSuffix)) ||
                !words[2].SequenceEqual(Compose(candidatePrefix, candidateBlock, 2, candidateSuffix)) ||
                !words[3].SequenceEqual(Compose(candidatePrefix, candidateBlock, 3, candidateSuffix))) {
                continue;
            }

            prefix = candidatePrefix;
            block = candidateBlock;
            suffix = candidateSuffix;
            return true;
        }

        return false;
    }

    private static BigInteger[] Compose(
        BigInteger[] prefix,
        BigInteger[] block,
        int repetitions,
        BigInteger[] suffix) {
        var result = new List<BigInteger>(prefix.Length + (block.Length * repetitions) + suffix.Length);
        result.AddRange(prefix);
        for (var repetition = 0; repetition < repetitions; ++repetition) { result.AddRange(block); }
        result.AddRange(suffix);
        return result.ToArray();
    }
}
