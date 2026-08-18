using System.Numerics;
using Puck.Maths.Research;

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
    /// <summary>Gets one nonempty repeating block of partial quotients.</summary>
    public IReadOnlyList<BigInteger> ContinuedFractionPeriod { get; }
    /// <summary>Gets the non-repeating continued-fraction prefix, including the integral partial quotient.</summary>
    public IReadOnlyList<BigInteger> ContinuedFractionPrefix { get; }
    /// <summary>Gets the continued-fraction period length.</summary>
    public int PeriodLength => ContinuedFractionPeriod.Count;
    /// <summary>Gets the index of the first periodic partial quotient.</summary>
    public int PeriodStart => ContinuedFractionPrefix.Count;

    internal (BigInteger A, BigInteger B, BigInteger C, BigInteger D) DenominatorShiftMatrix(
        int firstDenominatorIndex,
        int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(firstDenominatorIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var a = BigInteger.One;
        var b = BigInteger.Zero;
        var c = BigInteger.Zero;
        var d = BigInteger.One;

        for (var offset = 1; (offset <= length); ++offset) {
            var quotient = PartialQuotient(index: (firstDenominatorIndex + offset));

            (a, b, c, d) = (
                ((quotient * a) + c),
                ((quotient * b) + d),
                a,
                b
            );
        }

        return (a, b, c, d);
    }

    private List<BigInteger> Denominators(int count) {
        var result = new List<BigInteger>(capacity: count) { BigInteger.One };
        var previousPrevious = BigInteger.Zero;
        var previous = BigInteger.One;

        for (var index = 1; (index < count); ++index) {
            var next = ((PartialQuotient(index: index) * previous) + previousPrevious);

            result.Add(item: next);
            previousPrevious = previous;
            previous = next;
        }
        return result;
    }
    private List<BigInteger> DenominatorsThrough(BigInteger value) {
        var result = new List<BigInteger> { BigInteger.One };
        var previousPrevious = BigInteger.Zero;
        var previous = BigInteger.One;

        for (var index = 1; ; ++index) {
            var next = ((PartialQuotient(index: index) * previous) + previousPrevious);

            if (next > value) { return result; }
            result.Add(item: next);
            previousPrevious = previous;
            previous = next;
        }
    }

    /// <summary>Constructs the exact system of a positive irrational quadratic surd.</summary>
    public static QuadraticOstrowskiSystem Create(QuadraticSurd basis) {
        if (
            (basis.Sign <= 0) ||
            basis.IsRational ||
            (basis.SurdNumerator <= 0)
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(basis),
                "the Ostrowski basis must be a positive quadratic irrational"
            );
        }

        var expansion = new QuadraticSurdExpansion(
            rationalNumerator: basis.RationalNumerator,
            surdCoefficient: basis.SurdNumerator,
            radicand: basis.Radicand,
            denominator: basis.Denominator
        );
        var terms = new List<BigInteger>();

        while (expansion.MoveNext()) { terms.Add(item: expansion.Quotient); }

        return new QuadraticOstrowskiSystem(
            basis,
            terms.Take(count: expansion.PeriodStart).ToArray(),
            terms.Skip(count: expansion.PeriodStart).ToArray()
        );
    }
    /// <summary>Evaluates a most-significant-digit-first representation exactly.</summary>
    public BigInteger Evaluate(IReadOnlyList<BigInteger> digits) {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Count == 0) {
            throw new ArgumentException(
                message: "an Ostrowski representation cannot be empty",
                paramName: nameof(digits)
            );
        }

        var denominators = Denominators(count: digits.Count);
        var value = BigInteger.Zero;

        for (var mostIndex = 0; (mostIndex < digits.Count); ++mostIndex) {
            var leastIndex = ((digits.Count - 1) - mostIndex);

            value += (digits[mostIndex] * denominators[leastIndex]);
        }
        return value;
    }
    /// <summary>Checks the canonical Ostrowski digit constraints.</summary>
    public bool IsCanonical(IReadOnlyList<BigInteger> digits) {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Count == 0) { return false; }
        if (
            (digits.Count > 1) &&
            digits[0].IsZero
        ) { return false; }

        var leastFirst = digits.Reverse().ToArray();

        if (
            (leastFirst[0] < 0) ||
            (leastFirst[0] >= PartialQuotient(index: 1))
        ) { return false; }

        for (var index = 1; (index < leastFirst.Length); ++index) {
            var maximum = PartialQuotient(index: (index + 1));

            if (
                (leastFirst[index] < 0) ||
                (leastFirst[index] > maximum)
            ) { return false; }
            if (
                (leastFirst[index] == maximum) &&
                !leastFirst[(index - 1)].IsZero
            ) { return false; }
        }

        return true;
    }
    /// <summary>Returns partial quotient <c>a_index</c>, extending the periodic block as needed.</summary>
    public BigInteger PartialQuotient(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index < ContinuedFractionPrefix.Count) { return ContinuedFractionPrefix[index]; }
        return ContinuedFractionPeriod[((index - ContinuedFractionPrefix.Count) % ContinuedFractionPeriod.Count)];
    }
    /// <summary>Returns the canonical most-significant-digit-first Ostrowski representation of a non-negative integer.</summary>
    public IReadOnlyList<BigInteger> Represent(BigInteger value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value.IsZero) { return [BigInteger.Zero]; }

        var denominators = DenominatorsThrough(value: value);
        var digitsLeastFirst = new BigInteger[denominators.Count];
        var remainder = value;

        for (var index = (denominators.Count - 1); (index >= 0); --index) {
            digitsLeastFirst[index] = (remainder / denominators[index]);
            remainder -= (digitsLeastFirst[index] * denominators[index]);
        }
        if (!remainder.IsZero) {
            throw new InvalidOperationException(message: "the greedy Ostrowski representation left a remainder");
        }

        var result = digitsLeastFirst.Reverse().ToArray();

        if (!IsCanonical(digits: result)) {
            throw new InvalidOperationException(message: "the greedy Ostrowski representation violated the digit constraints");
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

    public PolynomialBeattyShadowPellChannel Channel { get; }
    public IReadOnlyList<BigInteger> PrefixDigits { get; }
    public IReadOnlyList<BigInteger> RepeatedBlock { get; }
    public int StartingExponent { get; }
    public IReadOnlyList<BigInteger> SuffixDigits { get; }
    public QuadraticOstrowskiSystem System { get; }

    /// <summary>Restricts the language to words with at least <paramref name="repeatCount"/> repeated blocks.</summary>
    public OstrowskiPellChannelCertificate Advance(int repeatCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(repeatCount);
        if (repeatCount == 0) { return this; }
        var prefix = new List<BigInteger>(capacity: (PrefixDigits.Count + (repeatCount * RepeatedBlock.Count)));

        prefix.AddRange(collection: PrefixDigits);
        for (var repeat = 0; (repeat < repeatCount); ++repeat) { prefix.AddRange(collection: RepeatedBlock); }
        var result = new OstrowskiPellChannelCertificate(
            System,
            Channel,
            checked((StartingExponent + repeatCount)),
            prefix.ToArray(),
            RepeatedBlock.ToArray(),
            SuffixDigits.ToArray()
        );

        if (!result.Verify()) { throw new InvalidOperationException(message: "the advanced channel language failed to verify"); }
        return result;
    }
    /// <summary>Compiles <c>Prefix (RepeatedBlock)* Suffix</c> into an explicit deterministic digit automaton.</summary>
    public OstrowskiDigitAutomaton CompileAutomaton() =>
        OstrowskiDigitAutomaton.FromPeriodicPattern(
            block: RepeatedBlock,
            prefix: PrefixDigits,
            suffix: SuffixDigits
        );
    /// <summary>Rechecks the finite recurrence and digit constraints proving every word in the language.</summary>
    public bool Verify() {
        if (
            (StartingExponent < 0) ||
            (RepeatedBlock.Count == 0) ||
            ((RepeatedBlock.Count % System.PeriodLength) != 0) ||
            (SuffixDigits.Count < System.PeriodStart)
        ) {
            return false;
        }

        for (var repeat = 0; (repeat <= 3); ++repeat) {
            var word = Word(repeatCount: repeat);

            if (!System.IsCanonical(digits: word)) { return false; }
        }

        var shift = System.DenominatorShiftMatrix(
            firstDenominatorIndex: (SuffixDigits.Count - 1),
            length: RepeatedBlock.Count
        );
        var determinant = ((shift.A * shift.D) - (shift.B * shift.C));
        var trace = (shift.A + shift.D);

        if (
            (determinant != BigInteger.One) ||
            (trace != (2 * Channel.PeriodUnit.X))
        ) { return false; }

        var values = new BigInteger[4];

        for (var repeat = 0; (repeat < values.Length); ++repeat) {
            values[repeat] = System.Evaluate(digits: Word(repeatCount: repeat));
            var point = Channel.Point(exponent: checked((StartingExponent + repeat)));
            var decoded = Channel.Decode(exponent: checked((StartingExponent + repeat)));

            if (
                (point.X <= 0) ||
                (decoded.TailIndex != values[repeat])
            ) { return false; }
        }

        var offset = Channel.Certificate.OffsetSurdNumerator;
        var slopeSurd = Channel.Certificate.SlopeSurdNumerator;
        var affineConstantNumerator = ((trace - 2) * offset);
        var affineConstant = BigInteger.DivRem(
            dividend: affineConstantNumerator,
            divisor: slopeSurd,
            remainder: out var affineRemainder
        );

        if (!affineRemainder.IsZero) { return false; }

        for (var index = 0; (index <= 1); ++index) {
            if (((values[(index + 2)] - (trace * values[(index + 1)])) + values[index]) != affineConstant) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Returns the canonical word for <c>StartingExponent+repeatCount</c>.</summary>
    public IReadOnlyList<BigInteger> Word(int repeatCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(repeatCount);
        var result = new List<BigInteger>(capacity: ((PrefixDigits.Count + (repeatCount * RepeatedBlock.Count)) + SuffixDigits.Count));

        result.AddRange(collection: PrefixDigits);
        for (var repeat = 0; (repeat < repeatCount); ++repeat) { result.AddRange(collection: RepeatedBlock); }
        result.AddRange(collection: SuffixDigits);
        return result;
    }
}
/// <summary>A deterministic finite automaton over exact non-negative Ostrowski digits.</summary>
public sealed class OstrowskiDigitAutomaton {
    private readonly bool[] m_accepting;
    private readonly Dictionary<(int State, BigInteger Digit), int> m_transitions;

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

    public IReadOnlyList<BigInteger> Alphabet { get; }
    public int DeadState { get; }
    public int StartState { get; }
    public int StateCount => m_accepting.Length;

    internal static OstrowskiDigitAutomaton FromLiteral(IReadOnlyList<BigInteger> word) {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Count == 0) {
            throw new ArgumentException(
                message: "the literal word cannot be empty",
                paramName: nameof(word)
            );
        }
        var deadState = (word.Count + 1);
        var accepting = new bool[(word.Count + 2)];

        accepting[word.Count] = true;
        var alphabet = word.Distinct().Order().ToArray();
        var transitions = new Dictionary<(int State, BigInteger Digit), int>();

        for (var state = 0; (state <= deadState); ++state) {
            foreach (var digit in alphabet) { transitions[(state, digit)] = deadState; }
        }
        for (var index = 0; (index < word.Count); ++index) {
            transitions[(index, word[index])] = (index + 1);
        }

        return new OstrowskiDigitAutomaton(
            accepting: accepting,
            alphabet: alphabet,
            deadState: deadState,
            startState: 0,
            transitions: transitions
        );
    }
    internal static OstrowskiDigitAutomaton FromPeriodicPattern(
        IReadOnlyList<BigInteger> prefix,
        IReadOnlyList<BigInteger> block,
        IReadOnlyList<BigInteger> suffix) {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(suffix);
        if (block.Count == 0) {
            throw new ArgumentException(
                message: "the repeated block cannot be empty",
                paramName: nameof(block)
            );
        }

        var symbolTransitions = new Dictionary<(int State, BigInteger Digit), HashSet<int>>();
        var epsilonTransitions = new Dictionary<int, HashSet<int>>();
        var nextState = 1;
        var cursor = 0;

        foreach (var digit in prefix) {
            var target = nextState++;

            AddTransition(
                digit: digit,
                state: cursor,
                target: target,
                transitions: symbolTransitions
            );
            cursor = target;
        }

        var loopState = cursor;

        cursor = loopState;
        for (var index = 0; (index < block.Count); ++index) {
            var target = ((index == (block.Count - 1))
                ? loopState
                : nextState++
            );

            AddTransition(
                symbolTransitions,
                cursor,
                block[index],
                target
            );
            cursor = target;
        }

        var suffixStart = nextState++;

        AddEpsilon(
            state: loopState,
            target: suffixStart,
            transitions: epsilonTransitions
        );
        cursor = suffixStart;
        foreach (var digit in suffix) {
            var target = nextState++;

            AddTransition(
                digit: digit,
                state: cursor,
                target: target,
                transitions: symbolTransitions
            );
            cursor = target;
        }
        var acceptingNfaState = cursor;
        var alphabet = prefix.Concat(second: block).Concat(second: suffix).Distinct().Order().ToArray();

        var dfaSets = new List<int[]>();
        var dfaIndex = new Dictionary<string, int>();
        var pending = new Queue<int>();
        var startSet = EpsilonClosure(
            epsilonTransitions: epsilonTransitions,
            states: [0]
        );

        AddDfaSet(set: startSet);
        var dfaTransitions = new Dictionary<(int State, BigInteger Digit), int>();

        while (pending.Count > 0) {
            var state = pending.Dequeue();
            var set = dfaSets[state];

            foreach (var digit in alphabet) {
                var moved = new HashSet<int>();

                foreach (var nfaState in set) {
                    if (symbolTransitions.TryGetValue(
                        key: (nfaState, digit),
                        value: out var targets
                    )) {
                        moved.UnionWith(other: targets);
                    }
                }
                var closure = EpsilonClosure(
                    epsilonTransitions: epsilonTransitions,
                    states: moved
                );
                var key = SetKey(states: closure);

                if (!dfaIndex.TryGetValue(
                    key: key,
                    value: out var targetState
                )) {
                    targetState = AddDfaSet(set: closure);
                }
                dfaTransitions[(state, digit)] = targetState;
            }
        }

        var deadKey = SetKey(states: []);

        if (!dfaIndex.TryGetValue(
            key: deadKey,
            value: out var deadState
        )) {
            // Over a single-symbol alphabet the subset construction never reaches the empty set: every reachable subset
            // holds a state of the prefix chain or of the block cycle, and each of those carries the one symbol. The
            // sink only has to exist — Transition resolves an absent row to DeadState, so a state with no rows sends
            // every digit to itself, and the empty set accepts nothing. Appending it here rather than seeding it keeps
            // the numbering the reachable states already have.
            deadState = dfaSets.Count;
            dfaSets.Add(item: []);
            dfaIndex[deadKey] = deadState;
        }

        var accepting = dfaSets.Select(selector: set => (Array.BinarySearch(
            array: set,
            value: acceptingNfaState
        ) >= 0)).ToArray();

        return new OstrowskiDigitAutomaton(
            accepting: accepting,
            alphabet: alphabet,
            deadState: deadState,
            startState: 0,
            transitions: dfaTransitions
        );

        int AddDfaSet(int[] set) {
            var key = SetKey(states: set);

            if (dfaIndex.TryGetValue(
                key: key,
                value: out var existing
            )) { return existing; }
            var index = dfaSets.Count;

            dfaSets.Add(item: set);
            dfaIndex[key] = index;
            pending.Enqueue(item: index);
            return index;
        }
    }

    private static void AddEpsilon(Dictionary<int, HashSet<int>> transitions, int state, int target) {
        if (!transitions.TryGetValue(
            key: state,
            value: out var targets
        )) {
            targets = [];
            transitions[state] = targets;
        }
        targets.Add(item: target);
    }
    private static void AddTransition(
        Dictionary<(int State, BigInteger Digit), HashSet<int>> transitions,
        int state,
        BigInteger digit,
        int target) {
        if (!transitions.TryGetValue(
            key: (state, digit),
            value: out var targets
        )) {
            targets = [];
            transitions[(state, digit)] = targets;
        }
        targets.Add(item: target);
    }
    private static int[] EpsilonClosure(
        IEnumerable<int> states,
        Dictionary<int, HashSet<int>> epsilonTransitions) {
        var closure = new HashSet<int>(collection: states);
        var pending = new Stack<int>(collection: closure);

        while (pending.Count > 0) {
            var state = pending.Pop();

            if (!epsilonTransitions.TryGetValue(
                key: state,
                value: out var targets
            )) { continue; }
            foreach (var target in targets) {
                if (closure.Add(item: target)) { pending.Push(item: target); }
            }
        }
        return closure.Order().ToArray();
    }
    private static string SetKey(IEnumerable<int> states) => string.Join(
        separator: ',',
        values: states
    );

    public bool Accepts(IReadOnlyList<BigInteger> digits) {
        ArgumentNullException.ThrowIfNull(digits);
        var state = StartState;

        foreach (var digit in digits) {
            state = Transition(
                digit: digit,
                state: state
            );
        }
        return IsAccepting(state: state);
    }
    public bool IsAccepting(int state) {
        if (
            (state < 0) ||
            (state >= StateCount)
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "the automaton state is out of range"
            );
        }
        return m_accepting[state];
    }
    public int Transition(int state, BigInteger digit) {
        if (
            (state < 0) ||
            (state >= StateCount)
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "the automaton state is out of range"
            );
        }
        return m_transitions.GetValueOrDefault(
            defaultValue: DeadState,
            key: (state, digit)
        );
    }
}
/// <summary>A DFA with integer outputs, formed as the product of finitely many Ostrowski channel automata.</summary>
public sealed class OstrowskiOutputAutomaton {
    private readonly BigInteger[] m_outputs;
    private readonly Dictionary<(int State, BigInteger Digit), int> m_transitions;

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

    public IReadOnlyList<BigInteger> Alphabet { get; }
    public int StartState => 0;
    public int StateCount => m_outputs.Length;
    public QuadraticOstrowskiSystem System { get; }

    public static OstrowskiOutputAutomaton Build(
        QuadraticOstrowskiSystem system,
        IReadOnlyList<(OstrowskiDigitAutomaton Automaton, BigInteger Output)> components) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(components);
        var alphabet = components.SelectMany(selector: component => component.Automaton.Alphabet)
            .Distinct().Order().ToArray();
        var states = new List<int[]>();
        var indexes = new Dictionary<string, int>();
        var pending = new Queue<int>();
        var transitions = new Dictionary<(int State, BigInteger Digit), int>();

        AutomatonStateDedup.AddState(
            indexes: indexes,
            pending: pending,
            state: components.Select(selector: component => component.Automaton.StartState).ToArray(),
            states: states
        );

        while (pending.Count > 0) {
            var stateIndex = pending.Dequeue();
            var state = states[stateIndex];

            foreach (var digit in alphabet) {
                var target = new int[components.Count];

                for (var index = 0; (index < components.Count); ++index) {
                    target[index] = components[index].Automaton.Transition(
                        state[index],
                        digit
                    );
                }
                transitions[(stateIndex, digit)] = AutomatonStateDedup.AddState(
                    indexes: indexes,
                    pending: pending,
                    state: target,
                    states: states
                );
            }
        }

        var outputs = new BigInteger[states.Count];

        for (var stateIndex = 0; (stateIndex < states.Count); ++stateIndex) {
            var output = BigInteger.Zero;

            for (var componentIndex = 0; (componentIndex < components.Count); ++componentIndex) {
                if (!components[componentIndex].Automaton.IsAccepting(state: states[stateIndex][componentIndex])) { continue; }
                var candidate = components[componentIndex].Output;

                if (
                    !output.IsZero &&
                    (candidate != output)
                ) {
                    throw new InvalidOperationException(message: "overlapping Ostrowski channel automata assign conflicting outputs");
                }
                output = candidate;
            }
            outputs[stateIndex] = output;
        }

        return new OstrowskiOutputAutomaton(
            alphabet: alphabet,
            outputs: outputs,
            system: system,
            transitions: transitions
        );
    }
    public BigInteger Output(BigInteger value) {
        var digits = System.Represent(value: value);
        var state = StartState;

        foreach (var digit in digits) {
            if (!m_transitions.TryGetValue(
                key: (state, digit),
                value: out state
            )) { return 0; }
        }
        return m_outputs[state];
    }
}
/// <summary>Constructs regular Ostrowski languages for positive generalized-Pell channels.</summary>
public static class OstrowskiPellChannel {
    private static BigInteger[] Compose(
        BigInteger[] prefix,
        BigInteger[] block,
        int repetitions,
        BigInteger[] suffix) {
        var result = new List<BigInteger>(capacity: ((prefix.Length + (block.Length * repetitions)) + suffix.Length));

        result.AddRange(collection: prefix);
        for (var repetition = 0; (repetition < repetitions); ++repetition) { result.AddRange(collection: block); }
        result.AddRange(collection: suffix);
        return result.ToArray();
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

        if (
            (blockLength <= 0) ||
            ((words[2].Length - words[1].Length) != blockLength) ||
            ((words[3].Length - words[2].Length) != blockLength)
        ) {
            return false;
        }

        for (var prefixLength = 0; (prefixLength <= words[0].Length); ++prefixLength) {
            var candidatePrefix = words[0][..prefixLength];
            var candidateSuffix = words[0][prefixLength..];
            var candidateBlock = words[1][prefixLength..(prefixLength + blockLength)];

            if (
                !words[1].SequenceEqual(other: Compose(
                block: candidateBlock,
                prefix: candidatePrefix,
                repetitions: 1,
                suffix: candidateSuffix
            )) ||
                !words[2].SequenceEqual(other: Compose(
                block: candidateBlock,
                prefix: candidatePrefix,
                repetitions: 2,
                suffix: candidateSuffix
            )) ||
                !words[3].SequenceEqual(other: Compose(
                block: candidateBlock,
                prefix: candidatePrefix,
                repetitions: 3,
                suffix: candidateSuffix
            ))
            ) {
                continue;
            }

            prefix = candidatePrefix;
            block = candidateBlock;
            suffix = candidateSuffix;
            return true;
        }

        return false;
    }

    /// <summary>Searches ascending channel exponents for a periodic Ostrowski language, returning the first certificate that verifies.</summary>
    /// <remarks>
    /// The search is unbounded, and nothing this routine validates establishes that a usable exponent exists: it advances
    /// until four consecutive points carry positive rational coordinates and tail indexes at or above
    /// <paramref name="minimumTailIndex"/>, which a channel whose usable side lies at negative exponents never presents.
    /// The candidate channels at metallic index two and norm minus four are that case — no exponent in <c>[0, 500]</c>
    /// qualifies on the first two of them — and each iteration costs more than the last, its point being a unit power.
    /// This is a research entry point, not a routine that answers for every channel.
    /// </remarks>
    public static OstrowskiPellChannelCertificate Build(
        PolynomialContinuedFractionAnalysis analysis,
        PolynomialBeattyShadowPellChannel channel,
        BigInteger minimumTailIndex) {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumTailIndex);
        var system = QuadraticOstrowskiSystem.Create(basis: analysis.Slope);
        var exponent = 0;

        while (true) {
            var words = new BigInteger[4][];
            var usable = true;

            for (var offset = 0; (offset < words.Length); ++offset) {
                var point = channel.Point(exponent: checked((exponent + offset)));
                var decoded = channel.Decode(exponent: checked((exponent + offset)));

                if (
                    (point.X <= 0) ||
                    (decoded.TailIndex < minimumTailIndex)
                ) {
                    usable = false;
                    break;
                }
                words[offset] = system.Represent(value: decoded.TailIndex).ToArray();
            }

            if (
                usable &&
                TryInfer(
                block: out var block,
                prefix: out var prefix,
                suffix: out var suffix,
                words: words
            )
            ) {
                var certificate = new OstrowskiPellChannelCertificate(
                    channel: channel,
                    prefixDigits: prefix,
                    repeatedBlock: block,
                    startingExponent: exponent,
                    suffixDigits: suffix,
                    system: system
                );

                if (certificate.Verify()) { return certificate; }
            }

            exponent = checked((exponent + 1));
        }
    }
}
