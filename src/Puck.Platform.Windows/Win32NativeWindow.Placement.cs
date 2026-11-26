using System.Runtime.InteropServices;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

// Live placement over the host's own primitives: AdjustWindowRectEx turns a client size into the frame SetWindowPos
// takes, and MonitorFromWindow/GetMonitorInfo name the monitor and its work area. SetWindowPos sends WM_SIZE to this
// window procedure before it returns, so a size change reaches Width, Height and ResizeCount exactly as a drag of the
// window's edge does, and the presenter follows it on its next frame.
internal sealed partial class Win32NativeWindow : INativeWindowPlacement {
    private const int GwlExStyle = -20;
    // The widest invisible resize border DWM's visible frame may sit inside the window rectangle by. A larger inset
    // means the two rectangles are in different coordinate spaces (DWMWA_EXTENDED_FRAME_BOUNDS is in physical pixels
    // whatever the caller's DPI awareness), so the window rectangle stands as the visible frame.
    private const int MaxFrameInset = 64;
    private const int SwRestore = 9;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoSize = 0x0001;
    private const uint WmGetMinMaxInfo = 0x0024;

    // The frame the last client-size request needs. WM_GETMINMAXINFO lets the window take it even past the system's
    // default tracking limit (the desktop plus one frame), which a client the size of its monitor passes.
    private Point m_requestedFrameSize;

    /// <inheritdoc/>
    public NativeWindowPlacement Placement {
        get {
            ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

            NativeDesktopArea? client = null;
            string? monitorName = null;
            NativeDesktopArea? monitorArea = null;
            NativeDesktopArea? workArea = null;
            var origin = new Point();

            if ((m_windowHandle != 0) && User32.ClientToScreen(point: ref origin, windowHandle: m_windowHandle)) {
                client = new NativeDesktopArea(X: origin.X, Y: origin.Y, Width: checked((int)Width), Height: checked((int)Height));
            }
            if (TryReadMonitor(monitor: out var monitor)) {
                monitorName = monitor.DeviceName;
                monitorArea = AreaOf(rectangle: monitor.MonitorRectangle);
                workArea = AreaOf(rectangle: monitor.WorkRectangle);
            }

            return new NativeWindowPlacement(
                Client: client,
                Fullscreen: m_isFullscreen,
                Height: Height,
                Monitor: monitorName,
                MonitorArea: monitorArea,
                Width: Width,
                WorkArea: workArea
            );
        }
    }

    /// <inheritdoc/>
    public bool TrySetClientSize(uint width, uint height, out string? refusal) {
        refusal = NativeWindowPlacement.RefuseClientSize(height: height, width: width);
        if ((refusal is not null) || !TryRestoreWindowed(refusal: out refusal)) {
            return false;
        }

        var frame = new Rectangle {
            Bottom = ((int)height),
            Right = ((int)width),
        };

        if (!User32.AdjustWindowRectEx(
            extendedStyle: unchecked((uint)User32.GetWindowLongPtr(index: GwlExStyle, windowHandle: m_windowHandle).ToInt64()),
            hasMenu: false,
            rectangle: ref frame,
            style: unchecked((uint)User32.GetWindowLongPtr(index: GwlStyle, windowHandle: m_windowHandle).ToInt64())
        )) {
            refusal = $"AdjustWindowRectEx failed with Win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }

        m_requestedFrameSize = new Point {
            X = (frame.Right - frame.Left),
            Y = (frame.Bottom - frame.Top),
        };

        return TrySetWindowPosition(
            flags: (SwpNoMove | SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate),
            height: m_requestedFrameSize.Y,
            refusal: out refusal,
            width: m_requestedFrameSize.X,
            x: 0,
            y: 0
        );
    }
    /// <inheritdoc/>
    public bool TryMoveClient(int x, int y, out string? refusal) {
        if (!TryRestoreWindowed(refusal: out refusal) || !TryReadFrame(frame: out var frame, refusal: out refusal)) {
            return false;
        }

        var origin = new Point();

        if (!User32.ClientToScreen(point: ref origin, windowHandle: m_windowHandle)) {
            refusal = "ClientToScreen failed";
            return false;
        }

        return TrySetWindowPosition(
            flags: (SwpNoSize | SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate),
            height: 0,
            refusal: out refusal,
            width: 0,
            x: (x - (origin.X - frame.Left)),
            y: (y - (origin.Y - frame.Top))
        );
    }
    /// <inheritdoc/>
    public bool TryCenter(out string? refusal) {
        if (!TryRestoreWindowed(refusal: out refusal) || !TryReadFrame(frame: out var frame, refusal: out refusal)) {
            return false;
        }
        if (!TryReadMonitor(monitor: out var monitor)) {
            refusal = "no monitor holds the window";
            return false;
        }

        var visible = VisibleFrameOf(frame: frame);
        var work = monitor.WorkRectangle;
        var left = (work.Left + (((work.Right - work.Left) - (visible.Right - visible.Left)) / 2));
        // A window taller than the work area keeps its title bar at the area's top, where it can still be dragged.
        var top = (work.Top + Math.Max(val1: 0, val2: (((work.Bottom - work.Top) - (visible.Bottom - visible.Top)) / 2)));

        return TrySetWindowPosition(
            flags: (SwpNoSize | SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate),
            height: 0,
            refusal: out refusal,
            width: 0,
            x: (left - (visible.Left - frame.Left)),
            y: (top - (visible.Top - frame.Top))
        );
    }
    /// <inheritdoc/>
    public bool TrySetFullscreen(bool fullscreen, out string? refusal) {
        refusal = null;
        if (m_windowHandle == 0) {
            refusal = "the window is closed";
            return false;
        }
        if (fullscreen == m_isFullscreen) {
            return true;
        }

        try {
            if (fullscreen) {
                EnterFullscreen(windowHandle: m_windowHandle);
            } else {
                ExitFullscreen(windowHandle: m_windowHandle);
            }
        } catch (InvalidOperationException exception) {
            refusal = exception.Message;
            return false;
        }

        return true;
    }

    // WM_GETMINMAXINFO arrives with the system's defaults filled in; the tracking limit only ever rises, to the frame the
    // last client-size request needs.
    private void RaiseTrackingLimit(nint lParam) {
        if ((m_requestedFrameSize.X == 0) || (lParam == 0)) {
            return;
        }

        var limits = Marshal.PtrToStructure<MinMaxInfo>(ptr: lParam);

        limits.MaxTrackSize.X = Math.Max(val1: limits.MaxTrackSize.X, val2: m_requestedFrameSize.X);
        limits.MaxTrackSize.Y = Math.Max(val1: limits.MaxTrackSize.Y, val2: m_requestedFrameSize.Y);
        Marshal.StructureToPtr(fDeleteOld: false, ptr: lParam, structure: limits);
    }
    // Placement applies to a window: one filling its monitor, minimized or maximized returns to its windowed bounds first.
    private bool TryRestoreWindowed(out string? refusal) {
        if (m_windowHandle == 0) {
            refusal = "the window is closed";
            return false;
        }
        if (m_isFullscreen && !TrySetFullscreen(fullscreen: false, refusal: out refusal)) {
            return false;
        }
        if (User32.IsIconic(windowHandle: m_windowHandle) || User32.IsZoomed(windowHandle: m_windowHandle)) {
            _ = User32.ShowWindow(command: SwRestore, windowHandle: m_windowHandle);
        }

        refusal = null;
        return true;
    }
    private bool TryReadFrame(out Rectangle frame, out string? refusal) {
        if (!User32.GetWindowRect(rectangle: out frame, windowHandle: m_windowHandle)) {
            refusal = $"GetWindowRect failed with Win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }

        refusal = null;
        return true;
    }
    private bool TryReadMonitor(out MonitorInfoEx monitor) {
        monitor = new MonitorInfoEx { Size = ((uint)Marshal.SizeOf<MonitorInfoEx>()) };
        if (m_windowHandle == 0) {
            return false;
        }

        var handle = User32.MonitorFromWindow(flags: MonitorDefaultToNearest, windowHandle: m_windowHandle);

        return ((handle != 0) && User32.GetMonitorInfoEx(monitorHandle: handle, monitorInfo: ref monitor));
    }
    private bool TrySetWindowPosition(int flags, int x, int y, int width, int height, out string? refusal) {
        if (!User32.SetWindowPos(
            flags: ((uint)flags),
            height: height,
            insertAfterHandle: 0,
            width: width,
            windowHandle: m_windowHandle,
            x: x,
            y: y
        )) {
            refusal = $"SetWindowPos failed with Win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }

        refusal = null;
        return true;
    }
    // The frame a person sees: the window rectangle less the invisible resize borders DWM draws outside it.
    private Rectangle VisibleFrameOf(Rectangle frame) {
        if (Dwmapi.DwmGetWindowAttribute(
            attribute: Dwmapi.ExtendedFrameBounds,
            size: ((uint)Marshal.SizeOf<Rectangle>()),
            value: out var visible,
            windowHandle: m_windowHandle
        ) != 0) {
            return frame;
        }

        var insets = (Left: (visible.Left - frame.Left), Top: (visible.Top - frame.Top), Right: (frame.Right - visible.Right), Bottom: (frame.Bottom - visible.Bottom));

        return (
            (insets.Left is >= 0 and <= MaxFrameInset) &&
            (insets.Top is >= 0 and <= MaxFrameInset) &&
            (insets.Right is >= 0 and <= MaxFrameInset) &&
            (insets.Bottom is >= 0 and <= MaxFrameInset)
        )
            ? visible
            : frame;
    }
    private static NativeDesktopArea AreaOf(Rectangle rectangle) => new(
        Height: (rectangle.Bottom - rectangle.Top),
        Width: (rectangle.Right - rectangle.Left),
        X: rectangle.Left,
        Y: rectangle.Top
    );
}
