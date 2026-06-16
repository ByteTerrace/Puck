using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

internal static partial class User32 {
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint windowHandle);
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetClipboardData(uint format, nint memoryHandle);

    // WindowClassEx carries a delegate (WndProc) and a string field, so it is not
    // blittable — RegisterClassEx stays DllImport (the source generator cannot marshal it).
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WindowClassEx windowClass);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parentHandle,
        nint menuHandle,
        nint instanceHandle,
        nint parameter
    );
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint windowHandle);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
    public static partial nint DefWindowProc(nint windowHandle, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetCursor(nint cursorHandle);
    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int virtualKey);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out Message message, nint windowHandle, uint filterMin, uint filterMax, uint removeMessage);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Message message);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in Message message);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint windowHandle, int index, nint newLong);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint windowHandle, int index);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint windowHandle, out Rectangle rectangle);
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint MonitorFromWindow(nint windowHandle, int flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitorHandle, ref MonitorInfo monitorInfo);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint windowHandle, int command);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        nint windowHandle,
        nint insertAfterHandle,
        int x,
        int y,
        int width,
        int height,
        uint flags
    );
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateWindow(nint windowHandle);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint windowHandle, nint rectangle, [MarshalAs(UnmanagedType.Bool)] bool eraseBackground);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint windowHandle, out Rectangle rectangle);
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int FillRect(nint deviceContextHandle, in Rectangle rectangle, nint brushHandle);
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial int DrawText(nint deviceContextHandle, string text, int textLength, ref Rectangle rectangle, uint format);
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint BeginPaint(nint windowHandle, out PaintStruct paintStruct);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint windowHandle, in PaintStruct paintStruct);

    // EnumWindows takes a managed delegate callback, unsupported by the source
    // generator — it stays DllImport (converting needs a function pointer + GCHandle).
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint GetDC(nint windowHandle);
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial int GetWindowText(nint windowHandle, [Out] char[] text, int maxLength);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    public static partial int GetWindowTextLength(nint windowHandle);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint windowHandle);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint windowHandle);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint windowHandle);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint windowHandle, nint deviceContextHandle, uint flags);
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int ReleaseDC(nint windowHandle, nint deviceContextHandle);
}
