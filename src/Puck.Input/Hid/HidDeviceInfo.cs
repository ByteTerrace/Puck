namespace Puck.Input.Hid;

/// <summary>
/// A lightweight description of a present HID device interface — its path and parsed VID/PID — produced by a
/// <see cref="IHidDeviceSource"/> during enumeration without opening the device.
/// </summary>
/// <param name="Path">The device interface path used to open the device.</param>
/// <param name="VendorId">The USB vendor id parsed from the path.</param>
/// <param name="ProductId">The USB product id parsed from the path.</param>
public readonly record struct HidDeviceInfo(string Path, ushort VendorId, ushort ProductId);
