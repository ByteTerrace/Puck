using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

/// <summary>A <c>POINTER_INFO</c> (winuser.h): the information every pointer type shares. Mirrors the documented
/// field order (<c>pointerType</c>, <c>pointerId</c>, <c>frameId</c>, <c>pointerFlags</c>, <c>sourceDevice</c>,
/// <c>hwndTarget</c>, the four locations, <c>dwTime</c>, <c>historyCount</c>, <c>InputData</c>, <c>dwKeyStates</c>,
/// <c>PerformanceCount</c>, <c>ButtonChangeType</c>) with natural alignment.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PointerInfo {
    public uint PointerType;
    public uint PointerId;
    public uint FrameId;
    public uint PointerFlags;
    public nint SourceDevice;
    public nint TargetWindow;
    public Point PixelLocation;
    public Point HimetricLocation;
    public Point PixelLocationRaw;
    public Point HimetricLocationRaw;
    public uint Time;
    public uint HistoryCount;
    public int InputData;
    public uint KeyStates;
    public ulong PerformanceCount;
    public int ButtonChangeType;
}
/// <summary>A <c>POINTER_TOUCH_INFO</c> (winuser.h): a touch pointer's <see cref="PointerInfo"/> plus its contact
/// rectangles, orientation and pressure (0 to 1024, valid when <c>touchMask</c> carries <c>TOUCH_MASK_PRESSURE</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PointerTouchInfo {
    public PointerInfo PointerInfo;
    public uint TouchFlags;
    public uint TouchMask;
    public Rectangle Contact;
    public Rectangle ContactRaw;
    public uint Orientation;
    public uint Pressure;
}
/// <summary>A <c>POINTER_PEN_INFO</c> (winuser.h): a pen pointer's <see cref="PointerInfo"/> plus its button flags,
/// pressure (0 to 1024, valid when <c>penMask</c> carries <c>PEN_MASK_PRESSURE</c>), rotation and tilt.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PointerPenInfo {
    public PointerInfo PointerInfo;
    public uint PenFlags;
    public uint PenMask;
    public uint Pressure;
    public uint Rotation;
    public int TiltX;
    public int TiltY;
}
