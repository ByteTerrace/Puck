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
    /// Applies a typed adaptive-trigger effect to each trigger (L2 = <paramref name="left"/>, R2 =
    /// <paramref name="right"/>), as on the DualSense. The effect is composed into the controller's normal output
    /// report, so it coexists with rumble and the LED. Pass <see cref="TriggerEffectSpec.Off"/> to clear a trigger.
    /// </summary>
    /// <param name="left">The left trigger's effect.</param>
    /// <param name="right">The right trigger's effect.</param>
    /// <returns><see langword="true"/> if the device has adaptive triggers and the effect was accepted; otherwise <see langword="false"/>.</returns>
    bool SetTriggerEffect(in TriggerEffectSpec left, in TriggerEffectSpec right);

    /// <summary>
    /// Schedules an adaptive-trigger effect (as <see cref="SetTriggerEffect"/>) to be applied when the shared
    /// capture clock (<see cref="IInputClock"/>) reaches <paramref name="fireAtTick"/> — rhythm-grade haptics
    /// timed against the same clock that stamps input. A tick already in the past applies immediately.
    /// </summary>
    /// <param name="left">The left trigger's effect.</param>
    /// <param name="right">The right trigger's effect.</param>
    /// <param name="fireAtTick">The capture-clock engine tick to apply the effect at.</param>
    /// <returns><see langword="true"/> if the device has adaptive triggers and the effect was accepted; otherwise <see langword="false"/>.</returns>
    bool SetTriggerEffectAt(in TriggerEffectSpec left, in TriggerEffectSpec right, ulong fireAtTick);

    /// <summary>
    /// Sends a raw, device-specific output report (the escape hatch for effects without a typed shape, such as
    /// Switch HD-rumble waveforms).
    /// </summary>
    /// <param name="data">The raw report payload, in the device's report format.</param>
    /// <returns><see langword="true"/> if the device has a raw channel and the payload was accepted; otherwise <see langword="false"/>.</returns>
    bool SendEffect(ReadOnlySpan<byte> data);
}
