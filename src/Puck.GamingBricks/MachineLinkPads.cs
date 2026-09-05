using System.Runtime.CompilerServices;
using Puck.Abstractions.Machines;

namespace Puck.GamingBricks;

/// <summary>
/// One cable-linked group's per-seat controller images for a single step — the held-input record a
/// <see cref="LinkedMachineGroup"/>'s time-travel ring replays verbatim. Seat <c>i</c> is the image handed to member
/// <c>i</c> in cable order, so a multiplayer link routes per-seat input through the same replay surface a single
/// machine's <see cref="MachinePadState"/> does. Unused seats hold the neutral image.
/// </summary>
[InlineArray(length: MaxMembers)]
public struct MachineLinkPads {
    /// <summary>The largest number of seats one cable-linked group carries — the widest link the shipped machine
    /// families model.</summary>
    public const int MaxMembers = 4;

    private MachinePadState m_pad;

    /// <summary>Builds a seat image from a host's cable-ordered controller span, leaving unused seats neutral.</summary>
    /// <param name="inputs">The per-member controller images, in cable order.</param>
    /// <returns>The packed seat image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputs"/> holds more than
    /// <see cref="MaxMembers"/> entries.</exception>
    public static MachineLinkPads From(ReadOnlySpan<MachinePadState> inputs) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: MaxMembers,
            value: inputs.Length
        );

        var pads = default(MachineLinkPads);

        for (var index = 0; (index < inputs.Length); ++index) {
            pads[index] = inputs[index];
        }

        return pads;
    }
}
