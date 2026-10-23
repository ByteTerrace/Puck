namespace Puck.World.Protocol;

/// <summary>Independent support masks for the numeric and button banks.</summary>
/// <param name="Axes">One bit per numeric lane.</param>
/// <param name="Buttons">One bit per button lane.</param>
public readonly record struct ChannelMask(uint Axes, ulong Buttons) {
    /// <summary>Every address in both banks.</summary>
    public static ChannelMask All => new(Axes: uint.MaxValue, Buttons: ulong.MaxValue);
    /// <summary>Whether neither bank contains an address.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => ((Axes == 0) && (Buttons == 0));

    /// <summary>Tests a compiled bank address.</summary>
    public bool Contains(int ordinal) => ((ordinal >= 0) && (ordinal < ChannelLimits.MaxChannels) &&
        ((ordinal < ChannelLimits.MaxAxes) ? ClosedBitset.Contains(bits: Axes, ordinal: ordinal) : ClosedBitset.Contains(bits: Buttons, ordinal: (ordinal - ChannelLimits.MaxAxes))));
    /// <summary>Adds a compiled bank address.</summary>
    public ChannelMask With(int ordinal) {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, ChannelLimits.MaxChannels);
        return ((ordinal < ChannelLimits.MaxAxes)
            ? this with { Axes = ((uint)ClosedBitset.With(bits: Axes, ordinal: ordinal)) }
            : this with { Buttons = ClosedBitset.With(bits: Buttons, ordinal: (ordinal - ChannelLimits.MaxAxes)) });
    }

    /// <summary>Intersects each bank independently.</summary>
    public static ChannelMask operator &(ChannelMask a, ChannelMask b) => new(Axes: a.Axes & b.Axes, Buttons: a.Buttons & b.Buttons);
    /// <summary>Unites each bank independently.</summary>
    public static ChannelMask operator |(ChannelMask a, ChannelMask b) => new(Axes: a.Axes | b.Axes, Buttons: a.Buttons | b.Buttons);
    /// <summary>Complements each bank independently.</summary>
    public static ChannelMask operator ~(ChannelMask a) => new(Axes: ~a.Axes, Buttons: ~a.Buttons);
}
