using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

// Raw Input interop (WM_INPUT). Hand-authored — the window layer does not use CsWin32. Only the mouse path is
// modeled: RAWINPUT is a union of mouse/keyboard/HID after the header, and a sequential header+mouse layout
// matches the mouse case (we register for the mouse only, so the other union members are never read).

/// <summary>A <c>RAWINPUTDEVICE</c>: the device class to register for raw input with <see cref="User32.RegisterRawInputDevices"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDevice {
    public ushort UsagePage;
    public ushort Usage;
    public uint Flags;
    public nint TargetWindowHandle;
}

/// <summary>A <c>RAWINPUTHEADER</c>: the header preceding every raw input packet.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawInputHeader {
    public uint Type;
    public uint Size;
    public nint DeviceHandle;
    public nint WParam;
}

/// <summary>A <c>RAWMOUSE</c>: a raw mouse packet. The button union is modeled with explicit offsets; only the flags and relative motion are read.</summary>
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

/// <summary>A <c>RAWINPUT</c> packet for the mouse: the header followed by the mouse data (the first member of the device union).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawInput {
    public RawInputHeader Header;
    public RawMouse Mouse;
}
