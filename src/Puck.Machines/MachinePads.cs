using System.Runtime.CompilerServices;
using Puck.Abstractions.Machines;

namespace Puck.Machines;

/// <summary>
/// One step's controller images, one per seat — the held-input record the time-travel ring replays verbatim. A
/// multi-port machine's seat is one of its controller ports, in the order its host declares them; a cable-linked
/// group's seat is one member, in cable order. Seats a machine or group does not use hold the neutral image.
/// <para>
/// Equality compares every seat. The runtime supports no value equality on an inline array of its own, so the
/// runahead predictor's comparison of held images depends on this type's <see cref="Equals(MachinePads)"/>.
/// </para>
/// </summary>
[InlineArray(length: MaxSeats)]
public struct MachinePads : IEquatable<MachinePads> {
    /// <summary>The largest number of seats one image carries — the widest link and the most controller ports the
    /// shipped machine families model.</summary>
    public const int MaxSeats = 4;

    private MachinePadState m_pad;

    /// <summary>Gets the seat image in which every seat holds <see cref="MachinePadState.Neutral"/>.</summary>
    public static MachinePads Neutral => default;

    /// <summary>Builds a seat image from a seat-ordered controller span, leaving the remaining seats neutral.</summary>
    /// <param name="inputs">The per-seat controller images, in seat order.</param>
    /// <returns>The packed seat image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputs"/> holds more than
    /// <see cref="MaxSeats"/> entries.</exception>
    public static MachinePads From(ReadOnlySpan<MachinePadState> inputs) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: MaxSeats,
            value: inputs.Length
        );

        var pads = Neutral;

        for (var index = 0; (index < inputs.Length); ++index) {
            pads[index] = inputs[index];
        }

        return pads;
    }
    /// <summary>Builds a seat image whose first seat holds <paramref name="input"/> and whose other seats are
    /// neutral — the image of a single-port machine.</summary>
    /// <param name="input">The first seat's controller image.</param>
    /// <returns>The packed seat image.</returns>
    public static MachinePads One(in MachinePadState input) {
        var pads = Neutral;

        pads[0] = input;

        return pads;
    }
    /// <summary>Returns whether two seat images hold equal pads in every seat.</summary>
    /// <param name="left">The first image.</param>
    /// <param name="right">The second image.</param>
    /// <returns><see langword="true"/> when every seat's pad is equal.</returns>
    public static bool operator ==(MachinePads left, MachinePads right) =>
        left.Equals(other: right);
    /// <summary>Returns whether two seat images differ in any seat.</summary>
    /// <param name="left">The first image.</param>
    /// <param name="right">The second image.</param>
    /// <returns><see langword="true"/> when some seat's pad differs.</returns>
    public static bool operator !=(MachinePads left, MachinePads right) =>
        !left.Equals(other: right);

    /// <summary>Returns whether <paramref name="other"/> holds an equal pad in every seat.</summary>
    /// <param name="other">The image to compare.</param>
    /// <returns><see langword="true"/> when every seat's pad is equal.</returns>
    public readonly bool Equals(MachinePads other) {
        for (var seat = 0; (seat < MaxSeats); ++seat) {
            if (!this[seat].Equals(other: other[seat])) {
                return false;
            }
        }

        return true;
    }
    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) =>
        ((obj is MachinePads other) && Equals(other: other));
    /// <inheritdoc/>
    public override readonly int GetHashCode() {
        var hash = new HashCode();

        for (var seat = 0; (seat < MaxSeats); ++seat) {
            hash.Add(value: this[seat]);
        }

        return hash.ToHashCode();
    }
}
