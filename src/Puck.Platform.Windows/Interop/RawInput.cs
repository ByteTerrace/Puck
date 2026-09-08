using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

// Raw Input interop (WM_INPUT). Hand-authored — the window layer does not use CsWin32. RAWINPUT is a union of
// mouse/keyboard/HID after the header; this window registers for the mouse and keyboard classes only, so the union
// is modeled as exactly those two branches at the same offset.

/// <summary>A <c>RAWINPUTDEVICE</c>: the device class to register for raw input with <see cref="User32.RegisterRawInputDevices"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDevice {
    public ushort UsagePage;
    public ushort Usage;
    public uint Flags;
    public nint TargetWindowHandle;
}
/// <summary>A <c>RAWINPUTHEADER</c>: the header preceding every raw input packet. <see cref="DeviceHandle"/> is the
/// per-connection handle <see cref="User32.GetRawInputDeviceInfo"/> resolves to a stable device identity.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawInputHeader {
    public uint Type;
    public uint Size;
    public nint DeviceHandle;
    public nint WParam;
}
/// <summary>A <c>RAWMOUSE</c>: a raw mouse packet. <see cref="Flags"/> distinguishes relative motion, primary-desktop
/// absolute motion, and <c>MOUSE_VIRTUAL_DESKTOP</c> absolute motion. The button union is modeled with explicit offsets.
/// <see cref="ButtonData"/> carries a signed wheel-rotation quantum (in <c>WHEEL_DELTA</c> units) when
/// <see cref="ButtonFlags"/> has the wheel/h-wheel bit set.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct RawMouse {
    [FieldOffset(0)] public ushort Flags;
    [FieldOffset(4)] public uint Buttons;
    [FieldOffset(4)] public ushort ButtonFlags;
    [FieldOffset(6)] public ushort ButtonData;
    [FieldOffset(8)] public uint RawButtons;
    [FieldOffset(12)] public int LastX;
    [FieldOffset(16)] public int LastY;
    [FieldOffset(20)] public uint ExtraInformation;
}
/// <summary>A <c>RAWKEYBOARD</c>: a raw keyboard packet. <see cref="Message"/> carries the original
/// WM_KEYDOWN/WM_KEYUP/WM_SYSKEYDOWN/WM_SYSKEYUP id, and <see cref="MakeCode"/> the scan code — the same inputs the
/// legacy message path hands <c>MapVirtualKey</c>/<c>ToUnicodeEx</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawKeyboard {
    public ushort MakeCode;
    public ushort Flags;
    public ushort Reserved;
    public ushort VKey;
    public uint Message;
    public uint ExtraInformation;
}
/// <summary>The RAWINPUT device-data union, laid out at the same offset the native union occupies — only the two
/// branches this window registers for (mouse, keyboard) are modeled.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct RawInputData {
    [FieldOffset(0)] public RawMouse Mouse;
    [FieldOffset(0)] public RawKeyboard Keyboard;
}
/// <summary>A <c>RAWINPUT</c> packet: the header followed by the device-data union.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawInput {
    public RawInputHeader Header;
    public RawInputData Data;
}
