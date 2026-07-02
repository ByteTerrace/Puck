namespace Puck.Input.Devices;

/// <summary>
/// Implemented by HID parsers whose controllers support dual-motor rumble driven over the same device handle as
/// input. The hosting <see cref="GamepadDevice"/> invokes <see cref="SetRumbleAsync"/> from its single I/O loop,
/// so rumble writes stay serialized with the report reads on that device.
/// </summary>
internal interface IRumbleParser {
    /// <summary>Writes both rumble motors from the given normalized intensities.</summary>
    /// <param name="lowFrequency">The low-band (strong) motor intensity, 0..1.</param>
    /// <param name="highFrequency">The high-band (weak) motor intensity, 0..1.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the output report has been written.</returns>
    ValueTask SetRumbleAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken = default);
}
