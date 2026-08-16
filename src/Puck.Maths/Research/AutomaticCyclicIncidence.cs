using System.Diagnostics;
using System.Numerics;

namespace Puck.Maths.Research;

/// <summary>Identifies the numeration system used to index an automatic cyclic-incidence selector.</summary>
public enum AutomaticCyclicNumeration {
    /// <summary>Ordinary fixed-radix positional representations.</summary>
    Positional,
    /// <summary>Canonical Ostrowski representations for a positive quadratic irrational.</summary>
    QuadraticOstrowski
}
/// <summary>The exact finite relation selected by one automatic prefix or range.</summary>
public sealed class AutomaticCyclicIncidenceAnalysis {
    internal AutomaticCyclicIncidenceAnalysis(
        BigInteger start,
        BigInteger length,
        BigInteger selectionMask,
        int selectedLetterCount,
        OddCyclicWordAnalysis? wordAnalysis
    ) {
        Start = start;
        Length = length;
        SelectionMask = selectionMask;
        SelectedLetterCount = selectedLetterCount;
        WordAnalysis = wordAnalysis;
    }

    /// <summary>Gets whether all automatic contributions cancelled.</summary>
    public bool IsEmpty => SelectionMask.IsZero;
    /// <summary>Gets whether the selected relation is an irreducible parity proof.</summary>
    public bool IsIrreducible => (WordAnalysis?.IsIrreducible ?? false);
    /// <summary>Gets whether the selected relation is an odd even-incidence parity proof.</summary>
    public bool IsParityProof => (WordAnalysis?.IsParityProof ?? false);
    /// <summary>Gets the number of automatic-sequence terms combined by symmetric difference.</summary>
    public BigInteger Length { get; }
    /// <summary>Gets the Hamming weight of <see cref="SelectionMask"/>.</summary>
    public int SelectedLetterCount { get; }
    /// <summary>Gets the resulting context-orbit subset.</summary>
    public BigInteger SelectionMask { get; }
    /// <summary>Gets the first automatic-sequence index included in the range.</summary>
    public BigInteger Start { get; }
    /// <summary>
    /// Gets the exact CRT incidence analysis, or <see langword="null"/> when the selected relation is empty.
    /// </summary>
    public OddCyclicWordAnalysis? WordAnalysis { get; }
}
/// <summary>
/// Composes a finite-output automatic sequence with an exact odd-cyclic binary incidence system.
/// </summary>
/// <remarks>
/// At index <c>j</c>, the selector emits a bit mask of context orbits. A prefix or range is their symmetric difference,
/// which is precisely addition in the relation space over <c>GF(2)</c>. Positional prefixes are evaluated by a finite
/// parity-state transducer in time linear in the digit count, independent of the numeric prefix length. Quadratic
/// Ostrowski prefixes use exact digit dynamic programming with cached suffix aggregates.
/// </remarks>
public sealed class AutomaticCyclicIncidence {
    private const int AnalysisCacheCapacity = 4096;

    private readonly BigInteger[]? m_normalizedPositionalSelections;
    private readonly int[,]? m_normalizedPositionalTransitions;
    private readonly QuadraticOstrowskiSystem? m_ostrowskiSystem;
    private readonly BigInteger[]? m_positionalAllDigitTargets;
    private readonly BigInteger[,]? m_positionalSmallerDigitTargets;
    private readonly PositionalNumerationSystem? m_positionalSystem;

    private int m_binaryGrayBitCount;

    private readonly List<BigInteger[]> m_ostrowskiFreeSuffixes = [];
    private readonly List<BigInteger[]> m_ostrowskiForcedZeroSuffixes = [];
    private readonly List<BigInteger> m_ostrowskiLengthAggregates = [];
    private readonly Dictionary<BigInteger, OddCyclicWordAnalysis> m_analysisCache = [];
    private readonly Lock m_ostrowskiCacheLock = new();
    private readonly Lock m_analysisCacheLock = new();

    /// <summary>Creates a positional automatic incidence system.</summary>
    public AutomaticCyclicIncidence(
        OddCyclicIncidence incidence,
        AutomaticSelectionAutomaton selector,
        PositionalNumerationSystem numeration
    ) {
        ArgumentNullException.ThrowIfNull(incidence);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(numeration);
        if (selector.AlphabetSize != numeration.Radix) {
            throw new ArgumentException(
                message: "The selector alphabet must equal the positional radix.",
                paramName: nameof(selector)
            );
        }

        Incidence = incidence;
        Selector = selector;
        Numeration = AutomaticCyclicNumeration.Positional;
        m_positionalSystem = numeration;
        ValidateSelectionMasks();

        var originalStateCount = selector.StateCount;
        var normalizedStateCount = checked((originalStateCount + 1));
        var leadingZeroState = originalStateCount;
        var transitions = new int[normalizedStateCount, selector.AlphabetSize];
        var selections = new BigInteger[normalizedStateCount];

        for (var state = 0; (state < originalStateCount); ++state) {
            selections[state] = selector.SelectionAtStateUnchecked(state: state);
            for (var digit = 0; (digit < selector.AlphabetSize); ++digit) {
                transitions[state, digit] = selector.TransitionUnchecked(
                    digit: digit,
                    state: state
                );
            }
        }
        selections[leadingZeroState] = selector.SelectionAtStateUnchecked(state: selector.TransitionUnchecked(
            selector.StartState,
            digit: 0
        ));
        transitions[leadingZeroState, 0] = leadingZeroState;
        for (var digit = 1; (digit < selector.AlphabetSize); ++digit) {
            transitions[leadingZeroState, digit] = selector.TransitionUnchecked(
                digit: digit,
                state: selector.StartState
            );
        }

        var allTargets = new BigInteger[normalizedStateCount];
        var smallerTargets = new BigInteger[normalizedStateCount, selector.AlphabetSize];

        for (var state = 0; (state < normalizedStateCount); ++state) {
            var parity = BigInteger.Zero;

            for (var digit = 0; (digit < selector.AlphabetSize); ++digit) {
                smallerTargets[state, digit] = parity;
                parity ^= (BigInteger.One << transitions[state, digit]);
            }
            allTargets[state] = parity;
        }

        m_normalizedPositionalTransitions = transitions;
        m_normalizedPositionalSelections = selections;
        m_positionalAllDigitTargets = allTargets;
        m_positionalSmallerDigitTargets = smallerTargets;
    }

    private (int Current, BigInteger LesserParity) AdvancePositionalPrefixState(
        (int Current, BigInteger LesserParity) source,
        int digit
    ) {
        var expanded = BigInteger.Zero;
        var remaining = source.LesserParity;

        while (!remaining.IsZero) {
            var state = checked((int)BigInteger.TrailingZeroCount(value: remaining));

            expanded ^= m_positionalAllDigitTargets![state];
            remaining &= (remaining - BigInteger.One);
        }

        expanded ^= m_positionalSmallerDigitTargets![source.Current, digit];
        var current = m_normalizedPositionalTransitions![source.Current, digit];

        return (current, expanded);
    }
    private AutomaticCyclicIncidenceAnalysis AnalyzeSelection(
        BigInteger start,
        BigInteger length,
        BigInteger selectionMask,
        bool verifyExpandedMatrix
    ) {
        if (selectionMask.IsZero) {
            return new AutomaticCyclicIncidenceAnalysis(
                start,
                length,
                selectionMask,
                0,
                wordAnalysis: null
            );
        }

        var selected = new int[checked((int)BigInteger.PopCount(value: selectionMask))];
        var count = 0;

        for (var letter = 0; (letter < Incidence.LetterCount); ++letter) {
            if ((selectionMask & (BigInteger.One << letter)).IsZero) { continue; }
            selected[count++] = letter;
        }

        OddCyclicWordAnalysis wordAnalysis;

        if (
            !verifyExpandedMatrix &&
            TryGetCachedAnalysis(
            analysis: out var cached,
            selectionMask: selectionMask
        )
        ) {
            wordAnalysis = cached;
        } else {
            wordAnalysis = Incidence.Analyze(
                selectedLetters: selected,
                verifyExpandedMatrix: verifyExpandedMatrix
            );
            if (
                !verifyExpandedMatrix &&
                (m_binaryGrayBitCount == 0)
            ) {
                TryCacheAnalysis(
                    analysis: wordAnalysis,
                    selectionMask: selectionMask
                );
            }
        }

        return new AutomaticCyclicIncidenceAnalysis(
            length: length,
            selectedLetterCount: count,
            selectionMask: selectionMask,
            start: start,
            wordAnalysis: wordAnalysis
        );
    }
    private void EnsureOstrowskiLengthAggregates(int maximumLengthIndex) {
        while (m_ostrowskiLengthAggregates.Count <= maximumLengthIndex) {
            var length = (m_ostrowskiLengthAggregates.Count + 1);
            var leastIndex = (length - 1);
            var minimum = ((length == 1)
                ? 0
                : 1
            );
            var maximum = OstrowskiMaximumDigit(leastIndex: leastIndex);
            var aggregate = BigInteger.Zero;

            for (var digit = minimum; (digit <= maximum); ++digit) {
                var target = Selector.TransitionUnchecked(
                    digit: digit,
                    state: Selector.StartState
                );
                var nextForcedZero = ((leastIndex > 0) && (digit == maximum));

                aggregate ^= OstrowskiSuffixAggregate(
                    forcedZero: nextForcedZero,
                    leastIndex: (leastIndex - 1),
                    state: target
                );
            }
            m_ostrowskiLengthAggregates.Add(item: aggregate);
        }
    }
    private void EnsureOstrowskiSuffixDepth(int maximumLeastIndex) {
        while (m_ostrowskiFreeSuffixes.Count <= maximumLeastIndex) {
            var leastIndex = m_ostrowskiFreeSuffixes.Count;
            var free = new BigInteger[Selector.StateCount];
            var forced = new BigInteger[Selector.StateCount];
            var maximum = OstrowskiMaximumDigit(leastIndex: leastIndex);

            for (var state = 0; (state < Selector.StateCount); ++state) {
                forced[state] = OstrowskiSuffixAggregate(
                    (leastIndex - 1),
                    Selector.TransitionUnchecked(
                        state,
                        digit: 0
                    ),
                    forcedZero: false
                );

                var aggregate = BigInteger.Zero;

                for (var digit = 0; (digit <= maximum); ++digit) {
                    var nextForcedZero = ((leastIndex > 0) && (digit == maximum));

                    aggregate ^= OstrowskiSuffixAggregate(
                        (leastIndex - 1),
                        Selector.TransitionUnchecked(
                            digit: digit,
                            state: state
                        ),
                        nextForcedZero
                    );
                }
                free[state] = aggregate;
            }

            m_ostrowskiFreeSuffixes.Add(item: free);
            m_ostrowskiForcedZeroSuffixes.Add(item: forced);
        }
    }
    private static BigInteger FoldBinaryGrayCode(BigInteger index, int bitCount) {
        var gray = index ^ (index >> 1);
        var result = BigInteger.Zero;

        while (!gray.IsZero) {
            var sourceBit = checked((int)BigInteger.TrailingZeroCount(value: gray));

            result ^= (BigInteger.One << (sourceBit % bitCount));
            gray &= (gray - BigInteger.One);
        }
        return result;
    }
    private static int MaximumOstrowskiDigit(QuadraticOstrowskiSystem system) {
        var maximum = system.PartialQuotient(index: 1);

        for (var index = 1; (index < system.ContinuedFractionPrefix.Count); ++index) {
            maximum = BigInteger.Max(
                left: maximum,
                right: system.ContinuedFractionPrefix[index]
            );
        }
        foreach (var quotient in system.ContinuedFractionPeriod) {
            maximum = BigInteger.Max(
                left: maximum,
                right: quotient
            );
        }
        return checked((int)maximum);
    }
    private int OstrowskiMaximumDigit(int leastIndex) {
        var quotient = m_ostrowskiSystem!.PartialQuotient(index: (leastIndex + 1));

        if (leastIndex == 0) { --quotient; }
        return checked((int)quotient);
    }
    private BigInteger OstrowskiPrefixSelection(BigInteger exclusiveEnd) {
        lock (m_ostrowskiCacheLock) {
            return OstrowskiPrefixSelectionLocked(exclusiveEnd: exclusiveEnd);
        }
    }
    private BigInteger OstrowskiPrefixSelectionLocked(BigInteger exclusiveEnd) {
        if (exclusiveEnd.IsZero) { return BigInteger.Zero; }
        var digits = m_ostrowskiSystem!.Represent(value: exclusiveEnd);
        var length = digits.Count;

        EnsureOstrowskiSuffixDepth(maximumLeastIndex: (length - 2));
        EnsureOstrowskiLengthAggregates(maximumLengthIndex: (length - 1));

        var result = BigInteger.Zero;

        for (var shorter = 1; (shorter < length); ++shorter) {
            result ^= m_ostrowskiLengthAggregates[(shorter - 1)];
        }

        var state = Selector.StartState;
        var forcedZero = false;

        for (var position = 0; (position < length); ++position) {
            var leastIndex = ((length - 1) - position);
            var actual = checked((int)digits[position]);
            var minimum = (((position == 0) && (length > 1))
                ? 1
                : 0
            );
            var maximum = (forcedZero
                ? 0
                : OstrowskiMaximumDigit(leastIndex: leastIndex)
            );

            if (
                (actual < minimum) ||
                (actual > maximum)
            ) {
                throw new InvalidOperationException(message: "The numeration system returned a non-canonical digit word.");
            }

            for (var candidate = minimum; (candidate < actual); ++candidate) {
                var target = Selector.TransitionUnchecked(
                    digit: candidate,
                    state: state
                );
                var nextForcedZero = ((leastIndex > 0) && !forcedZero &&
                    (candidate == OstrowskiMaximumDigit(leastIndex: leastIndex)));

                result ^= OstrowskiSuffixAggregate(
                    forcedZero: nextForcedZero,
                    leastIndex: (leastIndex - 1),
                    state: target
                );
            }

            state = Selector.TransitionUnchecked(
                digit: actual,
                state: state
            );
            forcedZero = ((leastIndex > 0) && !forcedZero &&
                (actual == OstrowskiMaximumDigit(leastIndex: leastIndex)));
        }

        return result;
    }
    private BigInteger OstrowskiSuffixAggregate(int leastIndex, int state, bool forcedZero) {
        if (leastIndex < 0) { return Selector.SelectionAtStateUnchecked(state: state); }
        EnsureOstrowskiSuffixDepth(maximumLeastIndex: leastIndex);
        return (forcedZero
            ? m_ostrowskiForcedZeroSuffixes[leastIndex][state]
            : m_ostrowskiFreeSuffixes[leastIndex][state]
        );
    }
    private BigInteger PositionalPrefixSelection(BigInteger exclusiveEnd) {
        var current = Selector.StateCount;
        var lesserParity = BigInteger.Zero;

        foreach (var digit in m_positionalSystem!.Represent(value: exclusiveEnd)) {
            (current, lesserParity) = AdvancePositionalPrefixState(
                digit: digit,
                source: (current, lesserParity)
            );
        }
        return SelectionFromNormalizedParity(stateParity: lesserParity);
    }
    private BigInteger SelectionAtAutomaton(BigInteger index) {
        var state = Selector.StartState;

        if (Numeration == AutomaticCyclicNumeration.Positional) {
            foreach (var digit in m_positionalSystem!.Represent(value: index)) {
                state = Selector.TransitionUnchecked(
                    digit: digit,
                    state: state
                );
            }
        } else {
            foreach (var digit in m_ostrowskiSystem!.Represent(value: index)) {
                state = Selector.TransitionUnchecked(
                    digit: checked((int)digit),
                    state: state
                );
            }
        }

        return Selector.SelectionAtStateUnchecked(state: state);
    }
    private BigInteger SelectionFromNormalizedParity(BigInteger stateParity) {
        var selection = BigInteger.Zero;

        while (!stateParity.IsZero) {
            var state = checked((int)BigInteger.TrailingZeroCount(value: stateParity));

            selection ^= m_normalizedPositionalSelections![state];
            stateParity &= (stateParity - BigInteger.One);
        }
        return selection;
    }
    private void TryCacheAnalysis(BigInteger selectionMask, OddCyclicWordAnalysis analysis) {
        lock (m_analysisCacheLock) {
            if (m_analysisCache.Count >= AnalysisCacheCapacity) { return; }
            m_analysisCache.TryAdd(
                key: selectionMask,
                value: analysis
            );
        }
    }
    private bool TryGetCachedAnalysis(BigInteger selectionMask, out OddCyclicWordAnalysis analysis) {
        if (m_binaryGrayBitCount > 0) {
            analysis = null!;
            return false;
        }
        lock (m_analysisCacheLock) {
            return m_analysisCache.TryGetValue(
                key: selectionMask,
                value: out analysis!
            );
        }
    }
    private void ValidateSelectionMasks() {
        for (var state = 0; (state < Selector.StateCount); ++state) {
            var selection = Selector.SelectionAtStateUnchecked(state: state);

            if ((selection >> Incidence.LetterCount) != BigInteger.Zero) {
                throw new ArgumentException(
                    message: $"Selector state {state} emits a letter outside the incidence system.",
                    paramName: nameof(Selector)
                );
            }
        }
    }

    /// <summary>Analyzes the automatic prefix ending immediately before <paramref name="exclusiveEnd"/>.</summary>
    public AutomaticCyclicIncidenceAnalysis AnalyzePrefix(
        BigInteger exclusiveEnd,
        bool verifyExpandedMatrix = false
    ) {
        ArgumentOutOfRangeException.ThrowIfNegative(exclusiveEnd);
        return AnalyzeSelection(
            start: BigInteger.Zero,
            length: exclusiveEnd,
            selectionMask: PrefixSelection(exclusiveEnd: exclusiveEnd),
            verifyExpandedMatrix: verifyExpandedMatrix
        );
    }
    /// <summary>Analyzes one automatic range after cancellation in the binary relation space.</summary>
    public AutomaticCyclicIncidenceAnalysis AnalyzeRange(
        BigInteger start,
        BigInteger length,
        bool verifyExpandedMatrix = false
    ) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return AnalyzeSelection(
            start,
            length,
            RangeSelection(
                length: length,
                start: start
            ),
            verifyExpandedMatrix
        );
    }
    /// <summary>
    /// Constructs a positional DFAO whose output at <c>N</c> is exactly <see cref="PrefixSelection(BigInteger)"/>.
    /// This is a finite, executable witness that binary prefix accumulation preserves positional automaticity.
    /// </summary>
    /// <param name="maximumStateCount">A safety ceiling for the reachable determinized parity states.</param>
    public AutomaticSelectionAutomaton CompilePositionalPrefixSelector(int maximumStateCount = 1_000_000) {
        if (Numeration != AutomaticCyclicNumeration.Positional) {
            throw new InvalidOperationException(message: "Only the positional prefix compiler is currently available.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStateCount);

        var radix = Selector.AlphabetSize;
        var leadingZeroState = Selector.StateCount;
        var states = new List<(int Current, BigInteger LesserParity)>();
        var stateIndexes = new Dictionary<(int Current, BigInteger LesserParity), int>();
        var pending = new Queue<int>();
        var rows = new List<int[]>();

        AddState(state: (leadingZeroState, BigInteger.Zero));

        while (pending.Count > 0) {
            var index = pending.Dequeue();
            var source = states[index];
            var row = new int[radix];

            for (var digit = 0; (digit < radix); ++digit) {
                var target = AdvancePositionalPrefixState(
                    digit: digit,
                    source: source
                );

                row[digit] = AddState(state: target);
            }
            while (rows.Count <= index) { rows.Add(item: []); }
            rows[index] = row;
        }

        var transitions = new int[checked((states.Count * radix))];
        var selections = new BigInteger[states.Count];

        for (var state = 0; (state < states.Count); ++state) {
            for (var digit = 0; (digit < radix); ++digit) {
                transitions[((state * radix) + digit)] = rows[state][digit];
            }
            selections[state] = SelectionFromNormalizedParity(stateParity: states[state].LesserParity);
        }

        return new AutomaticSelectionAutomaton(
            radix,
            transitions,
            selections
        );

        int AddState((int Current, BigInteger LesserParity) state) {
            if (stateIndexes.TryGetValue(
                key: state,
                value: out var existing
            )) { return existing; }
            if (states.Count >= maximumStateCount) {
                throw new InvalidOperationException(message: $"The positional prefix selector exceeded the {maximumStateCount} state safety ceiling.");
            }
            var result = states.Count;

            states.Add(item: state);
            stateIndexes[state] = result;
            pending.Enqueue(item: result);
            return result;
        }
    }
    /// <summary>
    /// Creates the canonical binary Gray walk through the incidence system's complete selection space.
    /// </summary>
    /// <remarks>
    /// Prefixes <c>0,...,2^LetterCount-1</c> select every context-orbit subset exactly once, and therefore include every
    /// kernel relation. This converts any exact finite classification of selections into an equivalent classification
    /// of one automatic prefix family.
    /// </remarks>
    public static AutomaticCyclicIncidence CreateBinaryGrayCodeEnumerator(OddCyclicIncidence incidence) {
        ArgumentNullException.ThrowIfNull(incidence);
        var result = new AutomaticCyclicIncidence(
            incidence,
            AutomaticSelectionAutomaton.BinaryGrayCodeToggles(selectionBitCount: incidence.LetterCount),
            new PositionalNumerationSystem(radix: 2)
        );

        result.m_binaryGrayBitCount = incidence.LetterCount;
        return result;
    }
    /// <summary>
    /// Returns the symmetric difference of the selector masks at indices <c>0,...,exclusiveEnd-1</c>.
    /// </summary>
    public BigInteger PrefixSelection(BigInteger exclusiveEnd) {
        ArgumentOutOfRangeException.ThrowIfNegative(exclusiveEnd);
        if (m_binaryGrayBitCount > 0) {
            return FoldBinaryGrayCode(
                bitCount: m_binaryGrayBitCount,
                index: exclusiveEnd
            );
        }
        return ((Numeration == AutomaticCyclicNumeration.Positional)
            ? PositionalPrefixSelection(exclusiveEnd: exclusiveEnd)
            : OstrowskiPrefixSelection(exclusiveEnd: exclusiveEnd)
        );
    }
    /// <summary>Returns the symmetric difference of a non-negative finite index range.</summary>
    public BigInteger RangeSelection(BigInteger start, BigInteger length) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var end = (start + length);

        return PrefixSelection(exclusiveEnd: start) ^ PrefixSelection(exclusiveEnd: end);
    }
    /// <summary>Returns the selector mask at one non-negative index.</summary>
    public BigInteger SelectionAt(BigInteger index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (m_binaryGrayBitCount > 0) {
            var toggle = checked((int)(BigInteger.TrailingZeroCount(value: (index + BigInteger.One)) % m_binaryGrayBitCount));
            var selection = (BigInteger.One << toggle);

            Debug.Assert(
                condition: (selection == SelectionAtAutomaton(index: index)),
                message: "the Gray fast path disagrees with its DFAO"
            );
            return selection;
        }
        return SelectionAtAutomaton(index: index);
    }

    /// <summary>Creates a quadratic-Ostrowski automatic incidence system.</summary>
    public AutomaticCyclicIncidence(
        OddCyclicIncidence incidence,
        AutomaticSelectionAutomaton selector,
        QuadraticOstrowskiSystem numeration
    ) {
        ArgumentNullException.ThrowIfNull(incidence);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(numeration);

        Incidence = incidence;
        Selector = selector;
        Numeration = AutomaticCyclicNumeration.QuadraticOstrowski;
        m_ostrowskiSystem = numeration;
        ValidateSelectionMasks();

        var maximumDigit = MaximumOstrowskiDigit(system: numeration);

        if (maximumDigit >= selector.AlphabetSize) {
            throw new ArgumentException(
                message: $"The selector alphabet omits canonical Ostrowski digit {maximumDigit}.",
                paramName: nameof(selector)
            );
        }
    }

    /// <summary>Gets the incidence system receiving the automatic selections.</summary>
    public OddCyclicIncidence Incidence { get; }
    /// <summary>Gets the indexing numeration system.</summary>
    public AutomaticCyclicNumeration Numeration { get; }
    /// <summary>Gets the finite-output selector.</summary>
    public AutomaticSelectionAutomaton Selector { get; }
}
