using System.Runtime.CompilerServices;

namespace Puck.Input.Devices;

/// <summary>
/// A fixed, allocation-free buffer of one engine-tick stamp per <see cref="GamepadButtons"/> bit, indexed by bit
/// position (<see cref="System.Numerics.BitOperations.TrailingZeroCount(uint)"/> of the single-bit flag). The
/// coalescer records the arrival time of each button's <em>first</em> press within a frame window here, so the
/// snapshot capture can stamp every press edge with its true sub-frame time rather than one shared frame value.
/// A zero slot means the button did not press this window.
/// </summary>
[InlineArray(length: Count)]
public struct GamepadButtonEdges : IEquatable<GamepadButtonEdges> {
    /// <summary>
    /// The number of button bits in <see cref="GamepadButtons"/>; one stamp slot per bit. KEEP IN SYNC with the
    /// highest <see cref="GamepadButtons"/> flag (currently <see cref="GamepadButtons.TouchpadLeft"/> = bit 22):
    /// a flag past this count makes the coalescer's edge stamp throw on the device I/O thread and fault the pad
    /// on every fresh press of that button — found on real hardware by a triple-press validation pass.
    /// </summary>
    public const int Count = 23;

    private ulong m_element0;

    /// <summary>Indicates whether this buffer holds the same per-button stamps as <paramref name="other"/>.</summary>
    /// <param name="other">The buffer to compare against.</param>
    /// <returns><see langword="true"/> when every slot is equal; otherwise <see langword="false"/>.</returns>
    public readonly bool Equals(GamepadButtonEdges other) {
        return ((ReadOnlySpan<ulong>)this).SequenceEqual(other: (ReadOnlySpan<ulong>)other);
    }
    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) {
        return ((obj is GamepadButtonEdges other) && Equals(other: other));
    }
    /// <inheritdoc/>
    public readonly override int GetHashCode() {
        var hash = new HashCode();

        for (var index = 0; (index < Count); index++) {
            hash.Add(value: this[index]);
        }

        return hash.ToHashCode();
    }
}
