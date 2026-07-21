using System.Numerics;

namespace Puck.Maths;

/// <summary>A canonical most-significant-digit-first positional numeration system.</summary>
public sealed class PositionalNumerationSystem {
    public PositionalNumerationSystem(int radix = 2) {
        ArgumentOutOfRangeException.ThrowIfLessThan(radix, 2);
        Radix = radix;
    }

    public int Radix { get; }

    public IReadOnlyList<int> Represent(BigInteger value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value.IsZero) { return [0]; }
        var leastFirst = new List<int>();
        while (value > 0) {
            value = BigInteger.DivRem(value, Radix, out var digit);
            leastFirst.Add((int)digit);
        }
        leastFirst.Reverse();
        return leastFirst;
    }

    public BigInteger Evaluate(IReadOnlyList<int> digits) {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Count == 0) { throw new ArgumentException("a representation cannot be empty", nameof(digits)); }
        var value = BigInteger.Zero;
        foreach (var digit in digits) {
            if ((digit < 0) || (digit >= Radix)) {
                throw new ArgumentOutOfRangeException(nameof(digits), "a digit lies outside the radix");
            }
            value = ((value * Radix) + digit);
        }
        return value;
    }
}

/// <summary>A deterministic positional-digit acceptor.</summary>
public sealed class PositionalDigitAutomaton {
    private readonly int[,] m_transitions;
    private readonly bool[] m_accepting;

    private PositionalDigitAutomaton(int radix, int[,] transitions, bool[] accepting) {
        Radix = radix;
        m_transitions = transitions;
        m_accepting = accepting;
    }

    public int Radix { get; }
    public int StartState => 0;
    public int StateCount => m_accepting.Length;
    public int Transition(int state, int digit) {
        if ((state < 0) || (state >= StateCount)) { throw new ArgumentOutOfRangeException(nameof(state)); }
        if ((digit < 0) || (digit >= Radix)) { throw new ArgumentOutOfRangeException(nameof(digit)); }
        return m_transitions[state, digit];
    }
    public bool IsAccepting(int state) => m_accepting[state];

    internal static PositionalDigitAutomaton FromLiteral(int radix, IReadOnlyList<int> word) {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Count == 0) { throw new ArgumentException("the literal word cannot be empty", nameof(word)); }
        var dead = word.Count + 1;
        var transitions = new int[word.Count + 2, radix];
        for (var state = 0; state <= dead; ++state) {
            for (var digit = 0; digit < radix; ++digit) { transitions[state, digit] = dead; }
        }
        for (var index = 0; index < word.Count; ++index) {
            transitions[index, word[index]] = index + 1;
        }
        var accepting = new bool[word.Count + 2];
        accepting[word.Count] = true;
        return new PositionalDigitAutomaton(radix, transitions, accepting);
    }

    internal static PositionalDigitAutomaton AtLeast(PositionalNumerationSystem system, BigInteger cutoff) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentOutOfRangeException.ThrowIfNegative(cutoff);
        var boundary = system.Represent(cutoff);
        // state = count * 3 + comparison, where comparison 0/1/2 means less/equal/greater.
        // Count L+1 is an absorbing "longer than boundary" accepting layer.
        var length = boundary.Count;
        var states = ((length + 2) * 3);
        var transitions = new int[states, system.Radix];
        for (var count = 0; count <= length + 1; ++count) {
            for (var comparison = 0; comparison < 3; ++comparison) {
                var state = ((count * 3) + comparison);
                for (var digit = 0; digit < system.Radix; ++digit) {
                    if (count > length) {
                        transitions[state, digit] = state;
                        continue;
                    }
                    var nextCount = count + 1;
                    if (nextCount > length) {
                        transitions[state, digit] = (((length + 1) * 3) + 2);
                        continue;
                    }
                    var nextComparison = comparison;
                    if (comparison == 1) {
                        nextComparison = digit.CompareTo(boundary[count]) + 1;
                    }
                    transitions[state, digit] = ((nextCount * 3) + nextComparison);
                }
            }
        }
        var accepting = new bool[states];
        accepting[(length * 3) + 1] = true;
        accepting[(length * 3) + 2] = true;
        for (var comparison = 0; comparison < 3; ++comparison) {
            accepting[((length + 1) * 3) + comparison] = true;
        }
        // Start in the equal-comparison state.
        return RebaseStart(new PositionalDigitAutomaton(system.Radix, transitions, accepting), 1);
    }

    private static PositionalDigitAutomaton RebaseStart(PositionalDigitAutomaton source, int oldStart) {
        var map = Enumerable.Range(0, source.StateCount).ToArray();
        (map[0], map[oldStart]) = (map[oldStart], map[0]);
        var inverse = new int[map.Length];
        for (var index = 0; index < map.Length; ++index) { inverse[map[index]] = index; }
        var transitions = new int[source.StateCount, source.Radix];
        var accepting = new bool[source.StateCount];
        for (var state = 0; state < source.StateCount; ++state) {
            accepting[inverse[state]] = source.m_accepting[state];
            for (var digit = 0; digit < source.Radix; ++digit) {
                transitions[inverse[state], digit] = inverse[source.m_transitions[state, digit]];
            }
        }
        return new PositionalDigitAutomaton(source.Radix, transitions, accepting);
    }
}

/// <summary>A positional DFAO obtained by product-composing finitely many acceptors.</summary>
public sealed class PositionalOutputAutomaton {
    private readonly int[,] m_transitions;
    private readonly BigInteger[] m_outputs;

    private PositionalOutputAutomaton(
        PositionalNumerationSystem system,
        int[,] transitions,
        BigInteger[] outputs) {
        System = system;
        m_transitions = transitions;
        m_outputs = outputs;
    }

    public PositionalNumerationSystem System { get; }
    public int StateCount => m_outputs.Length;

    public BigInteger Output(BigInteger value) {
        var state = 0;
        foreach (var digit in System.Represent(value)) { state = m_transitions[state, digit]; }
        return m_outputs[state];
    }

    public static PositionalOutputAutomaton Build(
        PositionalNumerationSystem system,
        IReadOnlyList<(PositionalDigitAutomaton Automaton, BigInteger Output)> components) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(components);
        if (components.Any(component => component.Automaton.Radix != system.Radix)) {
            throw new ArgumentException("every component must use the output system radix", nameof(components));
        }

        var states = new List<int[]>();
        var indexes = new Dictionary<string, int>();
        var pending = new Queue<int>();
        var transitionRows = new List<int[]>();
        AddState(components.Select(component => component.Automaton.StartState).ToArray());
        while (pending.Count > 0) {
            var stateIndex = pending.Dequeue();
            var row = new int[system.Radix];
            for (var digit = 0; digit < system.Radix; ++digit) {
                var target = new int[components.Count];
                for (var index = 0; index < components.Count; ++index) {
                    target[index] = components[index].Automaton.Transition(states[stateIndex][index], digit);
                }
                row[digit] = AddState(target);
            }
            while (transitionRows.Count <= stateIndex) { transitionRows.Add([]); }
            transitionRows[stateIndex] = row;
        }

        var transitions = new int[states.Count, system.Radix];
        var outputs = new BigInteger[states.Count];
        for (var stateIndex = 0; stateIndex < states.Count; ++stateIndex) {
            for (var digit = 0; digit < system.Radix; ++digit) {
                transitions[stateIndex, digit] = transitionRows[stateIndex][digit];
            }
            for (var index = 0; index < components.Count; ++index) {
                if (!components[index].Automaton.IsAccepting(states[stateIndex][index])) { continue; }
                var candidate = components[index].Output;
                if (!outputs[stateIndex].IsZero && (outputs[stateIndex] != candidate)) {
                    throw new InvalidOperationException("overlapping positional automata assign conflicting outputs");
                }
                outputs[stateIndex] = candidate;
            }
        }
        return new PositionalOutputAutomaton(system, transitions, outputs);

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
