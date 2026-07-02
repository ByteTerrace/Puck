namespace Puck.Input.Hid;

/// <summary>
/// The physical transport a HID device is connected over, inferred from its device interface path. Lets a parser
/// pick the correct wire protocol for a family that speaks differently over each link (e.g. the Switch Pro's USB
/// init handshake, or the DualSense's Bluetooth output-report framing).
/// </summary>
public enum HidTransport {
    /// <summary>The transport could not be determined from the device path.</summary>
    Unknown = 0,
    /// <summary>Wired USB.</summary>
    Usb = 1,
    /// <summary>Bluetooth (classic or low-energy).</summary>
    Bluetooth = 2,
}
