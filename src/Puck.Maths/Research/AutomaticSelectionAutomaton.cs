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
    private readonly int[] Transitions;
    private readonly BigInteger[] StateSelections;

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
            throw new ArgumentException("At least one state is required.", nameof(stateSelections));
        }
        if (transitions.Length != checked(alphabetSize * stateSelections.Length)) {
            throw new ArgumentException(
                "The transition table length must equal alphabetSize * stateSelections.Length.",
                nameof(transitions)
            );
        }
        if ((uint)startState >= (uint)stateSelections.Length) {
            throw new ArgumentOutOfRangeException(nameof(startState));
        }

        for (var index = 0; index < transitions.Length; ++index) {
            if ((uint)transitions[index] >= (uint)stateSelections.Length) {
                throw new ArgumentException($"Transition {index} targets an invalid state.", nameof(transitions));
            }
        }
        for (var state = 0; state < stateSelections.Length; ++state) {
            if (stateSelections[state].Sign < 0) {
                throw new ArgumentException($"State {state} emits a negative selection mask.", nameof(stateSelections));
            }
        }

        AlphabetSize = alphabetSize;
        StartState = startState;
        Transitions = transitions.ToArray();
        StateSelections = stateSelections.ToArray();
    }

    /// <summary>Gets the dense alphabet size.</summary>
    public int AlphabetSize { get; }
    /// <summary>Gets the initial state.</summary>
    public int StartState { get; }
    /// <summary>Gets the number of states.</summary>
    public int StateCount => StateSelections.Length;

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
        var transitions = new int[checked(2 * selectionBitCount)];
        var selections = new BigInteger[selectionBitCount];

        for (var state = 0; state < selectionBitCount; ++state) {
            // The state is the current run of trailing one digits, modulo the number of selection bits.
            transitions[(state * 2) + 0] = 0;
            transitions[(state * 2) + 1] = ((state + 1) % selectionBitCount);
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
        var transitions = new int[checked(alphabetSize * residueCount)];
        var selections = new BigInteger[residueCount];

        for (var state = 0; state < residueCount; ++state) {
            for (var digit = 0; digit < alphabetSize; ++digit) {
                transitions[(state * alphabetSize) + digit] = ((state + digit) % residueCount);
            }
            selections[state] = (BigInteger.One << state);
        }

        return new AutomaticSelectionAutomaton(alphabetSize, transitions, selections);
    }

    /// <summary>Returns the target of one transition.</summary>
    public int Transition(int state, int digit) {
        ValidateState(state);
        if ((uint)digit >= (uint)AlphabetSize) { throw new ArgumentOutOfRangeException(nameof(digit)); }
        return TransitionUnchecked(state, digit);
    }

    /// <summary>Returns the selection mask emitted by one state.</summary>
    public BigInteger SelectionAtState(int state) {
        ValidateState(state);
        return StateSelections[state];
    }

    /// <summary>Runs the automaton over a caller-supplied most-significant-digit-first word.</summary>
    public BigInteger Output(ReadOnlySpan<int> digits) {
        if (digits.IsEmpty) { throw new ArgumentException("The digit word cannot be empty.", nameof(digits)); }
        var state = StartState;
        foreach (var digit in digits) { state = Transition(state, digit); }
        return StateSelections[state];
    }

    internal int TransitionUnchecked(int state, int digit) => Transitions[checked((state * AlphabetSize) + digit)];
    internal BigInteger SelectionAtStateUnchecked(int state) => StateSelections[state];

    private void ValidateState(int state) {
        if ((uint)state >= (uint)StateCount) { throw new ArgumentOutOfRangeException(nameof(state)); }
    }
}
