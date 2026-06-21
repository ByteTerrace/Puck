using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Gamepad;

/// <summary>The XInput gamepad state (buttons bitmask, 0..255 triggers, signed 16-bit thumbsticks).</summary>
[StructLayout(layoutKind: LayoutKind.Sequential)]
public struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short ThumbLeftX;
    public short ThumbLeftY;
    public short ThumbRightX;
    public short ThumbRightY;
}

/// <summary>A polled XInput controller state, tagged with a packet number that changes only on a real update.</summary>
[StructLayout(layoutKind: LayoutKind.Sequential)]
public struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

/// <summary>The dual-motor vibration request (low-frequency left motor, high-frequency right motor; 0..65535).</summary>
[StructLayout(layoutKind: LayoutKind.Sequential)]
public struct XInputVibration
{
    public ushort LeftMotorSpeed;
    public ushort RightMotorSpeed;
}

/// <summary>P/Invoke surface for XInput (xinput1_4.dll, Windows 8+) and the multimedia timer-resolution calls.</summary>
public static partial class XInput
{
    public const uint ErrorSuccess = 0u;

    /// <summary>The Guide/home button bit, exposed only by the undocumented <see cref="GetStateEx"/>.</summary>
    public const ushort GamepadGuide = 0x0400;

    [LibraryImport(libraryName: "xinput1_4.dll", EntryPoint = "XInputGetState")]
    public static partial uint GetState(uint userIndex, out XInputState state);

    /// <summary>
    /// The undocumented ordinal-100 export: identical to <c>XInputGetState</c> but also reports the Guide
    /// button (<see cref="GamepadGuide"/>), which the public API hides.
    /// </summary>
    [LibraryImport(libraryName: "xinput1_4.dll", EntryPoint = "#100")]
    public static partial uint GetStateEx(uint userIndex, out XInputState state);

    [LibraryImport(libraryName: "xinput1_4.dll", EntryPoint = "XInputSetState")]
    public static partial uint SetState(uint userIndex, in XInputVibration vibration);

    [LibraryImport(libraryName: "winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint period);

    [LibraryImport(libraryName: "winmm.dll", EntryPoint = "timeEndPeriod")]
    public static partial uint TimeEndPeriod(uint period);
}
