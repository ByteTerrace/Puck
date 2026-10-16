using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// Implemented by a parser whose controller has trackpad actuators that play typed haptics (Steam controllers).
/// Implementing it advertises <see cref="GamepadOutputCapabilities.TrackpadHaptics"/>.
/// </summary>
public interface ITrackpadHapticParser {
    /// <summary>Gets the haptic forms the controller plays.</summary>
    TrackpadHapticKinds SupportedTrackpadHaptics { get; }

    /// <summary>Plays one trackpad haptic.</summary>
    /// <param name="effect">The haptic, of a form in <see cref="SupportedTrackpadHaptics"/>.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>A task that completes when the write has been issued.</returns>
    ValueTask PlayTrackpadHapticAsync(TrackpadHapticEffect effect, CancellationToken cancellationToken = default);
}
