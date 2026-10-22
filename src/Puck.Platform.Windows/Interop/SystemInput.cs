using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

/// <summary>One input event as <c>SendInput</c> takes it (<c>INPUT</c>): a type tag, then a union of the mouse and
/// keyboard events, the larger sizing it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SystemInput {
    /// <summary>The type tag of a keyboard event (<c>INPUT_KEYBOARD</c>).</summary>
    public const uint Keyboard = 1;
    /// <summary>The type tag of a mouse event (<c>INPUT_MOUSE</c>).</summary>
    public const uint Mouse = 0;

    /// <summary>The type tag: <see cref="Mouse"/> or <see cref="Keyboard"/>.</summary>
    public uint Type;
    /// <summary>The event the tag names.</summary>
    public SystemInputUnion Union;
}
/// <summary>The union of a <see cref="SystemInput"/>'s mouse and keyboard events.</summary>
[StructLayout(LayoutKind.Explicit)]
public struct SystemInputUnion {
    /// <summary>The keyboard event.</summary>
    [FieldOffset(0)]
    public KeyboardInput Keyboard;
    /// <summary>The mouse event.</summary>
    [FieldOffset(0)]
    public MouseInput Mouse;
}
/// <summary>A mouse event (<c>MOUSEINPUT</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MouseInput {
    /// <summary>The x coordinate: normalized 0 to 65535 with an absolute flag, a relative distance otherwise.</summary>
    public int X;
    /// <summary>The y coordinate: normalized 0 to 65535 with an absolute flag, a relative distance otherwise.</summary>
    public int Y;
    /// <summary>The wheel turn in 120ths of a notch, or the extra button (1 or 2) of an X button event.</summary>
    public int Data;
    /// <summary>The <c>MOUSEEVENTF_</c> flags.</summary>
    public uint Flags;
    /// <summary>The event's time stamp in milliseconds; 0 lets the system stamp it.</summary>
    public uint Time;
    /// <summary>The extra information the receiving window reads back (<c>GetMessageExtraInfo</c>).</summary>
    public nint ExtraInfo;
}
/// <summary>A keyboard event (<c>KEYBDINPUT</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct KeyboardInput {
    /// <summary>The virtual key, which a scan-code event ignores.</summary>
    public ushort VirtualKey;
    /// <summary>The hardware scan code.</summary>
    public ushort ScanCode;
    /// <summary>The <c>KEYEVENTF_</c> flags.</summary>
    public uint Flags;
    /// <summary>The event's time stamp in milliseconds; 0 lets the system stamp it.</summary>
    public uint Time;
    /// <summary>The extra information the receiving window reads back (<c>GetMessageExtraInfo</c>).</summary>
    public nint ExtraInfo;
}
