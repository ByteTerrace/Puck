using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

// The pointer lock's Win32 seam: the calls that hide, confine, capture and move the session's real cursor, which only a
// foreground window and a real mouse reach, so build/CoverageHardwareOnly.txt lists this file. Every decision and every
// coordinate is Win32PointerLock's, which the CPU suites test.
internal sealed partial class Win32NativeWindow {
    // Locks the pointer for a drag when the window may (Win32PointerLock.CanLock).
    private void BeginPointerLock() {
        if (
            (m_windowHandle == 0) ||
            !User32.GetCursorPos(point: out var restore) ||
            !User32.GetClientRect(
                rectangle: out var client,
                windowHandle: m_windowHandle
            ) ||
            !Win32PointerLock.CanLock(
                clientHeight: client.Bottom,
                clientWidth: client.Right,
                foreground: (User32.GetForegroundWindow() == m_windowHandle)
            )
        ) {
            return;
        }

        var origin = new Point();

        if (!User32.ClientToScreen(
            point: ref origin,
            windowHandle: m_windowHandle
        )) {
            return;
        }

        var (centreX, centreY) = Win32PointerLock.Centre(
            clientHeight: client.Bottom,
            clientWidth: client.Right,
            originX: origin.X,
            originY: origin.Y
        );

        m_locksPointer = true;
        m_lockRestore = restore;
        m_lockCentre = new Point {
            X = centreX,
            Y = centreY,
        };

        if (User32.GetCapture() != m_windowHandle) {
            _ = User32.SetCapture(windowHandle: m_windowHandle);
        }

        _ = User32.ClipCursor(rectangle: new Rectangle {
            Bottom = (origin.Y + client.Bottom),
            Left = origin.X,
            Right = (origin.X + client.Right),
            Top = origin.Y,
        });
        _ = User32.SetCursor(cursorHandle: 0);
        RecentrePointer();
    }
    // Frees a locked pointer, at the point the lock began when restoring, and gives back the capture no held button
    // keeps.
    private void EndPointerLock(bool restore) {
        if (!m_locksPointer) {
            return;
        }

        m_locksPointer = false;
        _ = User32.FreeCursor(rectangle: 0);

        if (restore) {
            WarpPointer(screen: m_lockRestore);
        }
        if (m_pointerButtons == 0) {
            ReleasePointerCapture();
        }
        if (m_lastMouseX is not null) {
            _ = User32.SetCursor(cursorHandle: ClientCursor);
        }
    }
    // Returns a locked pointer to the client area's centre once it has moved off it, after the pump has taken the
    // motion that moved it.
    private void RecentrePointer() {
        if (
            User32.GetCursorPos(point: out var at) &&
            ((at.X != m_lockCentre.X) || (at.Y != m_lockCentre.Y))
        ) {
            WarpPointer(screen: m_lockCentre);
        }
    }
    // Moves the cursor to a screen point and rebases every motion baseline on it, so the move itself reports no motion:
    // the window-message fallback's last client point, and each absolute-mode device's awaited warp target
    // (Win32PointerLock.Baseline).
    private void WarpPointer(Point screen) {
        if (!User32.SetCursorPos(
            x: screen.X,
            y: screen.Y
        )) {
            return;
        }

        var client = screen;

        if (User32.ScreenToClient(
            point: ref client,
            windowHandle: m_windowHandle
        )) {
            m_lastMouseX = client.X;
            m_lastMouseY = client.Y;
        }

        foreach (var state in m_rawMouseStates.Values) {
            if (state.LastAbsoluteX is null) {
                continue;
            }

            state.Warp = (state.VirtualDesktop
                ? Win32PointerLock.ToAbsolute(
                    height: User32.GetSystemMetrics(index: SmCyVirtualScreen),
                    left: User32.GetSystemMetrics(index: SmXVirtualScreen),
                    screenX: screen.X,
                    screenY: screen.Y,
                    top: User32.GetSystemMetrics(index: SmYVirtualScreen),
                    width: User32.GetSystemMetrics(index: SmCxVirtualScreen)
                )
                : Win32PointerLock.ToAbsolute(
                    height: User32.GetSystemMetrics(index: SmCyScreen),
                    left: 0,
                    screenX: screen.X,
                    screenY: screen.Y,
                    top: 0,
                    width: User32.GetSystemMetrics(index: SmCxScreen)
                ));
            state.WarpReports = 0;
        }
    }
}
