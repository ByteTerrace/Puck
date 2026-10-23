using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

// The Win32 pointer-lock calls need a real foreground window and session cursor. The hardware-only coverage list
// excludes this seam; Win32PointerLock owns its pure eligibility and centre calculations. Absolute devices retain
// Win32AbsolutePointer's no-warp behavior: their client cursor does not follow a session-side warp.
internal sealed partial class Win32NativeWindow {
    // Locks the pointer for a drag: only the foreground window takes the mouse, and a window with no client area has no
    // centre to hold the cursor at.
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

        // No ClipCursor: the capture already routes every mouse message here wherever the cursor is, the recentre
        // brings it back, and confining it pins a Remote Desktop session's cursor, which then reports no motion.
        _ = User32.SetCursor(cursorHandle: ClientCursor);
        RecentrePointer();
    }
    // Frees a locked pointer, at the point the lock began when restoring, and gives back the capture no held button
    // keeps.
    private void EndPointerLock(bool restore) {
        if (!m_locksPointer) {
            return;
        }

        m_locksPointer = false;

        if (restore && !m_lastRawMouseAbsolute) {
            WarpPointer(screen: m_lockRestore);
        }
        if (m_pointerButtons == 0) {
            ReleasePointerCapture();
        }
        if (m_lastMouseX is not null) {
            _ = User32.SetCursor(cursorHandle: ClientCursor);
        }
    }
    // Returns a locked pointer to the client area's centre once it has left it, after the pump has taken the motion
    // that moved it, while the lock holds it.
    private void RecentrePointer() {
        if (
            HoldsCursor &&
            User32.GetCursorPos(point: out var at) &&
            ((at.X != m_lockCentre.X) || (at.Y != m_lockCentre.Y))
        ) {
            WarpPointer(screen: m_lockCentre);
        }
    }
    // Moves the cursor to a screen point and rebases the window-message fallback's last client point on it, so the move
    // itself reports no motion.
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
    }
}
