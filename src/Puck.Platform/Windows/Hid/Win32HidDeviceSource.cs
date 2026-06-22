using Puck.Input.Hid;

namespace Puck.Platform.Windows.Hid;

/// <summary>
/// The Windows HID transport for the <c>Puck.Input</c> gamepad manager: enumerates and opens HID device
/// interfaces via CsWin32 (SetupAPI / overlapped <c>CreateFile</c>). On a non-Windows OS it reports no devices,
/// so the manager simply finds nothing rather than failing.
/// </summary>
public sealed class Win32HidDeviceSource : IHidDeviceSource {
    /// <inheritdoc />
    public IEnumerable<HidDeviceInfo> EnumerateInterfaces() {
        return (OperatingSystem.IsWindowsVersionAtLeast(major: 5, minor: 1, build: 2600)
            ? Win32HumanInterfaceDevice.EnumerateInterfaces()
            : []);
    }

    /// <inheritdoc />
    public IHidDevice? Open(string devicePath) {
        ArgumentNullException.ThrowIfNull(devicePath);

        return (OperatingSystem.IsWindowsVersionAtLeast(major: 5, minor: 1, build: 2600)
            ? Win32HumanInterfaceDevice.Open(devicePath: devicePath)
            : null);
    }
}
