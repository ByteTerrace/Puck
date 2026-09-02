using System.Numerics;

namespace Puck.Maths;

/// <summary>Identifies the canonical digit system used to address an automatic integer sequence.</summary>
public enum IntegerNumerationKind : byte {
    /// <summary>A conventional fixed-radix positional representation.</summary>
    Positional = 1,
    /// <summary>The canonical Ostrowski representation defined by a positive quadratic irrational.</summary>
    QuadraticOstrowski = 2,
}
/// <summary>
/// Describes a canonical, most-significant-digit-first representation of every non-negative integer.
/// </summary>
public sealed class IntegerNumerationSystem {
    private readonly QuadraticOstrowskiSystem? m_ostrowski;
    private readonly PositionalNumerationSystem? m_positional;

    private IntegerNumerationSystem(PositionalNumerationSystem positional) {
        m_positional = positional;
        Kind = IntegerNumerationKind.Positional;
        AlphabetSize = positional.Radix;
    }
    private IntegerNumerationSystem(QuadraticOstrowskiSystem ostrowski, int alphabetSize) {
        m_ostrowski = ostrowski;
        Kind = IntegerNumerationKind.QuadraticOstrowski;
        AlphabetSize = alphabetSize;
    }

    /// <summary>Gets the number of digit values accepted by this system, beginning at zero.</summary>
    public int AlphabetSize { get; }
    /// <summary>Gets the quadratic irrational defining an Ostrowski system, or <see langword="null"/> for a positional system.</summary>
    public RealQuadratic? Basis => m_ostrowski?.Basis;
    /// <summary>Gets the representation kind.</summary>
    public IntegerNumerationKind Kind { get; }
    /// <summary>Gets the positional radix, or zero for an Ostrowski system.</summary>
    public int Radix => (m_positional?.Radix ?? 0);

    internal int Run(DeterministicOutputAutomaton automaton, BigInteger value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        var state = automaton.StartState;

        foreach (var digit in Represent(value: value)) {
            state = automaton.TransitionUnchecked(
                digit: digit,
                state: state
            );
        }

        return automaton.OutputSymbolUnchecked(state: state);
    }
    internal int Run(DeterministicOutputAutomaton automaton, ulong value) {
        Span<ulong> placeValues = stackalloc ulong[128];
        var count = 0;

        if (m_positional is not null) {
            var remaining = value;

            do {
                placeValues[count++] = (remaining % ((ulong)m_positional.Radix));
                remaining /= ((ulong)m_positional.Radix);
            } while (remaining != 0);
        } else {
            placeValues[count++] = 1;
            var previousPrevious = 0UL;
            var previous = 1UL;

            for (var index = 1; ; ++index) {
                var quotient = checked((ulong)m_ostrowski!.PartialQuotient(index: index));
                var next = ((((UInt128)quotient) * previous) + previousPrevious);

                if (next > value) { break; }
                if (count >= placeValues.Length) {
                    return Run(
                        automaton: automaton,
                        value: new BigInteger(value: value)
                    );
                }

                placeValues[count++] = checked((ulong)next);
                previousPrevious = previous;
                previous = checked((ulong)next);
            }
        }

        var state = automaton.StartState;

        if (m_positional is not null) {
            for (var index = (count - 1); (index >= 0); --index) {
                state = automaton.TransitionUnchecked(
                    digit: checked((int)placeValues[index]),
                    state: state
                );
            }
        } else {
            var remainder = value;

            for (var index = (count - 1); (index >= 0); --index) {
                var digit = (remainder / placeValues[index]);

                remainder -= (digit * placeValues[index]);
                state = automaton.TransitionUnchecked(
                    digit: checked((int)digit),
                    state: state
                );
            }
        }

        return automaton.OutputSymbolUnchecked(state: state);
    }

    /// <summary>Evaluates one canonical-or-caller-supplied digit word exactly.</summary>
    /// <param name="digits">The nonempty most-significant-digit-first digit word.</param>
    /// <returns>The represented non-negative integer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="digits"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="digits"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A digit is outside this system's alphabet.</exception>
    public BigInteger Evaluate(IReadOnlyList<int> digits) {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Count == 0) {
            throw new ArgumentException(
                message: "a representation cannot be empty",
                paramName: nameof(digits)
            );
        }

        if (m_positional is not null) {
            return m_positional.Evaluate(digits: digits);
        }

        var widened = new BigInteger[digits.Count];

        for (var index = 0; (index < digits.Count); ++index) {
            var digit = digits[index];

            if (((uint)digit) >= ((uint)AlphabetSize)) {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(digits),
                    message: "a digit lies outside the numeration alphabet"
                );
            }

            widened[index] = digit;
        }

        return m_ostrowski!.Evaluate(digits: widened);
    }
    /// <summary>Creates a fixed-radix positional numeration system.</summary>
    /// <param name="radix">The radix; it must be at least two.</param>
    /// <returns>The canonical positional system.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radix"/> is below two.</exception>
    public static IntegerNumerationSystem Positional(int radix = 2) =>
        new(positional: new PositionalNumerationSystem(radix: radix));
    /// <summary>Creates the Ostrowski system of a positive quadratic irrational.</summary>
    /// <param name="basis">The positive quadratic irrational defining the convergent-denominator basis.</param>
    /// <returns>The canonical quadratic Ostrowski system.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="basis"/> is not a positive quadratic irrational, or one of its periodic digit bounds exceeds
    /// the signed 32-bit digit alphabet supported by deterministic output automata.
    /// </exception>
    public static IntegerNumerationSystem QuadraticOstrowski(RealQuadratic basis) {
        var system = QuadraticOstrowskiSystem.Create(basis: basis);
        var maximumDigit = system.ContinuedFractionPrefix
            .Skip(count: 1)
            .Concat(second: system.ContinuedFractionPeriod)
            .DefaultIfEmpty(defaultValue: BigInteger.One)
            .Max();

        if (maximumDigit >= int.MaxValue) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(basis),
                message: "the Ostrowski digit alphabet exceeds the signed 32-bit automaton alphabet"
            );
        }

        return new IntegerNumerationSystem(
            alphabetSize: checked((((int)maximumDigit) + 1)),
            ostrowski: system
        );
    }
    /// <summary>Returns the canonical representation of a non-negative integer.</summary>
    /// <param name="value">The non-negative integer to represent.</param>
    /// <returns>The most-significant-digit-first digit word.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public IReadOnlyList<int> Represent(BigInteger value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        if (m_positional is not null) {
            return m_positional.Represent(value: value);
        }

        return m_ostrowski!.Represent(value: value)
            .Select(selector: digit => checked((int)digit))
            .ToArray();
    }
}
/// <summary>An immutable dense deterministic finite automaton with an integer output symbol on every state.</summary>
public sealed class DeterministicOutputAutomaton {
    private readonly int[] m_outputSymbols;
    private readonly int[] m_transitions;

    /// <summary>Creates a DFAO from a state-major transition table.</summary>
    /// <param name="alphabetSize">The number of digits, beginning at zero.</param>
    /// <param name="transitions">The flattened table at <c>state * alphabetSize + digit</c>.</param>
    /// <param name="outputSymbols">The non-negative output symbol emitted by each state.</param>
    /// <param name="startState">The initial state.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="alphabetSize"/> is not positive, <paramref name="startState"/> is invalid, or an output symbol is negative.</exception>
    /// <exception cref="ArgumentException">The table dimensions are inconsistent or a transition target is invalid.</exception>
    public DeterministicOutputAutomaton(
        int alphabetSize,
        ReadOnlySpan<int> transitions,
        ReadOnlySpan<int> outputSymbols,
        int startState = 0
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alphabetSize);
        if (outputSymbols.IsEmpty) {
            throw new ArgumentException(
                message: "at least one state is required",
                paramName: nameof(outputSymbols)
            );
        }
        if (transitions.Length != checked((alphabetSize * outputSymbols.Length))) {
            throw new ArgumentException(
                message: "the transition table length must equal alphabetSize * outputSymbols.Length",
                paramName: nameof(transitions)
            );
        }
        if (((uint)startState) >= ((uint)outputSymbols.Length)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(startState));
        }

        for (var state = 0; (state < outputSymbols.Length); ++state) {
            if (outputSymbols[state] < 0) {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(outputSymbols),
                    message: $"state {state} emits a negative output symbol"
                );
            }
        }

        for (var index = 0; (index < transitions.Length); ++index) {
            if (((uint)transitions[index]) >= ((uint)outputSymbols.Length)) {
                throw new ArgumentException(
                    message: $"transition {index} targets an invalid state",
                    paramName: nameof(transitions)
                );
            }
        }

        AlphabetSize = alphabetSize;
        (m_transitions, m_outputSymbols) = NormalizeReachable(
            alphabetSize: alphabetSize,
            outputSymbols: outputSymbols,
            startState: startState,
            transitions: transitions
        );
    }

    /// <summary>Gets the number of accepted digits, beginning at zero.</summary>
    public int AlphabetSize { get; }
    /// <summary>Gets the normalized initial state, which is always zero.</summary>
    public int StartState => 0;
    /// <summary>Gets the number of reachable states.</summary>
    public int StateCount => m_outputSymbols.Length;

    internal int OutputSymbolUnchecked(int state) => m_outputSymbols[state];
    internal int TransitionUnchecked(int state, int digit) => m_transitions[checked(((state * AlphabetSize) + digit))];

    private static (int[] Transitions, int[] Outputs) NormalizeReachable(
        int alphabetSize,
        ReadOnlySpan<int> transitions,
        ReadOnlySpan<int> outputSymbols,
        int startState
    ) {
        var oldToNew = new int[outputSymbols.Length];

        Array.Fill(
            array: oldToNew,
            value: -1
        );
        var oldStates = new List<int>(capacity: outputSymbols.Length) { startState };

        oldToNew[startState] = 0;

        for (var newState = 0; (newState < oldStates.Count); ++newState) {
            var oldState = oldStates[newState];

            for (var digit = 0; (digit < alphabetSize); ++digit) {
                var target = transitions[((oldState * alphabetSize) + digit)];

                if (oldToNew[target] >= 0) { continue; }
                oldToNew[target] = oldStates.Count;
                oldStates.Add(item: target);
            }
        }

        var normalizedTransitions = new int[checked((oldStates.Count * alphabetSize))];
        var normalizedOutputs = new int[oldStates.Count];

        for (var state = 0; (state < oldStates.Count); ++state) {
            var oldState = oldStates[state];

            normalizedOutputs[state] = outputSymbols[oldState];

            for (var digit = 0; (digit < alphabetSize); ++digit) {
                var oldTarget = transitions[((oldState * alphabetSize) + digit)];

                normalizedTransitions[((state * alphabetSize) + digit)] = oldToNew[oldTarget];
            }
        }

        return (normalizedTransitions, normalizedOutputs);
    }
    private void ValidateState(int state) {
        if (((uint)state) >= ((uint)StateCount)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(state));
        }
    }

    /// <summary>Returns the output symbol emitted by one state.</summary>
    /// <param name="state">The state index.</param>
    /// <returns>The non-negative output symbol.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is invalid.</exception>
    public int OutputSymbol(int state) {
        ValidateState(state: state);
        return m_outputSymbols[state];
    }
    /// <summary>Runs the automaton over a nonempty most-significant-digit-first word.</summary>
    /// <param name="digits">The digit word.</param>
    /// <returns>The output symbol at the final state.</returns>
    /// <exception cref="ArgumentException"><paramref name="digits"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A digit is outside the alphabet.</exception>
    public int Run(ReadOnlySpan<int> digits) {
        if (digits.IsEmpty) {
            throw new ArgumentException(
                message: "the digit word cannot be empty",
                paramName: nameof(digits)
            );
        }

        var state = StartState;

        foreach (var digit in digits) {
            state = Transition(
                digit: digit,
                state: state
            );
        }

        return m_outputSymbols[state];
    }
    /// <summary>Returns the target state for one state and digit.</summary>
    /// <param name="state">The source state.</param>
    /// <param name="digit">The input digit.</param>
    /// <returns>The target state.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> or <paramref name="digit"/> is invalid.</exception>
    public int Transition(int state, int digit) {
        ValidateState(state: state);
        if (((uint)digit) >= ((uint)AlphabetSize)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(digit));
        }

        return TransitionUnchecked(
            digit: digit,
            state: state
        );
    }
}
/// <summary>
/// A random-access integer sequence obtained by running a DFAO over canonical integer representations and mapping its
/// finite output alphabet to arbitrary-width integers.
/// </summary>
public sealed class AutomaticIntegerSequence {
    private readonly BigInteger[] m_outputAlphabet;

    /// <summary>Creates an automatic integer sequence.</summary>
    /// <param name="numeration">The canonical integer representation consumed by the automaton.</param>
    /// <param name="automaton">The deterministic finite output automaton.</param>
    /// <param name="outputAlphabet">The arbitrary-width integer assigned to each output symbol.</param>
    /// <exception cref="ArgumentNullException"><paramref name="numeration"/> or <paramref name="automaton"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The digit alphabets disagree, the output alphabet is empty, or an automaton symbol is outside it.</exception>
    public AutomaticIntegerSequence(
        IntegerNumerationSystem numeration,
        DeterministicOutputAutomaton automaton,
        ReadOnlySpan<BigInteger> outputAlphabet
    ) {
        ArgumentNullException.ThrowIfNull(numeration);
        ArgumentNullException.ThrowIfNull(automaton);
        if (numeration.AlphabetSize != automaton.AlphabetSize) {
            throw new ArgumentException(
                message: "the numeration and automaton digit alphabets differ",
                paramName: nameof(automaton)
            );
        }
        if (outputAlphabet.IsEmpty) {
            throw new ArgumentException(
                message: "the output alphabet cannot be empty",
                paramName: nameof(outputAlphabet)
            );
        }
        for (var state = 0; (state < automaton.StateCount); ++state) {
            if (((uint)automaton.OutputSymbol(state: state)) >= ((uint)outputAlphabet.Length)) {
                throw new ArgumentException(
                    message: $"state {state} emits a symbol outside the output alphabet",
                    paramName: nameof(automaton)
                );
            }
        }

        Numeration = numeration;
        Automaton = automaton;
        m_outputAlphabet = outputAlphabet.ToArray();
    }

    /// <summary>Gets the finite output automaton.</summary>
    public DeterministicOutputAutomaton Automaton { get; }
    /// <summary>Gets whether every output value is one of minus one, zero, or one.</summary>
    public bool HasSignedUnitOutput => m_outputAlphabet.All(predicate: value => ((value >= -1) && (value <= 1)));
    /// <summary>Gets the canonical integer numeration system.</summary>
    public IntegerNumerationSystem Numeration { get; }
    /// <summary>Gets the number of values in the finite output alphabet.</summary>
    public int OutputAlphabetSize => m_outputAlphabet.Length;

    /// <summary>Returns the output symbol at a non-negative arbitrary-width index.</summary>
    /// <param name="index">The non-negative sequence index.</param>
    /// <returns>The finite output symbol.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public int OutputSymbolAt(BigInteger index) => Numeration.Run(
        automaton: Automaton,
        value: index
    );
    /// <summary>Returns the output symbol at an unsigned 64-bit index without widening positional indices.</summary>
    /// <param name="index">The sequence index.</param>
    /// <returns>The finite output symbol.</returns>
    public int OutputSymbolAt(ulong index) => Numeration.Run(
        automaton: Automaton,
        value: index
    );
    /// <summary>Returns one value from the finite output alphabet.</summary>
    /// <param name="symbol">The output symbol.</param>
    /// <returns>The arbitrary-width integer assigned to the symbol.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="symbol"/> is outside the output alphabet.</exception>
    public BigInteger OutputValue(int symbol) {
        if (((uint)symbol) >= ((uint)m_outputAlphabet.Length)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(symbol));
        }

        return m_outputAlphabet[symbol];
    }
    /// <summary>Returns the arbitrary-width value at a non-negative arbitrary-width index.</summary>
    /// <param name="index">The non-negative sequence index.</param>
    /// <returns>The sequence value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public BigInteger ValueAt(BigInteger index) => m_outputAlphabet[OutputSymbolAt(index: index)];
    /// <summary>Returns the arbitrary-width value at an unsigned 64-bit index using the bounded-width evaluation path.</summary>
    /// <param name="index">The sequence index.</param>
    /// <returns>The sequence value.</returns>
    public BigInteger ValueAt(ulong index) => m_outputAlphabet[OutputSymbolAt(index: index)];
}
