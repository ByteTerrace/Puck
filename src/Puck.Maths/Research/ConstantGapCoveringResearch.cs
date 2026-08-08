using System.Numerics;

namespace Puck.Maths.Research;

/// <summary>One residue class occupied by a letter of a constant-gap coloring.</summary>
public readonly record struct ConstantGapClass(int Gap, int Residue);

/// <summary>An exact disjoint covering of the integers by constant-gap residue classes.</summary>
public sealed class ConstantGapCovering {
    private readonly ConstantGapClass[] m_classes;
    private readonly IReadOnlyList<ConstantGapClass> m_readOnlyClasses;

    internal ConstantGapCovering(int period, ConstantGapClass[] classes) {
        Period = period;
        m_classes = [.. classes];
        m_readOnlyClasses = Array.AsReadOnly(array: m_classes);
    }

    /// <summary>Gets the least global period of the coloring.</summary>
    public int Period { get; }
    /// <summary>Gets the number of distinct letters.</summary>
    public int SymbolCount => m_classes.Length;
    /// <summary>Gets the residue class belonging to each dense symbol index.</summary>
    public IReadOnlyList<ConstantGapClass> Classes => m_readOnlyClasses;

    /// <summary>Returns the symbol at any signed integer index.</summary>
    public int SymbolAt(BigInteger index) {
        for (var symbol = 0; (symbol < m_classes.Length); ++symbol) {
            var item = m_classes[symbol];
            var residue = (int)(index % item.Gap);

            if (residue < 0) { residue += item.Gap; }
            if (residue == item.Residue) { return symbol; }
        }
        throw new InvalidOperationException(message: "the residue classes do not cover the integer");
    }

    /// <summary>Rechecks disjointness, coverage, and least-period claims.</summary>
    public bool Verify() {
        if ((Period <= 0) || (m_classes.Length == 0)) { return false; }
        var leastPeriod = 1;
        var distinct = new HashSet<ConstantGapClass>();

        foreach (var item in m_classes) {
            if ((item.Gap <= 0) || (item.Residue < 0) || (item.Residue >= item.Gap) ||
                ((Period % item.Gap) != 0) || !distinct.Add(item: item)) {
                return false;
            }
            leastPeriod = LeastCommonMultiple(left: leastPeriod, right: item.Gap);
        }
        if (leastPeriod != Period) { return false; }
        for (var position = 0; (position < Period); ++position) {
            var owners = 0;

            foreach (var item in m_classes) {
                if ((position % item.Gap) == item.Residue) { ++owners; }
            }
            if (owners != 1) { return false; }
        }
        return true;
    }

    // Deliberately not BinaryIntegerFunctions.LeastCommonMultiple, which WRAPS on overflow: a wrapped period here would
    // be silently wrong rather than loudly so, and the search has no other guard against it. The divisor is the shipped
    // one; only the overflow policy is local.
    private static int LeastCommonMultiple(int left, int right) =>
        checked(((left / left.GreatestCommonDivisor(other: right)) * right));
}

/// <summary>One attainable least period and an exact representative coloring.</summary>
public readonly record struct ConstantGapPeriodWitness(
    int Period,
    ConstantGapCovering Covering
);

/// <summary>
/// Exact-cover search for finite-alphabet constant-gap colorings.
/// </summary>
/// <remarks>
/// A coloring is represented as a disjoint exact covering by residue classes. Search branches on
/// the first uncovered point, so it explores residue-class sets without permutation duplicates.
/// The explicit period ceiling prevents accidental construction of enormous bit masks.
/// </remarks>
public static class ConstantGapCoveringResearch {
    /// <summary>Largest period accepted by the exact bit-mask search.</summary>
    public const int MaximumSearchPeriod = 4096;

    /// <summary>Returns the maximal power-of-two period conjectured for <paramref name="symbolCount"/> letters.</summary>
    public static BigInteger MaximalPeriod(int symbolCount) {
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolCount, 1);
        return (BigInteger.One << (symbolCount - 1));
    }

    /// <summary>Returns <c>3·2^(r−3)</c>, the non-maximal stability cutoff for <c>r ≥ 3</c>.</summary>
    public static BigInteger NonmaximalPeriodUpperBound(int symbolCount) {
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolCount, 3);
        return (3 * (BigInteger.One << (symbolCount - 3)));
    }

    /// <summary>Constructs the canonical ruler coloring on the requested number of letters.</summary>
    public static ConstantGapCovering CanonicalRuler(int symbolCount) {
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolCount, 1);
        if (symbolCount == 1) {
            return new ConstantGapCovering(1, [new ConstantGapClass(Gap: 1, Residue: 0)]);
        }
        var periodValue = MaximalPeriod(symbolCount: symbolCount);

        if (periodValue > MaximumSearchPeriod) {
            throw new ArgumentOutOfRangeException(
                nameof(symbolCount),
                symbolCount,
                $"the canonical period exceeds the explicit verification ceiling {MaximumSearchPeriod}"
            );
        }
        var period = (int)periodValue;
        var classes = new ConstantGapClass[symbolCount];

        for (var symbol = 0; (symbol < (symbolCount - 1)); ++symbol) {
            var gap = (1 << (symbol + 1));

            classes[symbol] = new ConstantGapClass(Gap: gap, Residue: ((gap / 2) - 1));
        }
        classes[^1] = new ConstantGapClass(Gap: period, Residue: (period - 1));
        var result = new ConstantGapCovering(classes: classes, period: period);

        if (!result.Verify()) {
            throw new InvalidOperationException(message: "the canonical ruler construction did not verify");
        }
        return result;
    }

    /// <summary>Finds one coloring with exactly the requested least period, if one exists.</summary>
    public static bool TryFind(
        int symbolCount,
        int leastPeriod,
        out ConstantGapCovering? covering
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(leastPeriod, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(leastPeriod, MaximumSearchPeriod);
        if (symbolCount > leastPeriod) {
            covering = null;
            return false;
        }

        var divisors = Divisors(value: leastPeriod);
        var all = ((BigInteger.One << leastPeriod) - BigInteger.One);
        var chosen = new List<ConstantGapClass>(capacity: symbolCount);
        var failed = new HashSet<SearchState>();
        var densityFailed = new HashSet<DensityState>();
        var masks = new Dictionary<ConstantGapClass, BigInteger>();

        if (!DensityCanComplete(currentLcm: 1, remaining: leastPeriod, slots: symbolCount)) {
            covering = null;
            return false;
        }
        if (!Search(covered: BigInteger.Zero, currentLcm: 1, slots: symbolCount)) {
            covering = null;
            return false;
        }

        covering = new ConstantGapCovering(classes: [.. chosen], period: leastPeriod);
        if (!covering.Verify()) {
            throw new InvalidOperationException(message: "the exact-cover search emitted an invalid witness");
        }
        return true;

        bool Search(BigInteger covered, int slots, int currentLcm) {
            if (slots == 0) { return ((covered == all) && (currentLcm == leastPeriod)); }
            if (covered == all) { return false; }
            var remainingCount = (int)BigInteger.PopCount(value: all ^ covered);

            if (remainingCount < slots) { return false; }
            if (!DensityCanComplete(currentLcm: currentLcm, remaining: remainingCount, slots: slots)) { return false; }
            var state = new SearchState(Covered: covered, Lcm: currentLcm, Slots: slots);

            if (failed.Contains(item: state)) { return false; }

            var first = FirstUnset(covered: covered, period: leastPeriod);

            foreach (var gap in divisors) {
                var item = new ConstantGapClass(Gap: gap, Residue: (first % gap));

                if (!masks.TryGetValue(key: item, value: out var mask)) {
                    mask = ClassMask(item: item, period: leastPeriod);
                    masks[item] = mask;
                }
                if (!(mask & covered).IsZero) { continue; }
                var nextLcm = LeastCommonMultiple(left: currentLcm, right: gap);

                if ((leastPeriod % nextLcm) != 0) { continue; }
                chosen.Add(item: item);
                if (Search(covered: covered | mask, currentLcm: nextLcm, slots: (slots - 1))) { return true; }
                chosen.RemoveAt(index: (chosen.Count - 1));
            }
            failed.Add(item: state);
            return false;
        }

        bool DensityCanComplete(int slots, int remaining, int currentLcm) {
            if (slots == 0) {
                return ((remaining == 0) && (currentLcm == leastPeriod));
            }
            if ((remaining < slots) || (remaining > (slots * leastPeriod))) {
                return false;
            }
            var state = new DensityState(Lcm: currentLcm, Remaining: remaining, Slots: slots);

            if (densityFailed.Contains(item: state)) { return false; }
            foreach (var gap in divisors) {
                var classSize = (leastPeriod / gap);

                if (classSize > remaining) { continue; }
                var nextLcm = LeastCommonMultiple(left: currentLcm, right: gap);

                if (DensityCanComplete(currentLcm: nextLcm, remaining: (remaining - classSize), slots: (slots - 1))) {
                    return true;
                }
            }
            densityFailed.Add(item: state);
            return false;
        }
    }

    /// <summary>Finds every attainable least period through an inclusive finite limit.</summary>
    public static IReadOnlyList<ConstantGapPeriodWitness> PeriodSpectrum(
        int symbolCount,
        int inclusiveMaximumPeriod
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(inclusiveMaximumPeriod, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            inclusiveMaximumPeriod,
            MaximumSearchPeriod
        );
        var result = new List<ConstantGapPeriodWitness>();

        for (var period = 1; (period <= inclusiveMaximumPeriod); ++period) {
            if (TryFind(covering: out var covering, leastPeriod: period, symbolCount: symbolCount)) {
                result.Add(item: new ConstantGapPeriodWitness(Covering: covering!, Period: period));
            }
        }
        return result;
    }

    private static int[] Divisors(int value) {
        var low = new List<int>();
        var high = new List<int>();

        for (var divisor = 1; (((long)divisor * divisor) <= value); ++divisor) {
            if ((value % divisor) != 0) { continue; }
            low.Add(item: divisor);
            if (divisor != (value / divisor)) { high.Add(item: (value / divisor)); }
        }
        high.Reverse();
        low.AddRange(collection: high);
        return [.. low];
    }
    private static BigInteger ClassMask(ConstantGapClass item, int period) {
        var mask = BigInteger.Zero;

        for (var position = item.Residue; (position < period); position += item.Gap) {
            mask |= (BigInteger.One << position);
        }
        return mask;
    }
    private static int FirstUnset(BigInteger covered, int period) {
        for (var position = 0; (position < period); ++position) {
            if ((covered & (BigInteger.One << position)).IsZero) { return position; }
        }
        throw new InvalidOperationException(message: "the mask has no uncovered position");
    }

    // Deliberately not BinaryIntegerFunctions.LeastCommonMultiple, which WRAPS on overflow: a wrapped period here would
    // be silently wrong rather than loudly so, and the search has no other guard against it. The divisor is the shipped
    // one; only the overflow policy is local.
    private static int LeastCommonMultiple(int left, int right) =>
        checked(((left / left.GreatestCommonDivisor(other: right)) * right));

    private readonly record struct SearchState(BigInteger Covered, int Slots, int Lcm);
    private readonly record struct DensityState(int Slots, int Remaining, int Lcm);
}
