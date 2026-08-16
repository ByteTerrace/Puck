using Puck.Commands;
using Puck.Input;
using Puck.Platform.Windows.Gamepad;
using Puck.Platform.Windows.Hid;

namespace Puck.Platform.Windows;

/// <summary>Constructs the Windows gamepad manager: XInput/GameInput acquisition plus raw HID enumeration.</summary>
public static class WindowsInputTransports {
    /// <summary>Creates a <see cref="GamepadManager"/> wired to the Windows Xbox (XInput/GameInput) acquisition
    /// source and the Windows raw-HID device source.</summary>
    /// <param name="clock">The input clock the manager's coalescer stamps snapshots against.</param>
    /// <param name="diagnostics">The diagnostics sink for acquisition/enumeration failures.</param>
    /// <returns>The constructed gamepad manager.</returns>
    public static GamepadManager CreateGamepadManager(IInputClock clock, Action<string> diagnostics) => new(
        acquisitionSource: new Win32XboxAcquisitionSource(diagnostics: diagnostics),
        clock: clock,
        diagnostics: diagnostics,
        hidSource: new Win32HidDeviceSource()
    );
}
