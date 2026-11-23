using System.Collections.Immutable;
using Puck.Maths;

namespace Puck.Physics.Navigation;

/// <summary>One marked volume of a navigation world: every cell whose route point lies in <paramref name="Shape"/> takes
/// area type <paramref name="Type"/>, unless a later volume also holds it.</summary>
/// <param name="Type">The area type's ordinal, from zero below <see cref="NavigationAreaSet.TypeCount"/>.</param>
/// <param name="Shape">The closed world volume the area covers.</param>
public readonly record struct NavigationArea(int Type, FixedSpatialShape Shape);

/// <summary>The area types a runtime's cells carry: authored volumes in precedence order (the last volume holding a cell
/// decides its type), over each domain's own type for the cells no volume marks
/// (<see cref="NavigationDomainInput.Area"/>). Types are ordinals the host names; the kernel knows no area's meaning, only
/// what a <see cref="NavigationCostProfile"/> charges for it.</summary>
/// <remarks>Equal sets mark every cell alike, so a runtime replacing another keeps its goal fields only while the two
/// sets are equal.</remarks>
public sealed class NavigationAreaSet : IEquatable<NavigationAreaSet> {
    /// <summary>The most area types one set names: a cell's type is held in a byte beside "none".</summary>
    public const int MaxTypes = 254;

    /// <summary>Gets a set of no types and no volumes: every cell is unmarked, and every profile charges it its length.</summary>
    public static NavigationAreaSet None { get; } = new(typeCount: 0, volumes: []);

    /// <summary>Builds a set.</summary>
    /// <param name="typeCount">The number of area types, at most <see cref="MaxTypes"/>.</param>
    /// <param name="volumes">The marked volumes in precedence order, each naming a type below
    /// <paramref name="typeCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count or a volume's type is out of range.</exception>
    public NavigationAreaSet(int typeCount, IEnumerable<NavigationArea> volumes) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: typeCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: typeCount, other: MaxTypes);
        TypeCount = typeCount;
        Volumes = [.. volumes];
        foreach (var volume in Volumes) {
            if (((uint)volume.Type) >= ((uint)typeCount)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(volumes), actualValue: volume.Type, message: "a navigation area names a type outside the set.");
            }
        }
    }

    /// <summary>Gets the number of area types.</summary>
    public int TypeCount { get; }
    /// <summary>Gets the marked volumes in precedence order.</summary>
    public ImmutableArray<NavigationArea> Volumes { get; }

    /// <inheritdoc/>
    public bool Equals(NavigationAreaSet? other) =>
        ((other is not null) && (ReferenceEquals(objA: this, objB: other) || ((TypeCount == other.TypeCount) && Volumes.AsSpan().SequenceEqual(other: other.Volumes.AsSpan()))));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(other: (obj as NavigationAreaSet));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(value1: TypeCount, value2: Volumes.Length);
}

/// <summary>What one kind of agent pays to cross each area type: a fixed-point multiplier on a step's cost for each type,
/// or the type's exclusion. A step costs what it costs without areas (its length and the clearance surcharge) times the
/// multiplier of the cell it steps onto; an unmarked cell and a type the profile does not name cost one. A route or a
/// goal field never steps onto a cell whose type the profile excludes; a body already standing on one may step off it.</summary>
/// <remarks>Profiles compare by their costs, so a goal field keyed by an equal profile is the same field. Fixed point
/// throughout: a scaled step rounds down once, so every machine charges the same.</remarks>
public sealed class NavigationCostProfile : IEquatable<NavigationCostProfile> {
    // Each type's multiplier as raw Q48.16 bits, Excluded for an excluded type, trailing ones trimmed so a profile that
    // names a type at cost one equals one that leaves it out.
    private const long Excluded = -1L;
    private readonly long[] m_scales;

    /// <summary>Gets the profile every area costs one under: what an agent with no profile pays.</summary>
    public static NavigationCostProfile Neutral { get; } = new(costs: []);

    /// <summary>Builds a profile.</summary>
    /// <param name="costs">Each area type's multiplier by type ordinal, positive, or <see langword="null"/> to exclude the
    /// type; a type past the list costs one.</param>
    /// <exception cref="ArgumentOutOfRangeException">A multiplier is not positive.</exception>
    public NavigationCostProfile(IReadOnlyList<FixedQ4816?> costs) {
        ArgumentNullException.ThrowIfNull(argument: costs);

        var scales = new long[costs.Count];
        var minimum = FixedQ4816.One;

        for (var type = 0; (type < scales.Length); type++) {
            if (costs[type] is not { } cost) {
                scales[type] = Excluded;
                continue;
            }
            if (cost <= FixedQ4816.Zero) {
                throw new ArgumentOutOfRangeException(paramName: nameof(costs), actualValue: cost, message: "an area cost multiplier must be positive.");
            }
            scales[type] = cost.Value;
            minimum = FixedQ4816.Min(x: minimum, y: cost);
        }

        var length = scales.Length;

        while ((length > 0) && (scales[(length - 1)] == FixedQ4816.One.Value)) {
            length--;
        }
        m_scales = scales[..length];
        Minimum = minimum;
    }

    /// <summary>Gets the least multiplier any step can pay, never more than one: an unmarked cell always costs one. A
    /// route search scales its distance heuristic by it, so the heuristic never exceeds the cost left.</summary>
    public FixedQ4816 Minimum { get; }
    /// <summary>Gets whether every area costs one and none is excluded.</summary>
    public bool IsNeutral => (m_scales.Length == 0);

    /// <summary>Gets whether the profile excludes an area type.</summary>
    /// <param name="type">The type's ordinal.</param>
    public bool Excludes(int type) => ((((uint)type) < ((uint)m_scales.Length)) && (m_scales[type] == Excluded));
    /// <summary>Gets an area type's multiplier: one for a type past the profile's costs, zero for an excluded type.</summary>
    /// <param name="type">The type's ordinal.</param>
    public FixedQ4816 Cost(int type) => ((((uint)type) >= ((uint)m_scales.Length))
        ? FixedQ4816.One
        : ((m_scales[type] == Excluded) ? FixedQ4816.Zero : FixedQ4816.FromRawBits(value: m_scales[type])));

    // A step's cost onto a cell of an area code (0 for an unmarked cell, the type plus one otherwise): the unscaled cost
    // times the type's multiplier, rounded down, or -1 when the profile excludes the type.
    internal long Scale(long cost, byte area) {
        var type = (area - 1);

        if (((uint)type) >= ((uint)m_scales.Length)) {
            return cost;
        }

        var scale = m_scales[type];

        return ((scale == Excluded) ? -1L : checked((long)((((Int128)cost) * scale) >> 16)));
    }
    // A distance heuristic scaled by the least multiplier, rounded down so it stays a lower bound.
    internal long ScaleHeuristic(long distance) => ((Minimum == FixedQ4816.One) ? distance : checked((long)((((Int128)distance) * Minimum.Value) >> 16)));

    /// <inheritdoc/>
    public bool Equals(NavigationCostProfile? other) =>
        ((other is not null) && (ReferenceEquals(objA: this, objB: other) || m_scales.AsSpan().SequenceEqual(other: other.m_scales)));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(other: (obj as NavigationCostProfile));
    /// <inheritdoc/>
    public override int GetHashCode() => SequenceHash.Of(elements: (ReadOnlySpan<long>)m_scales);
}
