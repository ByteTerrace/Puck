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
        m_readOnlyClasses = Array.AsReadOnly(m_classes);
    }

    /// <summary>Gets the least global period of the coloring.</summary>
    public int Period { get; }
    /// <summary>Gets the number of distinct letters.</summary>
    public int SymbolCount => m_classes.Length;
    /// <summary>Gets the residue class belonging to each dense symbol index.</summary>
    public IReadOnlyList<ConstantGapClass> Classes => m_readOnlyClasses;

    /// <summary>Returns the symbol at any signed integer index.</summary>
    public int SymbolAt(BigInteger index) {
        for (var symbol = 0; symbol < m_classes.Length; ++symbol) {
            var item = m_classes[symbol];
            var residue = (int)(index % item.Gap);
            if (residue < 0) { residue += item.Gap; }
            if (residue == item.Residue) { return symbol; }
        }
        throw new InvalidOperationException("the residue classes do not cover the integer");
    }

    /// <summary>Rechecks disjointness, coverage, and least-period claims.</summary>
    public bool Verify() {
        if ((Period <= 0) || (m_classes.Length == 0)) { return false; }
        var leastPeriod = 1;
        var distinct = new HashSet<ConstantGapClass>();
        foreach (var item in m_classes) {
            if ((item.Gap <= 0) || (item.Residue < 0) || (item.Residue >= item.Gap) ||
                ((Period % item.Gap) != 0) || !distinct.Add(item)) {
                return false;
            }
            leastPeriod = LeastCommonMultiple(leastPeriod, item.Gap);
        }
        if (leastPeriod != Period) { return false; }
        for (var position = 0; position < Period; ++position) {
            var owners = 0;
            foreach (var item in m_classes) {
                if ((position % item.Gap) == item.Residue) { ++owners; }
            }
            if (owners != 1) { return false; }
        }
        return true;
    }

    private static int LeastCommonMultiple(int left, int right) =>
        checked((left / GreatestCommonDivisor(left, right)) * right);

    private static int GreatestCommonDivisor(int left, int right) {
        while (right != 0) { (left, right) = (right, left % right); }
        return Math.Abs(left);
    }
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
            return new ConstantGapCovering(1, [new ConstantGapClass(1, 0)]);
        }
        var periodValue = MaximalPeriod(symbolCount);
        if (periodValue > MaximumSearchPeriod) {
            throw new ArgumentOutOfRangeException(
                nameof(symbolCount),
                symbolCount,
                $"the canonical period exceeds the explicit verification ceiling {MaximumSearchPeriod}"
            );
        }
        var period = (int)periodValue;
        var classes = new ConstantGapClass[symbolCount];
        for (var symbol = 0; symbol < (symbolCount - 1); ++symbol) {
            var gap = (1 << (symbol + 1));
            classes[symbol] = new ConstantGapClass(gap, (gap / 2) - 1);
        }
        classes[^1] = new ConstantGapClass(period, period - 1);
        var result = new ConstantGapCovering(period, classes);
        if (!result.Verify()) {
            throw new InvalidOperationException("the canonical ruler construction did not verify");
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

        var divisors = Divisors(leastPeriod);
        var all = (BigInteger.One << leastPeriod) - BigInteger.One;
        var chosen = new List<ConstantGapClass>(symbolCount);
        var failed = new HashSet<SearchState>();
        var densityFailed = new HashSet<DensityState>();
        var masks = new Dictionary<ConstantGapClass, BigInteger>();
        if (!DensityCanComplete(symbolCount, leastPeriod, 1)) {
            covering = null;
            return false;
        }
        if (!Search(BigInteger.Zero, symbolCount, 1)) {
            covering = null;
            return false;
        }

        covering = new ConstantGapCovering(leastPeriod, [.. chosen]);
        if (!covering.Verify()) {
            throw new InvalidOperationException("the exact-cover search emitted an invalid witness");
        }
        return true;

        bool Search(BigInteger covered, int slots, int currentLcm) {
            if (slots == 0) { return (covered == all) && (currentLcm == leastPeriod); }
            if (covered == all) { return false; }
            var remainingCount = CountBits(all ^ covered);
            if (remainingCount < slots) { return false; }
            if (!DensityCanComplete(slots, remainingCount, currentLcm)) { return false; }
            var state = new SearchState(covered, slots, currentLcm);
            if (failed.Contains(state)) { return false; }

            var first = FirstUnset(covered, leastPeriod);
            foreach (var gap in divisors) {
                var item = new ConstantGapClass(gap, first % gap);
                if (!masks.TryGetValue(item, out var mask)) {
                    mask = ClassMask(item, leastPeriod);
                    masks[item] = mask;
                }
                if (!(mask & covered).IsZero) { continue; }
                var nextLcm = LeastCommonMultiple(currentLcm, gap);
                if ((leastPeriod % nextLcm) != 0) { continue; }
                chosen.Add(item);
                if (Search(covered | mask, slots - 1, nextLcm)) { return true; }
                chosen.RemoveAt(chosen.Count - 1);
            }
            failed.Add(state);
            return false;
        }

        bool DensityCanComplete(int slots, int remaining, int currentLcm) {
            if (slots == 0) {
                return (remaining == 0) && (currentLcm == leastPeriod);
            }
            if ((remaining < slots) || (remaining > (slots * leastPeriod))) {
                return false;
            }
            var state = new DensityState(slots, remaining, currentLcm);
            if (densityFailed.Contains(state)) { return false; }
            foreach (var gap in divisors) {
                var classSize = (leastPeriod / gap);
                if (classSize > remaining) { continue; }
                var nextLcm = LeastCommonMultiple(currentLcm, gap);
                if (DensityCanComplete(slots - 1, remaining - classSize, nextLcm)) {
                    return true;
                }
            }
            densityFailed.Add(state);
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
        for (var period = 1; period <= inclusiveMaximumPeriod; ++period) {
            if (TryFind(symbolCount, period, out var covering)) {
                result.Add(new ConstantGapPeriodWitness(period, covering!));
            }
        }
        return result;
    }

    private static int[] Divisors(int value) {
        var low = new List<int>();
        var high = new List<int>();
        for (var divisor = 1; ((long)divisor * divisor) <= value; ++divisor) {
            if ((value % divisor) != 0) { continue; }
            low.Add(divisor);
            if (divisor != (value / divisor)) { high.Add(value / divisor); }
        }
        high.Reverse();
        low.AddRange(high);
        return [.. low];
    }

    private static BigInteger ClassMask(ConstantGapClass item, int period) {
        var mask = BigInteger.Zero;
        for (var position = item.Residue; position < period; position += item.Gap) {
            mask |= (BigInteger.One << position);
        }
        return mask;
    }

    private static int FirstUnset(BigInteger covered, int period) {
        for (var position = 0; position < period; ++position) {
            if ((covered & (BigInteger.One << position)).IsZero) { return position; }
        }
        throw new InvalidOperationException("the mask has no uncovered position");
    }

    private static int CountBits(BigInteger value) {
        var count = 0;
        foreach (var item in value.ToByteArray(isUnsigned: true, isBigEndian: false)) {
            count += BitOperations.PopCount(item);
        }
        return count;
    }

    private static int LeastCommonMultiple(int left, int right) =>
        checked((left / GreatestCommonDivisor(left, right)) * right);

    private static int GreatestCommonDivisor(int left, int right) {
        while (right != 0) { (left, right) = (right, left % right); }
        return Math.Abs(left);
    }

    private readonly record struct SearchState(BigInteger Covered, int Slots, int Lcm);
    private readonly record struct DensityState(int Slots, int Remaining, int Lcm);
}
