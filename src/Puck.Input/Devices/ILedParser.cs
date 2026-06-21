using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// Implemented by HID parsers whose controllers expose a settable RGB indicator (the DualSense light bar). The
/// hosting <see cref="GamepadDevice"/> invokes <see cref="SetLedAsync"/> from its single I/O loop, so the write
/// stays serialized with the report reads and with rumble writes on the same device — important where the LED
/// and the motors share one output report.
/// </summary>
internal interface ILedParser
{
    /// <summary>Sets the controller's RGB indicator, preserving any current rumble state.</summary>
    /// <param name="color">The color to display.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the output report has been written.</returns>
    ValueTask SetLedAsync(LedColor color, CancellationToken cancellationToken = default);
}
