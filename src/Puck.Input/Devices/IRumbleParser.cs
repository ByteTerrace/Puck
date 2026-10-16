namespace Puck.Input.Devices;

/// <summary>
/// Implemented by HID parsers whose controllers support dual-motor rumble driven over the same device handle as
/// input. The hosting <see cref="GamepadDevice"/> invokes <see cref="SetRumbleAsync"/> from its single I/O loop,
/// so rumble writes stay serialized with the report reads on that device.
/// </summary>
/// <remarks>The device, not the parser, owns rumble cadence: it holds an update that arrives inside
/// <see cref="RumbleWriteInterval"/> until the interval passes, and rewrites a running rumble every
/// <see cref="RumbleRefreshInterval"/>.</remarks>
public interface IRumbleParser {
    /// <summary>Gets the shortest spacing the controller accepts between two rumble writes. A weaker or equal update
    /// arriving sooner is held and written once the spacing has passed; a stop or a stronger update is written at
    /// once. <see cref="TimeSpan.Zero"/> writes every update.</summary>
    TimeSpan RumbleWriteInterval { get; }
    /// <summary>Gets the period at which a running rumble is rewritten because the controller stops its motors on
    /// its own without fresh writes, or <see cref="TimeSpan.Zero"/> when the controller holds the last write.</summary>
    TimeSpan RumbleRefreshInterval { get; }

    /// <summary>Writes both rumble motors from the given normalized intensities, already clamped to 0..1.</summary>
    /// <param name="lowFrequency">The low-band (strong) motor intensity, 0..1.</param>
    /// <param name="highFrequency">The high-band (weak) motor intensity, 0..1.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the output report has been written.</returns>
    ValueTask SetRumbleAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken = default);
}
