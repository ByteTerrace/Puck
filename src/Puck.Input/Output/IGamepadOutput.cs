using Puck.Commands;

namespace Puck.Input.Output;

/// <summary>
/// The output (haptics/indicator) handle for a single connected controller, resolved by its
/// <see cref="InputDeviceId"/>. All methods are non-blocking and thread-safe relative to the input polling
/// path: a call enqueues the effect, and the device's I/O loop performs the native write. Methods return
/// <see langword="false"/> when the feature is unsupported (see <see cref="Capabilities"/>) or the device has
/// disconnected.
/// </summary>
public interface IGamepadOutput {
    /// <summary>The device this handle drives.</summary>
    InputDeviceId DeviceId { get; }

    /// <summary>The output features this device supports.</summary>
    GamepadOutputCapabilities Capabilities { get; }

    /// <summary>Requests dual-motor rumble.</summary>
    /// <param name="effect">The rumble intensities and duration.</param>
    /// <returns><see langword="true"/> if the effect was accepted; otherwise <see langword="false"/>.</returns>
    bool Rumble(in RumbleEffect effect);

    /// <summary>Requests trigger (impulse) rumble.</summary>
    /// <param name="effect">The per-trigger intensities and duration.</param>
    /// <returns><see langword="true"/> if the effect was accepted; otherwise <see langword="false"/>.</returns>
    bool RumbleTriggers(in TriggerRumbleEffect effect);

    /// <summary>Sets the controller's RGB indicator.</summary>
    /// <param name="color">The color to display.</param>
    /// <returns><see langword="true"/> if accepted; otherwise <see langword="false"/>.</returns>
    bool SetLed(in LedColor color);

    /// <summary>
    /// Sends a raw, device-specific output report (the escape hatch for DualSense adaptive triggers, Switch
    /// HD-rumble waveforms, and other effects without a portable shape).
    /// </summary>
    /// <param name="data">The raw report payload, in the device's report format.</param>
    /// <returns><see langword="true"/> if the device has a raw channel and the payload was accepted; otherwise <see langword="false"/>.</returns>
    bool SendEffect(ReadOnlySpan<byte> data);
}
