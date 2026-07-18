// Puck.BareMetal VulkanWindow — minimal Win32 windowing for puck.
//
// Thin, hand-written user32/kernel32 interop (plain DllImport + blittable structs +
// function-pointer WndProc). Puck.Platform's windowing uses [LibraryImport] + interfaces +
// the BCL, none of which run on puck, so we reimplement just the few calls a Vulkan
// surface needs: register a class, create a window, show it, and pump messages.
using System;
using System.Runtime.InteropServices;

namespace Puck.BareMetal.VulkanWindow;

internal static unsafe class Win32 {
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);
    public const uint PM_REMOVE = 0x0001;
    public const int SW_SHOW = 5;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_QUIT = 0x0012;
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_VISIBLE = 0x10000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32"), SuppressGCTransition]
    public static extern nint GetModuleHandleW(char* moduleName);
    [DllImport("kernel32"), SuppressGCTransition]
    public static extern void Sleep(uint milliseconds);
    [DllImport("user32"), SuppressGCTransition]
    public static extern ushort RegisterClassExW(WNDCLASSEXW* windowClass);
    [DllImport("user32"), SuppressGCTransition]
    public static extern nint CreateWindowExW(
        uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);
    [DllImport("user32"), SuppressGCTransition]
    public static extern int ShowWindow(nint window, int command);
    [DllImport("user32"), SuppressGCTransition]
    public static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32"), SuppressGCTransition]
    public static extern int PeekMessageW(MSG* message, nint window, uint filterMin, uint filterMax, uint remove);
    [DllImport("user32"), SuppressGCTransition]
    public static extern int TranslateMessage(MSG* message);
    [DllImport("user32"), SuppressGCTransition]
    public static extern nint DispatchMessageW(MSG* message);
    [DllImport("user32"), SuppressGCTransition]
    public static extern void PostQuitMessage(int exitCode);
    [DllImport("user32"), SuppressGCTransition]
    public static extern int DestroyWindow(nint window);

    [UnmanagedCallersOnly]
    private static nint WndProc(nint window, uint message, nint wParam, nint lParam) {
        if (message == WM_DESTROY) {
            PostQuitMessage(exitCode: 0);
            return 0;
        }

        return DefWindowProcW(lParam: lParam, message: message, wParam: wParam, window: window);
    }

    // Write a const ASCII string into a wide, null-terminated buffer (puck string
    // literals are not relied upon to be null-terminated for interop).
    private static void WriteWideZ(char* destination, string text) {
        int i = 0;

        for (; (i < text.Length); i++)
            destination[i] = text[i];
        destination[i] = '\0';
    }

    // Register the class and create a visible window. Returns the HWND (0 on failure).
    public static nint CreateMainWindow(int width, int height) {
        char* className = stackalloc char[32];

        WriteWideZ(destination: className, text: "PuckBareMetalVulkanWindow");

        char* title = stackalloc char[64];

        WriteWideZ(destination: title, text: "Puck.BareMetal - Vulkan");

        nint instance = GetModuleHandleW(moduleName: null);

        WNDCLASSEXW windowClass = default;

        windowClass.cbSize = (uint)sizeof(WNDCLASSEXW);
        windowClass.style = 0x0003; // CS_HREDRAW | CS_VREDRAW
        windowClass.lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc;
        windowClass.hInstance = instance;
        windowClass.lpszClassName = className;

        if (RegisterClassExW(windowClass: &windowClass) == 0)
            return 0;

        return CreateWindowExW(
            className: className, exStyle: 0, height: height,
            instance: instance,
            menu: 0, param: 0, parent: 0, style: WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            width: width, windowName: title, x: CW_USEDEFAULT, y: CW_USEDEFAULT);
    }

    // Pump all pending messages. Returns false when WM_QUIT was received.
    public static bool PumpMessages() {
        MSG message;

        while (PeekMessageW(filterMax: 0, filterMin: 0, message: &message, remove: PM_REMOVE, window: 0) != 0) {
            if (message.message == WM_QUIT)
                return false;

            TranslateMessage(message: &message);
            DispatchMessageW(message: &message);
        }

        return true;
    }
}
