namespace Puck.Input.Hid;

/// <summary>
/// A platform's HID transport: enumerates present device interfaces and opens them. The Windows implementation
/// lives in <c>Puck.Platform</c>; a Linux <c>hidraw</c> implementation can be supplied the same way without
/// changing <c>Puck.Input</c>. Implementations that have no HID support on the current OS return an empty
/// enumeration and <see langword="null"/> from <see cref="Open"/>.
/// </summary>
public interface IHidDeviceSource
{
    /// <summary>
    /// Enumerates present HID device interfaces (path + VID/PID) without opening any device, so a caller can
    /// filter on VID/PID and open only the few it cares about.
    /// </summary>
    /// <returns>The present HID device interfaces.</returns>
    IEnumerable<HidDeviceInfo> EnumerateInterfaces();

    /// <summary>Opens a specific device interface by path.</summary>
    /// <param name="devicePath">The device interface path (from <see cref="EnumerateInterfaces"/>).</param>
    /// <returns>An opened device, or <see langword="null"/> if it could not be opened.</returns>
    IHidDevice? Open(string devicePath);
}
