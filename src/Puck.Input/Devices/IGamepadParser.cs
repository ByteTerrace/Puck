namespace Puck.Input.Devices;

/// <summary>
/// Translates a device's raw HID input reports into the normalized <see cref="GamepadState"/>. One parser
/// implementation exists per controller family; it owns any device-specific initialization (handshakes,
/// sensor enablement) and the report-byte layout, so the rest of the engine never sees raw reports.
/// </summary>
public interface IGamepadParser {
    /// <summary>The controller family this parser handles.</summary>
    GamepadType Type { get; }

    /// <summary>The optional input features this controller provides (gyro, analog triggers).</summary>
    GamepadInputCapabilities InputCapabilities { get; }

    /// <summary>
    /// Performs any device-specific initialization required before input reports are meaningful (for example,
    /// the Switch Pro UART handshake and IMU enablement). Safe to call once per connection.
    /// </summary>
    /// <param name="playerIndex">
    /// The zero-based player slot assigned to this device, used to set a per-controller indicator (e.g. the
    /// Switch player LEDs) so simultaneously connected controllers are visually distinct.
    /// </param>
    /// <param name="cancellationToken">A token that cancels the initialization.</param>
    /// <returns>A task that completes when the device is ready to stream input reports.</returns>
    ValueTask InitializeAsync(int playerIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses a single raw input report into a normalized state.
    /// </summary>
    /// <param name="report">The raw report bytes as read from the device.</param>
    /// <param name="state">The normalized state decoded from the report, when parsing succeeds.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="report"/> was a recognized input report and
    /// <paramref name="state"/> was populated; otherwise <see langword="false"/> (the report should be ignored).
    /// </returns>
    bool TryParse(ReadOnlySpan<byte> report, out GamepadState state);
}
