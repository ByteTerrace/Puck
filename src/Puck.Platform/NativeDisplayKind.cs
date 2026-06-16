namespace Puck.Platform;

public enum NativeDisplayKind {
    Auto = 0,
    Headless,
    Win32,
    Wayland,
    Xcb,
    Xlib,
    Vi,
    Unsupported
}
