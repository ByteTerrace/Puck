using System.Runtime.InteropServices;

namespace Puck.Platform.Windows;

// The window's request to keep the display lit while it is open, as a game's window does: gamepad input does not reset
// the display's idle timer, and once Windows powers the display off the compositor retires presents on a slow fallback
// (the debug world read 7 to 8 FPS with its GPU work done in 8 ms). A PowerRequestDisplayRequired request is
// process-wide, unlike SetThreadExecutionState's, which drops when the calling thread exits, and `powercfg /requests`
// lists it under its reason.
internal sealed partial class Win32NativeWindow {
    private const uint PowerRequestContextVersion = 0u;
    private const uint PowerRequestContextSimpleString = 0x00000001u;
    private const int PowerRequestDisplayRequired = 0;

    private nint m_displayRequest;

    // A refused request leaves the window working; the display then follows its own idle timer.
    private void StartDisplayRequest() {
        var reason = Marshal.StringToHGlobalUni(s: "Puck World window presenting");

        try {
            var context = new ReasonContext {
                Flags = PowerRequestContextSimpleString,
                SimpleReasonString = reason,
                Version = PowerRequestContextVersion,
            };
            var request = PowerCreateRequest(context: ref context);

            // INVALID_HANDLE_VALUE (-1) is the refusal; a request whose set fails stays held and is closed at Stop.
            m_displayRequest = ((request == -1) ? 0 : request);
            _ = ((m_displayRequest != 0) && PowerSetRequest(request: m_displayRequest, requestType: PowerRequestDisplayRequired));
        } finally {
            Marshal.FreeHGlobal(hglobal: reason);
        }
    }
    private void StopDisplayRequest() {
        _ = ((m_displayRequest != 0) && (PowerClearRequest(request: m_displayRequest, requestType: PowerRequestDisplayRequired) | CloseHandle(handle: m_displayRequest)));
        m_displayRequest = 0;
    }

    // REASON_CONTEXT: the union's widest arm (Detailed: a module handle, two ULONGs and a string array) spans 24 bytes on
    // x64, of which the simple reason string is the first pointer.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
        public nint UnionPadding0;
        public nint UnionPadding1;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint PowerCreateRequest(ref ReasonContext context);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(nint request, int requestType);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(nint request, int requestType);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
