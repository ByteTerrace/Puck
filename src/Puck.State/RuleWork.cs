using Puck.Maths;

namespace Puck.State;

/// <summary>A bound on authored work in heuristic work units: a known non-negative count, a reachable operation
/// nothing prices, or a count no <see cref="long"/> holds. The arithmetic is <see cref="CostBound"/>'s; the type is
/// separate because a work unit is not a reference cycle, and the two never add.</summary>
public readonly record struct RuleWork {
    private readonly CostBound m_bound;

    private RuleWork(CostBound bound) {
        m_bound = bound;
    }

    /// <summary>Gets a bound no <see cref="long"/> holds.</summary>
    public static RuleWork Overflow { get; } = new(bound: CostBound.Overflow);
    /// <summary>Gets zero work units.</summary>
    public static RuleWork Zero { get; } = new(bound: CostBound.Zero);

    /// <summary>Gets a value indicating whether the bound is a known count.</summary>
    public bool IsKnown => m_bound.IsKnown;
    /// <summary>Gets a value indicating whether the count overflowed.</summary>
    public bool IsOverflow => m_bound.IsOverflow;
    /// <summary>Gets a value indicating whether a reachable operation has no price.</summary>
    public bool IsUnmodeled => m_bound.IsUnmodeled;
    /// <summary>Gets what has no price when <see cref="IsUnmodeled"/>; otherwise <see langword="null"/>.</summary>
    public string? Reason => m_bound.Reason;
    /// <summary>Gets the work units of a known bound.</summary>
    /// <exception cref="InvalidOperationException">The bound is unmodeled or overflowed, so no number states it.</exception>
    public long Units => (IsKnown
        ? m_bound.Cycles
        : throw new InvalidOperationException(message: $"A work bound that is {this} has no units.")
    );

    /// <summary>Orders two bounds by how much they could cost: a known count by its units, beneath an unmodeled
    /// bound, beneath an overflow.</summary>
    /// <param name="left">The left bound.</param>
    /// <param name="right">The right bound.</param>
    /// <returns>Negative, zero, or positive as <paramref name="left"/> orders before, with, or after
    /// <paramref name="right"/>.</returns>
    public static int Compare(RuleWork left, RuleWork right) {
        var byKind = ((byte)left.m_bound.Kind).CompareTo(value: ((byte)right.m_bound.Kind));

        return ((byKind != 0)
            ? byKind
            : left.m_bound.Cycles.CompareTo(value: right.m_bound.Cycles)
        );
    }
    /// <summary>Creates a known bound; a negative count is an overflow.</summary>
    /// <param name="units">The work units.</param>
    /// <returns>The bound.</returns>
    public static RuleWork Known(long units) => new(bound: CostBound.Known(cycles: units));
    /// <summary>Takes the costlier of two alternatives that cannot both execute.</summary>
    /// <param name="left">One alternative.</param>
    /// <param name="right">The other.</param>
    /// <returns>The costlier bound; overflow first, then the leftmost unmodeled reason.</returns>
    public static RuleWork Max(RuleWork left, RuleWork right) => new(bound: CostBound.Max(
        left: left.m_bound,
        right: right.m_bound
    ));
    /// <summary>Creates a bound for a reachable operation nothing prices.</summary>
    /// <param name="reason">What has no price.</param>
    /// <returns>The bound.</returns>
    public static RuleWork Unmodeled(string reason) => new(bound: CostBound.Unmodeled(reason: reason));
    /// <summary>Returns whether the bound is known and within a ceiling. An unmodeled or overflowed bound fits no
    /// ceiling, <see cref="long.MaxValue"/> included.</summary>
    /// <param name="ceiling">The ceiling, in work units.</param>
    /// <returns><see langword="true"/> when the work is known to fit.</returns>
    public bool Fits(long ceiling) => (IsKnown && (Units <= ceiling));
    /// <inheritdoc/>
    public override string ToString() => m_bound.Kind switch {
        CostBoundKind.Known => m_bound.Cycles.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
        CostBoundKind.Unmodeled => $"unmodeled: {Reason}",
        _ => "overflow",
    };

    /// <summary>Reads a known count of work units.</summary>
    /// <param name="units">The work units; a negative count is an overflow.</param>
    public static implicit operator RuleWork(long units) => Known(units: units);
    public static RuleWork operator +(RuleWork left, RuleWork right) => new(bound: (left.m_bound + right.m_bound));
    public static RuleWork operator *(RuleWork left, long right) => new(bound: (left.m_bound * right));
    public static RuleWork operator *(long left, RuleWork right) => new(bound: (left * right.m_bound));
}
