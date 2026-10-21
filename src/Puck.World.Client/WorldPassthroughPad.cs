using System.Numerics;
using Puck.Commands;
using Puck.Input;

namespace Puck.World.Client;

/// <summary>
/// A local pad's sticks as a desktop pointer over an engaged window: the stick deflections a seat's bound pad reports,
/// held between reports, and their integration into pointer travel and wheel turns over each frame's interval
/// (<see cref="Advance"/>), shaped by the screen route's <see cref="WorldScreenPadPointer"/>. Presentation only: floats,
/// a frame's wall-clock interval, and no simulation state.
/// </summary>
/// <remarks>Window-pump-thread only, like the passthrough door that owns it (<see cref="WorldSourcePassthrough"/>).</remarks>
public sealed class WorldPassthroughPad {
    // The family every gamepad control's source id belongs to (InputSources.Gamepad).
    private const string GamepadPrefix = "gamepad.";

    // How long the pointer stick has been deflected without resting, in seconds: the boost ramp's clock.
    private double m_heldSeconds;

    /// <summary>Gets the 0-based local seat whose pad drove the pointer last, or -1 when none has.</summary>
    public int Slot { get; private set; } = -1;
    /// <summary>Gets the pointer stick's deflection, +X right and +Y up, its length at most 1.</summary>
    public Vector2 Stick { get; private set; }
    /// <summary>Gets the scrolling stick's deflection, +X right and +Y up (the wheel turning away from the user), its
    /// length at most 1.</summary>
    public Vector2 Scroll { get; private set; }

    /// <summary>Returns whether a command reached its handler from a local pad's own control: a binding's dispatch of a
    /// physical gamepad source a local device reported. A console line, an authored command, a wheel sector, a scripted
    /// <c>player.signal</c> (which reports no device) and a probe's axis (which is no gamepad source) are not.</summary>
    /// <param name="origin">The invocation's origin.</param>
    /// <param name="deviceId">The device that produced the activation.</param>
    /// <param name="source">The binding's physical source, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> for a local pad's bound control.</returns>
    public static bool IsLocalPad(CommandOrigin origin, InputDeviceId deviceId, string? source) => (
        (origin == CommandOrigin.Binding) &&
        (deviceId != default) &&
        (source is { } named) &&
        named.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: GamepadPrefix
        ) &&
        InputSourceVocabulary.IsKnownSourceId(sourceId: named)
    );
    // A deflection's length clamped to 1, and its direction.
    private static (float Length, Vector2 Direction) Deflection(Vector2 value) {
        var length = value.Length();

        return (((length > 0f) && float.IsFinite(f: length))
            ? (MathF.Min(x: length, y: 1f), (value / length))
            : (0f, Vector2.Zero)
        );
    }
    // The boost ramp's integral from rest to t seconds held: t²/2R while it rises, then the whole ramp's area plus the
    // full boost since.
    private static double RampArea(double seconds, double rampSeconds) => ((seconds <= rampSeconds)
        ? ((seconds * seconds) / (2.0 * rampSeconds))
        : ((rampSeconds / 2.0) + (seconds - rampSeconds))
    );

    /// <summary>Integrates the held deflections over an interval: the pointer's travel, in heights of the framed picture
    /// with +Y down the display, and the wheel's turn, in notches with +Y away from the user. Each deflection is held
    /// constant over the interval and the boost ramp is integrated exactly, so splitting an interval into any number of
    /// frames yields the same totals.</summary>
    /// <param name="seconds">The interval, in seconds; a negative or non-finite interval is none.</param>
    /// <param name="feel">The screen route's pointer feel, or <see langword="null"/> for none, which travels and turns
    /// nothing.</param>
    /// <returns>The pointer's travel and the wheel's turn over the interval.</returns>
    public (Vector2 Pointer, Vector2 Wheel) Advance(double seconds, WorldScreenPadPointer? feel) {
        if (!(double.IsFinite(d: seconds) && (seconds > 0.0))) {
            return (Vector2.Zero, Vector2.Zero);
        }

        var (length, direction) = Deflection(value: Stick);
        var start = m_heldSeconds;

        m_heldSeconds = ((length > 0f)
            ? (start + seconds)
            : 0.0);

        if (feel is null) {
            return (Vector2.Zero, Vector2.Zero);
        }

        var pointer = Vector2.Zero;

        if (length > 0f) {
            var boosted = ((feel.RampSeconds > 0f)
                ? (RampArea(rampSeconds: feel.RampSeconds, seconds: m_heldSeconds) - RampArea(rampSeconds: feel.RampSeconds, seconds: start))
                : seconds);
            var heights = (feel.Speed * MathF.Pow(x: length, y: feel.Curve) * (seconds + ((feel.Boost - 1.0) * boosted)));

            pointer = new Vector2(
                x: (direction.X * ((float)heights)),
                y: (-direction.Y * ((float)heights))
            );
        }

        var (scrollLength, scrollDirection) = Deflection(value: Scroll);
        var notches = ((float)(feel.ScrollSpeed * MathF.Pow(x: scrollLength, y: feel.Curve) * seconds));

        return (pointer, (scrollDirection * notches));
    }
    /// <summary>Holds a seat's pointer stick deflection until its next report; a rest restarts the boost ramp.</summary>
    /// <param name="slot">The 0-based local seat whose pad reported it.</param>
    /// <param name="value">The deflection, +X right and +Y up.</param>
    public void SetStick(int slot, Vector2 value) {
        Slot = slot;
        Stick = value;

        if (value == Vector2.Zero) {
            m_heldSeconds = 0.0;
        }
    }
    /// <summary>Holds a seat's scrolling stick deflection until its next report.</summary>
    /// <param name="slot">The 0-based local seat whose pad reported it.</param>
    /// <param name="value">The deflection, +X right and +Y up.</param>
    public void SetScroll(int slot, Vector2 value) {
        Slot = slot;
        Scroll = value;
    }
    /// <summary>Rests both sticks and forgets the seat, as when the seat stops framing the window.</summary>
    public void Reset() {
        Slot = -1;
        Stick = Vector2.Zero;
        Scroll = Vector2.Zero;
        m_heldSeconds = 0.0;
    }
}
