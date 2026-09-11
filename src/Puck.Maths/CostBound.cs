namespace Puck.Maths;

/// <summary>The state of a cost calculation: a known non-negative cycle count, an unmodeled operation reason, or integer overflow.</summary>
public enum CostBoundKind : byte {
    /// <summary>A known non-negative cycle count; calibration evidence belongs to its owning schedule.</summary>
    Known = 0,
    /// <summary>A reachable operation or kernel that is not yet modeled.</summary>
    Unmodeled = 1,
    /// <summary>Arithmetic overflow during cost accumulation.</summary>
    Overflow = 2,
}

/// <summary>An exact cost bound in reference cycles, or an explicit unmodeled/overflow result.</summary>
public readonly record struct CostBound {
    /// <summary>Gets the kind of bound.</summary>
    public CostBoundKind Kind { get; }
    /// <summary>Gets the non-negative cycle count when <see cref="Kind"/> is <see cref="CostBoundKind.Known"/>; otherwise 0.</summary>
    public long Cycles { get; }
    /// <summary>Gets the reason when <see cref="Kind"/> is <see cref="CostBoundKind.Unmodeled"/>; otherwise null.</summary>
    public string? Reason { get; }

    /// <summary>Gets a value indicating whether this bound is a known cycle count.</summary>
    public bool IsKnown => (Kind == CostBoundKind.Known);
    /// <summary>Gets a value indicating whether this bound represents an unmodeled operation.</summary>
    public bool IsUnmodeled => (Kind == CostBoundKind.Unmodeled);
    /// <summary>Gets a value indicating whether this bound represents arithmetic overflow.</summary>
    public bool IsOverflow => (Kind == CostBoundKind.Overflow);

    private CostBound(CostBoundKind kind, long cycles, string? reason) {
        Kind = kind;
        Cycles = cycles;
        Reason = reason;
    }

    /// <summary>Initializes a new known cost bound.</summary>
    public CostBound(long cycles) : this(
        kind: ((cycles < 0L) ? CostBoundKind.Overflow : CostBoundKind.Known),
        cycles: Math.Max(0L, cycles),
        reason: null
    ) { }

    /// <summary>Zero reference cycles.</summary>
    public static CostBound Zero { get; } = new(0L);
    /// <summary>An overflowed cost bound.</summary>
    public static CostBound Overflow { get; } = new(CostBoundKind.Overflow, 0L, null);

    /// <summary>Creates an unmodeled bound with the supplied reason.</summary>
    public static CostBound Unmodeled(string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(CostBoundKind.Unmodeled, 0L, reason);
    }
    /// <summary>Creates a known non-negative cycle bound.</summary>
    public static CostBound Known(long cycles) => new(cycles);

    /// <summary>Adds two cost bounds, propagating overflow and unmodeled status.</summary>
    public static CostBound Add(CostBound left, CostBound right) {
        if (left.IsOverflow || right.IsOverflow) {
            return Overflow;
        }
        if (left.IsUnmodeled) {
            return left;
        }
        if (right.IsUnmodeled) {
            return right;
        }
        if (left.Cycles > (long.MaxValue - right.Cycles)) {
            return Overflow;
        }
        return new(left.Cycles + right.Cycles);
    }

    /// <summary>Multiplies a cost bound by a non-negative multiplier. A proved zero multiplier eliminates unmodeled or overflow status.</summary>
    public static CostBound Multiply(CostBound bound, long multiplier) {
        if (multiplier == 0L) {
            return Zero;
        }
        if ((multiplier < 0L) || bound.IsOverflow) {
            return Overflow;
        }
        if (bound.IsUnmodeled) {
            return bound;
        }
        if (bound.Cycles > (long.MaxValue / multiplier)) {
            return Overflow;
        }
        return new(bound.Cycles * multiplier);
    }

    /// <summary>Takes the maximum of two cost bounds.</summary>
    public static CostBound Max(CostBound left, CostBound right) {
        if (left.IsOverflow || right.IsOverflow) {
            return Overflow;
        }
        if (left.IsUnmodeled) {
            return left;
        }
        if (right.IsUnmodeled) {
            return right;
        }
        return new(Math.Max(val1: left.Cycles, val2: right.Cycles));
    }

    public static CostBound operator +(CostBound left, CostBound right) => Add(left: left, right: right);
    public static CostBound operator *(CostBound left, long right) => Multiply(bound: left, multiplier: right);
    public static CostBound operator *(long left, CostBound right) => Multiply(bound: right, multiplier: left);

    /// <summary>Returns the cycle count, clamping overflow to <see cref="long.MaxValue"/> and unmodeled to <see cref="long.MaxValue"/>.</summary>
    public long ToSaturatingCycles() => (IsKnown ? Cycles : long.MaxValue);

    /// <inheritdoc/>
    public override string ToString() => Kind switch {
        CostBoundKind.Known => $"{Cycles} cycles",
        CostBoundKind.Unmodeled => $"unmodeled: {Reason}",
        CostBoundKind.Overflow => "overflow",
        _ => "unknown",
    };
}
