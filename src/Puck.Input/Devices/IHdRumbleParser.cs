using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// Implemented by an <see cref="IRumbleParser"/> whose actuators play arbitrary frequencies (the Switch Pro). The
/// hosting <see cref="GamepadDevice"/> keeps its rumble cadence and writes every rumble, ordinary or HD, through
/// <see cref="SetHdRumbleAsync"/>; an ordinary rumble arrives as both actuators at their default carriers.
/// Implementing it advertises <see cref="GamepadOutputCapabilities.HdRumble"/>.
/// </summary>
public interface IHdRumbleParser {
    /// <summary>Writes both actuators' bands.</summary>
    /// <param name="left">The left actuator; amplitudes already clamped to 0..1.</param>
    /// <param name="right">The right actuator; amplitudes already clamped to 0..1.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>A task that completes when the write has been issued or queued behind the controller's spacing.</returns>
    ValueTask SetHdRumbleAsync(HdRumbleSide left, HdRumbleSide right, CancellationToken cancellationToken = default);
}
