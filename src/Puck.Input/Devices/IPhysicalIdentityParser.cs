namespace Puck.Input.Devices;

/// <summary>
/// A parser that learns which physical controller it reads, so one controller reached over two transports at
/// once (a USB cable plugged into a pad that is also paired over Bluetooth) counts as one device: the manager
/// keeps the USB connection and holds the Bluetooth one back while both stream.
/// </summary>
public interface IPhysicalIdentityParser {
    /// <summary>Gets the controller's physical identity (its Bluetooth address as hexadecimal), or
    /// <see langword="null"/> until initialization reads it. Written once on the device's I/O loop.</summary>
    string? PhysicalId { get; }
}
