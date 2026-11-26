using System.Runtime.InteropServices;
using Puck.Platform.Linux.Interop;

namespace Puck.Platform.Linux;

// Live placement through ConfigureWindow requests. The window manager answers a request with the ConfigureNotify the
// event pump already folds into Width, Height and ResizeCount, the path a drag of the window's edge takes, so the size it
// grants is reported from the next pump. This backend reads no RandR monitor layout: the root screen stands as the one
// monitor, which is exact on a single display and spans every monitor of a multi-head screen. It has no fullscreen mode.
internal sealed partial class XcbNativeWindow : INativeWindowPlacement {
    private const ushort XcbConfigWindowHeight = 8;
    private const ushort XcbConfigWindowWidth = 4;
    private const ushort XcbConfigWindowX = 1;
    private const ushort XcbConfigWindowY = 2;

    /// <inheritdoc/>
    public NativeWindowPlacement Placement {
        get {
            ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

            var screen = new NativeDesktopArea(X: 0, Y: 0, Width: m_screenWidth, Height: m_screenHeight);

            return new NativeWindowPlacement(
                Client: (TryReadClientOrigin(x: out var x, y: out var y) ? new NativeDesktopArea(X: x, Y: y, Width: ((int)Width), Height: ((int)Height)) : null),
                Fullscreen: false,
                Height: Height,
                Monitor: "screen",
                MonitorArea: screen,
                Width: Width,
                WorkArea: screen
            );
        }
    }

    /// <inheritdoc/>
    public bool TrySetClientSize(uint width, uint height, out string? refusal) {
        refusal = NativeWindowPlacement.RefuseClientSize(height: height, width: width);

        return ((refusal is null) && TryConfigure(mask: (XcbConfigWindowWidth | XcbConfigWindowHeight), refusal: out refusal, values: [width, height]));
    }
    /// <inheritdoc/>
    public bool TryMoveClient(int x, int y, out string? refusal) => TryConfigure(
        mask: (XcbConfigWindowX | XcbConfigWindowY),
        refusal: out refusal,
        values: [unchecked((uint)x), unchecked((uint)y)]
    );
    /// <inheritdoc/>
    public bool TryCenter(out string? refusal) => TryMoveClient(
        refusal: out refusal,
        x: ((m_screenWidth - ((int)Width)) / 2),
        y: Math.Max(val1: 0, val2: ((m_screenHeight - ((int)Height)) / 2))
    );
    /// <inheritdoc/>
    public bool TrySetFullscreen(bool fullscreen, out string? refusal) => NativeWindowPlacement.WithoutFullscreen(fullscreen: fullscreen, kind: DisplayKind, refusal: out refusal);

    private bool TryConfigure(ushort mask, uint[] values, out string? refusal) {
        if (!IsOpen) {
            refusal = "the window is closed";
            return false;
        }

        _ = Xcb.xcb_configure_window(connection: m_connection, valueList: values, valueMask: mask, windowId: m_window);
        _ = Xcb.xcb_flush(connection: m_connection);
        refusal = null;
        return true;
    }
    // The client area's top-left corner in root coordinates: a round trip to the server.
    private bool TryReadClientOrigin(out int x, out int y) {
        x = 0;
        y = 0;
        if (!IsOpen) {
            return false;
        }

        var reply = Xcb.xcb_translate_coordinates_reply(
            connection: m_connection,
            cookie: Xcb.xcb_translate_coordinates(connection: m_connection, destinationWindow: m_rootWindow, sourceWindow: m_window, sourceX: 0, sourceY: 0),
            error: 0
        );

        if (reply == 0) {
            return false;
        }

        try {
            x = Marshal.ReadInt16(ofs: 12, ptr: reply);
            y = Marshal.ReadInt16(ofs: 14, ptr: reply);
            return true;
        } finally {
            Libc.free(pointer: reply);
        }
    }
}
