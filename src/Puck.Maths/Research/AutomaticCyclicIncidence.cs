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

    /// <summary>Gets the first automatic-sequence index included in the range.</summary>
    public BigInteger Start { get; }
    /// <summary>Gets the number of automatic-sequence terms combined by symmetric difference.</summary>
    public BigInteger Length { get; }
    /// <summary>Gets the resulting context-orbit subset.</summary>
    public BigInteger SelectionMask { get; }
    /// <summary>Gets the Hamming weight of <see cref="SelectionMask"/>.</summary>
    public int SelectedLetterCount { get; }
    /// <summary>Gets whether all automatic contributions cancelled.</summary>
    public bool IsEmpty => SelectionMask.IsZero;
    /// <summary>
    /// Gets the exact CRT incidence analysis, or <see langword="null"/> when the selected relation is empty.
    /// </summary>
    public OddCyclicWordAnalysis? WordAnalysis { get; }
    /// <summary>Gets whether the selected relation is an odd even-incidence parity proof.</summary>
    public bool IsParityProof => WordAnalysis?.IsParityProof ?? false;
    /// <summary>Gets whether the selected relation is an irreducible parity proof.</summary>
    public bool IsIrreducible => WordAnalysis?.IsIrreducible ?? false;
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
    private readonly PositionalNumerationSystem? PositionalSystem;
    private readonly QuadraticOstrowskiSystem? OstrowskiSystem;
    private readonly int[,]? NormalizedPositionalTransitions;
    private readonly BigInteger[]? NormalizedPositionalSelections;
    private readonly BigInteger[]? PositionalAllDigitTargets;
    private readonly BigInteger[,]? PositionalSmallerDigitTargets;
    private readonly List<BigInteger[]> OstrowskiFreeSuffixes = [];
    private readonly List<BigInteger[]> OstrowskiForcedZeroSuffixes = [];
    private readonly List<BigInteger> OstrowskiLengthAggregates = [];
    private readonly Dictionary<BigInteger, OddCyclicWordAnalysis> AnalysisCache = [];
    private readonly object OstrowskiCacheLock = new();
    private readonly object AnalysisCacheLock = new();
    private int BinaryGrayBitCount;

    private const int AnalysisCacheCapacity = 4096;

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
            throw new ArgumentException("The selector alphabet must equal the positional radix.", nameof(selector));
        }

        Incidence = incidence;
        Selector = selector;
        Numeration = AutomaticCyclicNumeration.Positional;
        PositionalSystem = numeration;
        ValidateSelectionMasks();

        var originalStateCount = selector.StateCount;
        var normalizedStateCount = checked(originalStateCount + 1);
        var leadingZeroState = originalStateCount;
        var transitions = new int[normalizedStateCount, selector.AlphabetSize];
        var selections = new BigInteger[normalizedStateCount];

        for (var state = 0; state < originalStateCount; ++state) {
            selections[state] = selector.SelectionAtStateUnchecked(state);
            for (var digit = 0; digit < selector.AlphabetSize; ++digit) {
                transitions[state, digit] = selector.TransitionUnchecked(state, digit);
            }
        }
        selections[leadingZeroState] = selector.SelectionAtStateUnchecked(
            selector.TransitionUnchecked(selector.StartState, digit: 0)
        );
        transitions[leadingZeroState, 0] = leadingZeroState;
        for (var digit = 1; digit < selector.AlphabetSize; ++digit) {
            transitions[leadingZeroState, digit] = selector.TransitionUnchecked(selector.StartState, digit);
        }

        var allTargets = new BigInteger[normalizedStateCount];
        var smallerTargets = new BigInteger[normalizedStateCount, selector.AlphabetSize];
        for (var state = 0; state < normalizedStateCount; ++state) {
            var parity = BigInteger.Zero;
            for (var digit = 0; digit < selector.AlphabetSize; ++digit) {
                smallerTargets[state, digit] = parity;
                parity ^= (BigInteger.One << transitions[state, digit]);
            }
            allTargets[state] = parity;
        }

        NormalizedPositionalTransitions = transitions;
        NormalizedPositionalSelections = selections;
        PositionalAllDigitTargets = allTargets;
        PositionalSmallerDigitTargets = smallerTargets;
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
            AutomaticSelectionAutomaton.BinaryGrayCodeToggles(incidence.LetterCount),
            new PositionalNumerationSystem(radix: 2)
        );
        result.BinaryGrayBitCount = incidence.LetterCount;
        return result;
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
        OstrowskiSystem = numeration;
        ValidateSelectionMasks();

        var maximumDigit = MaximumOstrowskiDigit(numeration);
        if (maximumDigit >= selector.AlphabetSize) {
            throw new ArgumentException(
                $"The selector alphabet omits canonical Ostrowski digit {maximumDigit}.",
                nameof(selector)
            );
        }
    }

    /// <summary>Gets the incidence system receiving the automatic selections.</summary>
    public OddCyclicIncidence Incidence { get; }
    /// <summary>Gets the finite-output selector.</summary>
    public AutomaticSelectionAutomaton Selector { get; }
    /// <summary>Gets the indexing numeration system.</summary>
    public AutomaticCyclicNumeration Numeration { get; }

    /// <summary>Returns the selector mask at one non-negative index.</summary>
    public BigInteger SelectionAt(BigInteger index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (BinaryGrayBitCount > 0) {
            var toggle = checked((int)(BigInteger.TrailingZeroCount(index + BigInteger.One) % BinaryGrayBitCount));
            return (BigInteger.One << toggle);
        }
        var state = Selector.StartState;

        if (Numeration == AutomaticCyclicNumeration.Positional) {
            foreach (var digit in PositionalSystem!.Represent(index)) {
                state = Selector.TransitionUnchecked(state, digit);
            }
        } else {
            foreach (var digit in OstrowskiSystem!.Represent(index)) {
                state = Selector.TransitionUnchecked(state, checked((int)digit));
            }
        }

        return Selector.SelectionAtStateUnchecked(state);
    }

    /// <summary>
    /// Returns the symmetric difference of the selector masks at indices <c>0,...,exclusiveEnd-1</c>.
    /// </summary>
    public BigInteger PrefixSelection(BigInteger exclusiveEnd) {
        ArgumentOutOfRangeException.ThrowIfNegative(exclusiveEnd);
        if (BinaryGrayBitCount > 0) { return FoldBinaryGrayCode(exclusiveEnd, BinaryGrayBitCount); }
        return Numeration == AutomaticCyclicNumeration.Positional
            ? PositionalPrefixSelection(exclusiveEnd)
            : OstrowskiPrefixSelection(exclusiveEnd);
    }

    /// <summary>Returns the symmetric difference of a non-negative finite index range.</summary>
    public BigInteger RangeSelection(BigInteger start, BigInteger length) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var end = (start + length);
        return PrefixSelection(start) ^ PrefixSelection(end);
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
            selectionMask: PrefixSelection(exclusiveEnd),
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
        return AnalyzeSelection(start, length, RangeSelection(start, length), verifyExpandedMatrix);
    }

    /// <summary>
    /// Constructs a positional DFAO whose output at <c>N</c> is exactly <see cref="PrefixSelection(BigInteger)"/>.
    /// This is a finite, executable witness that binary prefix accumulation preserves positional automaticity.
    /// </summary>
    /// <param name="maximumStateCount">A safety ceiling for the reachable determinized parity states.</param>
    public AutomaticSelectionAutomaton CompilePositionalPrefixSelector(int maximumStateCount = 1_000_000) {
        if (Numeration != AutomaticCyclicNumeration.Positional) {
            throw new InvalidOperationException("Only the positional prefix compiler is currently available.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStateCount);

        var radix = Selector.AlphabetSize;
        var leadingZeroState = Selector.StateCount;
        var states = new List<(int Current, BigInteger LesserParity)>();
        var stateIndexes = new Dictionary<(int Current, BigInteger LesserParity), int>();
        var pending = new Queue<int>();
        var rows = new List<int[]>();
        AddState((leadingZeroState, BigInteger.Zero));

        while (pending.Count > 0) {
            var index = pending.Dequeue();
            var source = states[index];
            var row = new int[radix];
            for (var digit = 0; digit < radix; ++digit) {
                var target = AdvancePositionalPrefixState(source, digit);
                row[digit] = AddState(target);
            }
            while (rows.Count <= index) { rows.Add([]); }
            rows[index] = row;
        }

        var transitions = new int[checked(states.Count * radix)];
        var selections = new BigInteger[states.Count];
        for (var state = 0; state < states.Count; ++state) {
            for (var digit = 0; digit < radix; ++digit) {
                transitions[(state * radix) + digit] = rows[state][digit];
            }
            selections[state] = SelectionFromNormalizedParity(states[state].LesserParity);
        }

        return new AutomaticSelectionAutomaton(radix, transitions, selections);

        int AddState((int Current, BigInteger LesserParity) state) {
            if (stateIndexes.TryGetValue(state, out var existing)) { return existing; }
            if (states.Count >= maximumStateCount) {
                throw new InvalidOperationException(
                    $"The positional prefix selector exceeded the {maximumStateCount} state safety ceiling."
                );
            }
            var result = states.Count;
            states.Add(state);
            stateIndexes[state] = result;
            pending.Enqueue(result);
            return result;
        }
    }

    private BigInteger PositionalPrefixSelection(BigInteger exclusiveEnd) {
        var current = Selector.StateCount;
        var lesserParity = BigInteger.Zero;
        foreach (var digit in PositionalSystem!.Represent(exclusiveEnd)) {
            (current, lesserParity) = AdvancePositionalPrefixState((current, lesserParity), digit);
        }
        return SelectionFromNormalizedParity(lesserParity);
    }

    private (int Current, BigInteger LesserParity) AdvancePositionalPrefixState(
        (int Current, BigInteger LesserParity) source,
        int digit
    ) {
        var expanded = BigInteger.Zero;
        var remaining = source.LesserParity;
        while (!remaining.IsZero) {
            var state = checked((int)BigInteger.TrailingZeroCount(remaining));
            expanded ^= PositionalAllDigitTargets![state];
            remaining &= (remaining - BigInteger.One);
        }

        expanded ^= PositionalSmallerDigitTargets![source.Current, digit];
        var current = NormalizedPositionalTransitions![source.Current, digit];
        return (current, expanded);
    }

    private BigInteger SelectionFromNormalizedParity(BigInteger stateParity) {
        var selection = BigInteger.Zero;
        while (!stateParity.IsZero) {
            var state = checked((int)BigInteger.TrailingZeroCount(stateParity));
            selection ^= NormalizedPositionalSelections![state];
            stateParity &= (stateParity - BigInteger.One);
        }
        return selection;
    }

    private BigInteger OstrowskiPrefixSelection(BigInteger exclusiveEnd) {
        lock (OstrowskiCacheLock) {
            return OstrowskiPrefixSelectionLocked(exclusiveEnd);
        }
    }

    private BigInteger OstrowskiPrefixSelectionLocked(BigInteger exclusiveEnd) {
        if (exclusiveEnd.IsZero) { return BigInteger.Zero; }
        var digits = OstrowskiSystem!.Represent(exclusiveEnd);
        var length = digits.Count;
        EnsureOstrowskiSuffixDepth(length - 2);
        EnsureOstrowskiLengthAggregates(length - 1);

        var result = BigInteger.Zero;
        for (var shorter = 1; shorter < length; ++shorter) {
            result ^= OstrowskiLengthAggregates[shorter - 1];
        }

        var state = Selector.StartState;
        var forcedZero = false;
        for (var position = 0; position < length; ++position) {
            var leastIndex = (length - 1 - position);
            var actual = checked((int)digits[position]);
            var minimum = ((position == 0) && (length > 1)) ? 1 : 0;
            var maximum = forcedZero ? 0 : OstrowskiMaximumDigit(leastIndex);
            if ((actual < minimum) || (actual > maximum)) {
                throw new InvalidOperationException("The numeration system returned a non-canonical digit word.");
            }

            for (var candidate = minimum; candidate < actual; ++candidate) {
                var target = Selector.TransitionUnchecked(state, candidate);
                var nextForcedZero = (leastIndex > 0) && !forcedZero &&
                    (candidate == OstrowskiMaximumDigit(leastIndex));
                result ^= OstrowskiSuffixAggregate(leastIndex - 1, target, nextForcedZero);
            }

            state = Selector.TransitionUnchecked(state, actual);
            forcedZero = (leastIndex > 0) && !forcedZero &&
                (actual == OstrowskiMaximumDigit(leastIndex));
        }

        return result;
    }

    private void EnsureOstrowskiSuffixDepth(int maximumLeastIndex) {
        while (OstrowskiFreeSuffixes.Count <= maximumLeastIndex) {
            var leastIndex = OstrowskiFreeSuffixes.Count;
            var free = new BigInteger[Selector.StateCount];
            var forced = new BigInteger[Selector.StateCount];
            var maximum = OstrowskiMaximumDigit(leastIndex);

            for (var state = 0; state < Selector.StateCount; ++state) {
                forced[state] = OstrowskiSuffixAggregate(
                    leastIndex - 1,
                    Selector.TransitionUnchecked(state, digit: 0),
                    forcedZero: false
                );

                var aggregate = BigInteger.Zero;
                for (var digit = 0; digit <= maximum; ++digit) {
                    var nextForcedZero = (leastIndex > 0) && (digit == maximum);
                    aggregate ^= OstrowskiSuffixAggregate(
                        leastIndex - 1,
                        Selector.TransitionUnchecked(state, digit),
                        nextForcedZero
                    );
                }
                free[state] = aggregate;
            }

            OstrowskiFreeSuffixes.Add(free);
            OstrowskiForcedZeroSuffixes.Add(forced);
        }
    }

    private void EnsureOstrowskiLengthAggregates(int maximumLengthIndex) {
        while (OstrowskiLengthAggregates.Count <= maximumLengthIndex) {
            var length = (OstrowskiLengthAggregates.Count + 1);
            var leastIndex = (length - 1);
            var minimum = (length == 1) ? 0 : 1;
            var maximum = OstrowskiMaximumDigit(leastIndex);
            var aggregate = BigInteger.Zero;

            for (var digit = minimum; digit <= maximum; ++digit) {
                var target = Selector.TransitionUnchecked(Selector.StartState, digit);
                var nextForcedZero = (leastIndex > 0) && (digit == maximum);
                aggregate ^= OstrowskiSuffixAggregate(leastIndex - 1, target, nextForcedZero);
            }
            OstrowskiLengthAggregates.Add(aggregate);
        }
    }

    private BigInteger OstrowskiSuffixAggregate(int leastIndex, int state, bool forcedZero) {
        if (leastIndex < 0) { return Selector.SelectionAtStateUnchecked(state); }
        EnsureOstrowskiSuffixDepth(leastIndex);
        return forcedZero
            ? OstrowskiForcedZeroSuffixes[leastIndex][state]
            : OstrowskiFreeSuffixes[leastIndex][state];
    }

    private int OstrowskiMaximumDigit(int leastIndex) {
        var quotient = OstrowskiSystem!.PartialQuotient(leastIndex + 1);
        if (leastIndex == 0) { --quotient; }
        return checked((int)quotient);
    }

    private AutomaticCyclicIncidenceAnalysis AnalyzeSelection(
        BigInteger start,
        BigInteger length,
        BigInteger selectionMask,
        bool verifyExpandedMatrix
    ) {
        if (selectionMask.IsZero) {
            return new AutomaticCyclicIncidenceAnalysis(start, length, selectionMask, 0, wordAnalysis: null);
        }

        var selected = new int[checked((int)BigInteger.PopCount(selectionMask))];
        var count = 0;
        for (var letter = 0; letter < Incidence.LetterCount; ++letter) {
            if ((selectionMask & (BigInteger.One << letter)).IsZero) { continue; }
            selected[count++] = letter;
        }

        OddCyclicWordAnalysis wordAnalysis;
        if (!verifyExpandedMatrix && TryGetCachedAnalysis(selectionMask, out var cached)) {
            wordAnalysis = cached;
        } else {
            wordAnalysis = Incidence.Analyze(selected, verifyExpandedMatrix);
            if (!verifyExpandedMatrix && (BinaryGrayBitCount == 0)) { TryCacheAnalysis(selectionMask, wordAnalysis); }
        }

        return new AutomaticCyclicIncidenceAnalysis(start, length, selectionMask, count, wordAnalysis);
    }

    private bool TryGetCachedAnalysis(BigInteger selectionMask, out OddCyclicWordAnalysis analysis) {
        if (BinaryGrayBitCount > 0) {
            analysis = null!;
            return false;
        }
        lock (AnalysisCacheLock) {
            return AnalysisCache.TryGetValue(selectionMask, out analysis!);
        }
    }

    private void TryCacheAnalysis(BigInteger selectionMask, OddCyclicWordAnalysis analysis) {
        lock (AnalysisCacheLock) {
            if (AnalysisCache.Count >= AnalysisCacheCapacity) { return; }
            AnalysisCache.TryAdd(selectionMask, analysis);
        }
    }

    private void ValidateSelectionMasks() {
        for (var state = 0; state < Selector.StateCount; ++state) {
            var selection = Selector.SelectionAtStateUnchecked(state);
            if ((selection >> Incidence.LetterCount) != BigInteger.Zero) {
                throw new ArgumentException(
                    $"Selector state {state} emits a letter outside the incidence system.",
                    nameof(Selector)
                );
            }
        }
    }

    private static int MaximumOstrowskiDigit(QuadraticOstrowskiSystem system) {
        var maximum = system.PartialQuotient(1);
        for (var index = 1; index < system.ContinuedFractionPrefix.Count; ++index) {
            maximum = BigInteger.Max(maximum, system.ContinuedFractionPrefix[index]);
        }
        foreach (var quotient in system.ContinuedFractionPeriod) {
            maximum = BigInteger.Max(maximum, quotient);
        }
        return checked((int)maximum);
    }

    private static BigInteger FoldBinaryGrayCode(BigInteger index, int bitCount) {
        var gray = (index ^ (index >> 1));
        var result = BigInteger.Zero;
        while (!gray.IsZero) {
            var sourceBit = checked((int)BigInteger.TrailingZeroCount(gray));
            result ^= (BigInteger.One << (sourceBit % bitCount));
            gray &= (gray - BigInteger.One);
        }
        return result;
    }
}
