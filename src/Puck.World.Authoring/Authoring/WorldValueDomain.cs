using System.Globalization;

namespace Puck.World.Authoring;

/// <summary>
/// The numbers a presentation field admits: an interval whose either end is closed or open, or unbounded. One domain
/// serves the validators, which refuse an authored or bound starting value outside it, and the presentation, which
/// maps a bound value that moves outside it back in with <see cref="Map"/>: a clamp, or where no clamp is admissible, a
/// hold of the binding's last valid value. A world field's domain is its row of
/// <c>WorldValueFields</c>.
/// </summary>
/// <param name="Minimum">The lower end, or negative infinity for none.</param>
/// <param name="Maximum">The upper end, or positive infinity for none.</param>
/// <param name="MinimumOpen">Whether <paramref name="Minimum"/> itself lies outside the domain.</param>
/// <param name="MaximumOpen">Whether <paramref name="Maximum"/> itself lies outside the domain.</param>
public readonly record struct WorldValueDomain(float Minimum, float Maximum, bool MinimumOpen = false, bool MaximumOpen = false) {
    /// <summary>Gets every finite number.</summary>
    public static WorldValueDomain Finite { get; } = new(
        Maximum: float.PositiveInfinity,
        Minimum: float.NegativeInfinity
    );
    /// <summary>Gets every finite number at or above zero.</summary>
    public static WorldValueDomain NonNegative { get; } = new(
        Maximum: float.PositiveInfinity,
        Minimum: 0f
    );
    /// <summary>Gets every finite number above zero.</summary>
    public static WorldValueDomain Positive { get; } = new(
        Maximum: float.PositiveInfinity,
        Minimum: 0f,
        MinimumOpen: true
    );
    /// <summary>Gets the closed unit interval.</summary>
    public static WorldValueDomain Unit { get; } = new(
        Maximum: 1f,
        Minimum: 0f
    );

    /// <summary>Gets whether the domain admits less than every finite number.</summary>
    public bool IsRestricted => ((Lowest > float.MinValue) || (Highest < float.MaxValue));
    /// <summary>Gets the smallest number the domain admits: its closed lower end, the next float above an open one,
    /// or the most negative finite float when it has none.</summary>
    public float Lowest => (float.IsNegativeInfinity(f: Minimum)
        ? float.MinValue
        : (MinimumOpen
            ? MathF.BitIncrement(x: Minimum)
            : Minimum));
    /// <summary>Gets the largest number the domain admits: its closed upper end, the next float below an open one,
    /// or the largest finite float when it has none.</summary>
    public float Highest => (float.IsPositiveInfinity(f: Maximum)
        ? float.MaxValue
        : (MaximumOpen
            ? MathF.BitDecrement(x: Maximum)
            : Maximum));

    /// <summary>Returns whether the domain admits a number.</summary>
    /// <param name="value">The number.</param>
    /// <returns><see langword="true"/> when it is finite and lies within both ends.</returns>
    public bool Contains(float value) => (
        float.IsFinite(f: value) &&
        (value >= Lowest) &&
        (value <= Highest)
    );
    /// <summary>Maps a number into the domain, as a pure function of the number alone: a number inside is returned as
    /// it is, and a finite number beyond a closed end becomes that end. A number that is not finite, or lies at or
    /// beyond an open end, has no admissible nearest number, so the outcome is a hold: the presentation keeps the last
    /// value the binding presented from a valid one.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The clamped number, or a hold.</returns>
    public WorldValueMapping Map(float value) => ((
        !float.IsFinite(f: value) ||
        (MinimumOpen && (value <= Minimum)) ||
        (MaximumOpen && (value >= Maximum))
    )
        ? WorldValueMapping.Hold
        : new WorldValueMapping(
            Holds: false,
            Value: Math.Clamp(
                max: Maximum,
                min: Minimum,
                value: value
            )
        ));
    /// <inheritdoc/>
    public override string ToString() => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"{((MinimumOpen || float.IsNegativeInfinity(f: Minimum)) ? '(' : '[')}{(float.IsNegativeInfinity(f: Minimum) ? "-inf" : Minimum.ToString(provider: CultureInfo.InvariantCulture))}, {(float.IsPositiveInfinity(f: Maximum) ? "inf" : Maximum.ToString(provider: CultureInfo.InvariantCulture))}{((MaximumOpen || float.IsPositiveInfinity(f: Maximum)) ? ')' : ']')}"
    );
}
/// <summary>What <see cref="WorldValueDomain.Map"/> makes of a number: a number the domain admits, or a hold.</summary>
/// <param name="Holds">Whether the number has no admissible nearest number, so the binding holds its last valid
/// value.</param>
/// <param name="Value">The number the domain admits; zero, and meaningless, when <paramref name="Holds"/>.</param>
public readonly record struct WorldValueMapping(bool Holds, float Value) {
    /// <summary>Gets the outcome that keeps the last valid value.</summary>
    public static WorldValueMapping Hold { get; } = new(
        Holds: true,
        Value: 0f
    );
}
