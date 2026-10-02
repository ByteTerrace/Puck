using System.Globalization;

namespace Puck.World.Authoring;

/// <summary>
/// The numbers a presentation field admits: an interval whose either end is closed or open, or unbounded. One domain
/// serves the validators, which refuse an authored or bound starting value outside it, and the presentation, which
/// maps a bound value that moves outside it back in with <see cref="Clamp"/>. A world field's domain is its row of
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
    /// <summary>Maps a number into the domain, as a pure function of the number alone: a number inside is returned
    /// as it is, a number below <see cref="Lowest"/> (or not a number) becomes <see cref="Lowest"/>, and a number above
    /// <see cref="Highest"/> becomes <see cref="Highest"/>. A closed end clamps to itself; an open end clamps to the
    /// nearest float inside it.</summary>
    /// <param name="value">The number.</param>
    /// <returns>A number the domain admits.</returns>
    public float Clamp(float value) => ((value >= Lowest)
        ? MathF.Min(
            x: value,
            y: Highest
        )
        : Lowest);
    /// <inheritdoc/>
    public override string ToString() => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"{((MinimumOpen || float.IsNegativeInfinity(f: Minimum)) ? '(' : '[')}{(float.IsNegativeInfinity(f: Minimum) ? "-inf" : Minimum.ToString(provider: CultureInfo.InvariantCulture))}, {(float.IsPositiveInfinity(f: Maximum) ? "inf" : Maximum.ToString(provider: CultureInfo.InvariantCulture))}{((MaximumOpen || float.IsPositiveInfinity(f: Maximum)) ? ')' : ']')}"
    );
}
