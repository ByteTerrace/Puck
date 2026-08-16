using System.Numerics;

namespace Puck.Maths.Research;

/// <summary>
/// A dense deterministic finite automaton with a binary selection mask attached to every state.
/// </summary>
/// <remarks>
/// Digits are the integers in <c>[0, AlphabetSize)</c>. A state output is a subset of some external finite universe,
/// packed as a non-negative <see cref="BigInteger"/>. Consumers such as <see cref="AutomaticCyclicIncidence"/> interpret
/// addition of outputs as symmetric difference (bitwise XOR).
/// </remarks>
public sealed class AutomaticSelectionAutomaton {
    private readonly BigInteger[] m_stateSelections;
    private readonly int[] m_transitions;

    /// <summary>Creates an immutable dense DFAO from a state-major transition table.</summary>
    /// <param name="alphabetSize">The number of digits, starting at zero.</param>
    /// <param name="transitions">
    /// A flattened state-major table. Entry <c>state * alphabetSize + digit</c> is the target state.
    /// </param>
    /// <param name="stateSelections">The non-negative selection mask emitted by each state.</param>
    /// <param name="startState">The initial state.</param>
    public AutomaticSelectionAutomaton(
        int alphabetSize,
        ReadOnlySpan<int> transitions,
        ReadOnlySpan<BigInteger> stateSelections,
        int startState = 0
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alphabetSize);
        if (stateSelections.IsEmpty) {
            throw new ArgumentException(
                message: "At least one state is required.",
                paramName: nameof(stateSelections)
            );
        }
        if (transitions.Length != checked((alphabetSize * stateSelections.Length))) {
            throw new ArgumentException(
                message: "The transition table length must equal alphabetSize * stateSelections.Length.",
                paramName: nameof(transitions)
            );
        }
        if (((uint)startState) >= ((uint)stateSelections.Length)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(startState));
        }

        for (var index = 0; (index < transitions.Length); ++index) {
            if (((uint)transitions[index]) >= ((uint)stateSelections.Length)) {
                throw new ArgumentException(
                    message: $"Transition {index} targets an invalid state.",
                    paramName: nameof(transitions)
                );
            }
        }
        for (var state = 0; (state < stateSelections.Length); ++state) {
            if (stateSelections[state].Sign < 0) {
                throw new ArgumentException(
                    message: $"State {state} emits a negative selection mask.",
                    paramName: nameof(stateSelections)
                );
            }
        }

        AlphabetSize = alphabetSize;
        StartState = startState;
        m_transitions = transitions.ToArray();
        m_stateSelections = stateSelections.ToArray();
    }

    /// <summary>Gets the dense alphabet size.</summary>
    public int AlphabetSize { get; }
    /// <summary>Gets the initial state.</summary>
    public int StartState { get; }
    /// <summary>Gets the number of states.</summary>
    public int StateCount => m_stateSelections.Length;

    internal BigInteger SelectionAtStateUnchecked(int state) => m_stateSelections[state];
    internal int TransitionUnchecked(int state, int digit) => m_transitions[checked(((state * AlphabetSize) + digit))];

    private void ValidateState(int state) {
        if (((uint)state) >= ((uint)StateCount)) { throw new ArgumentOutOfRangeException(paramName: nameof(state)); }
    }

    /// <summary>
    /// Creates the binary automatic toggle sequence whose term at index <c>n</c> selects bit
    /// <c>v₂(n+1) mod selectionBitCount</c>.
    /// </summary>
    /// <remarks>
    /// Prefix XORs are the binary-reflected Gray code, with bit positions folded modulo
    /// <paramref name="selectionBitCount"/>. Consequently the first <c>2^selectionBitCount</c> prefixes enumerate
    /// every subset of the selected universe exactly once.
    /// </remarks>
    public static AutomaticSelectionAutomaton BinaryGrayCodeToggles(int selectionBitCount) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(selectionBitCount);
        var transitions = new int[checked((2 * selectionBitCount))];
        var selections = new BigInteger[selectionBitCount];

        for (var state = 0; (state < selectionBitCount); ++state) {
            // The state is the current run of trailing one digits, modulo the number of selection bits.
            transitions[((state * 2) + 0)] = 0;
            transitions[((state * 2) + 1)] = ((state + 1) % selectionBitCount);
            selections[state] = (BigInteger.One << state);
        }

        return new AutomaticSelectionAutomaton(
            alphabetSize: 2,
            transitions: transitions,
            stateSelections: selections
        );
    }
    /// <summary>
    /// Creates the digit-sum selector whose state and one-hot output are the sum of the input digits modulo
    /// <paramref name="residueCount"/>.
    /// </summary>
    /// <remarks>
    /// The construction is numeration-agnostic: a positional system gives the ordinary base-digit sum, while a
    /// canonical Ostrowski system gives its Ostrowski digit sum. Leading zeroes leave the state unchanged.
    /// </remarks>
    public static AutomaticSelectionAutomaton DigitSumResidues(int alphabetSize, int residueCount) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alphabetSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(residueCount);
        var transitions = new int[checked((alphabetSize * residueCount))];
        var selections = new BigInteger[residueCount];

        for (var state = 0; (state < residueCount); ++state) {
            for (var digit = 0; (digit < alphabetSize); ++digit) {
                transitions[((state * alphabetSize) + digit)] = ((state + digit) % residueCount);
            }
            selections[state] = (BigInteger.One << state);
        }

        return new AutomaticSelectionAutomaton(
            alphabetSize,
            transitions,
            selections
        );
    }
    /// <summary>Runs the automaton over a caller-supplied most-significant-digit-first word.</summary>
    public BigInteger Output(ReadOnlySpan<int> digits) {
        if (digits.IsEmpty) {
            throw new ArgumentException(
                message: "The digit word cannot be empty.",
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
        return m_stateSelections[state];
    }
    /// <summary>Returns the selection mask emitted by one state.</summary>
    public BigInteger SelectionAtState(int state) {
        ValidateState(state: state);
        return m_stateSelections[state];
    }
    /// <summary>Returns the target of one transition.</summary>
    public int Transition(int state, int digit) {
        ValidateState(state: state);
        if (((uint)digit) >= ((uint)AlphabetSize)) { throw new ArgumentOutOfRangeException(paramName: nameof(digit)); }
        return TransitionUnchecked(
            digit: digit,
            state: state
        );
    }
}
