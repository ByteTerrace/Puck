using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Gamepad;

/// <summary>The XInput gamepad state (buttons bitmask, 0..255 triggers, signed 16-bit thumbsticks).</summary>
[StructLayout(layoutKind: LayoutKind.Sequential)]
public struct XInputGamepad {
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short ThumbLeftX;
    public short ThumbLeftY;
    public short ThumbRightX;
    public short ThumbRightY;
}
/// <summary>A polled XInput controller state, tagged with a packet number that changes only on a real update.</summary>
/// <remarks>Mirrors <c>XINPUT_STATE</c> plus the trailing reserved word some builds of the ordinal-100 export
/// write, so that export can never write past the structure.</remarks>
[StructLayout(layoutKind: LayoutKind.Sequential)]
public struct XInputState {
    public uint PacketNumber;
    public XInputGamepad Gamepad;
    public uint Reserved;
}
/// <summary>The dual-motor vibration request (low-frequency left motor, high-frequency right motor; 0..65535).</summary>
[StructLayout(layoutKind: LayoutKind.Sequential)]
public struct XInputVibration {
    public ushort LeftMotorSpeed;
    public ushort RightMotorSpeed;
}
/// <summary>The extended capabilities the ordinal-108 export reports: <c>XINPUT_CAPABILITIES</c> followed by
/// the device's USB identity.</summary>
/// <remarks>Mirrors SDL's <c>SDL_XINPUT_CAPABILITIES_EX</c>.</remarks>
[StructLayout(layoutKind: LayoutKind.Sequential)]
public struct XInputCapabilitiesEx {
    public byte Type;
    public byte SubType;
    public ushort Flags;
    public XInputGamepad Gamepad;
    public XInputVibration Vibration;
    public ushort VendorId;
    public ushort ProductId;
    public ushort ProductVersion;
    public ushort Unknown1;
    public uint Unknown2;
}
/// <summary>The battery report of <c>XInputGetBatteryInformation</c>.</summary>
[StructLayout(layoutKind: LayoutKind.Sequential)]
public struct XInputBatteryInformation {
    public byte BatteryType;
    public byte BatteryLevel;
}
/// <summary>P/Invoke surface for XInput 1.4 (xinput1_4.dll, Windows 8 and later).</summary>
public static partial class XInput {
    /// <summary>The alkaline battery type.</summary>
    public const byte BatteryTypeAlkaline = 2;
    /// <summary>The battery type of a controller that is not connected.</summary>
    public const byte BatteryTypeDisconnected = 0;
    /// <summary>The nickel-metal-hydride battery type.</summary>
    public const byte BatteryTypeNickelMetalHydride = 3;
    /// <summary>The battery type of a controller powered by its cable.</summary>
    public const byte BatteryTypeWired = 1;
    /// <summary>The battery-information device type of the gamepad itself.</summary>
    public const byte DeviceTypeGamepad = 0;
    /// <summary>The capability flag of a pad with a voice device (a headset) attached.</summary>
    public const ushort CapabilityVoiceSupported = 0x0004;
    public const uint ErrorSuccess = 0u;
    /// <summary>The Guide/home button bit, exposed only by the undocumented <see cref="GetStateEx"/>.</summary>
    public const ushort GamepadGuide = 0x0400;

    /// <summary>Reads a controller's battery type and its charge level, 0 (empty) through 3 (full).</summary>
    /// <param name="userIndex">The XInput user index, 0 through 3.</param>
    /// <param name="deviceType">The device whose battery is read; <see cref="DeviceTypeGamepad"/> for the pad.</param>
    /// <param name="information">The battery report.</param>
    /// <returns><see cref="ErrorSuccess"/> on success; otherwise a Win32 error code.</returns>
    [LibraryImport(libraryName: "xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    public static partial uint GetBatteryInformation(uint userIndex, byte deviceType, out XInputBatteryInformation information);
    /// <summary>
    /// The undocumented ordinal-108 export: <c>XInputGetCapabilities</c> extended with the device's vendor and
    /// product ids. Its first argument is always 1.
    /// </summary>
    /// <param name="one">Always 1.</param>
    /// <param name="userIndex">The XInput user index, 0 through 3.</param>
    /// <param name="flags">The capability flags; 0 for every device.</param>
    /// <param name="capabilities">The extended capabilities.</param>
    /// <returns><see cref="ErrorSuccess"/> on success; otherwise a Win32 error code.</returns>
    [LibraryImport(libraryName: "xinput1_4.dll", EntryPoint = "#108")]
    public static partial uint GetCapabilitiesEx(uint one, uint userIndex, uint flags, out XInputCapabilitiesEx capabilities);
    /// <summary>
    /// The undocumented ordinal-100 export: identical to <c>XInputGetState</c> but also reports the Guide
    /// button (<see cref="GamepadGuide"/>), which the public API hides.
    /// </summary>
    /// <param name="userIndex">The XInput user index, 0 through 3.</param>
    /// <param name="state">The polled state.</param>
    /// <returns><see cref="ErrorSuccess"/> on success; otherwise a Win32 error code.</returns>
    [LibraryImport(libraryName: "xinput1_4.dll", EntryPoint = "#100")]
    public static partial uint GetStateEx(uint userIndex, out XInputState state);
    [LibraryImport(libraryName: "xinput1_4.dll", EntryPoint = "XInputSetState")]
    public static partial uint SetState(uint userIndex, in XInputVibration vibration);
}
