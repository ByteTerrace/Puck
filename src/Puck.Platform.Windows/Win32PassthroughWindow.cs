using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Commands;
using Puck.Input;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

/// <summary>
/// A top-level window a window capture shows, as a passthrough input target: pointer and key events reach it as posted
/// window messages, the way the window's own message loop reads them, so the window need not be in the foreground.
/// </summary>
/// <remarks>
/// <para>Coordinates are read in two DPI contexts, switched on the calling thread for each read: physical pixels
/// (per-monitor aware) for the frame and the client area the capture shows, and the window's own context for the
/// client coordinates its messages carry, so a DPI-unaware, system-aware or per-monitor-aware window each hears its own
/// units whatever this process's awareness.</para>
/// <para>A pointer message goes to the deepest visible, enabled child under the point, in that child's client
/// coordinates, and a key or text message to the window's thread's keyboard focus when that lies inside the window. A
/// key whose press types text is posted only while Control or Alt is held, since its text arrives as a text event and
/// posts as <c>WM_CHAR</c>; a window whose loop translates a posted key into text would otherwise hear it twice.</para>
/// <para>Window-pump-thread only. A message to a window that has gone is dropped.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class Win32PassthroughWindow : ISourcePassthroughWindow {
    private const uint ChildSkipDisabled = 0x0002;
    private const uint ChildSkipInvisible = 0x0001;
    private const uint ChildSkipTransparent = 0x0004;
    private const int KeyAlt = (1 << 29);
    private const int KeyExtended = (1 << 24);
    private const int KeyPrevious = (1 << 30);
    private const int KeyTransition = unchecked((int)(1u << 31));
    private const int MkControl = 0x0008;
    private const int MkLeftButton = 0x0001;
    private const int MkMiddleButton = 0x0010;
    private const int MkRightButton = 0x0002;
    private const int MkShift = 0x0004;
    private const int MkXButton1 = 0x0020;
    private const int MkXButton2 = 0x0040;
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
    private const nint PerMonitorAwareV2 = -4;
    private const int WheelDelta = 120;
    private const uint WmChar = 0x0102;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmMouseHWheel = 0x020E;
    private const uint WmMouseMove = 0x0200;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmXButtonUp = 0x020C;

    private readonly nint m_handle;

    // The buttons and modifiers this window was delivered and not yet released, which every message's key state carries.
    private int m_buttons;
    private bool m_alt;
    private bool m_control;
    private bool m_shift;

    /// <summary>Initializes a new instance of the <see cref="Win32PassthroughWindow"/> class.</summary>
    /// <param name="windowHandle">The top-level window's handle; nonzero.</param>
    /// <exception cref="ArgumentException"><paramref name="windowHandle"/> is zero.</exception>
    public Win32PassthroughWindow(nint windowHandle) {
        if (windowHandle == 0) {
            throw new ArgumentException(
                message: "A passthrough window needs a window handle.",
                paramName: nameof(windowHandle)
            );
        }

        m_handle = windowHandle;
    }

    /// <summary>Gets the window's handle.</summary>
    public nint Handle => m_handle;
    /// <inheritdoc/>
    public string Title {
        get {
            if (!User32.IsWindow(windowHandle: m_handle)) {
                return string.Empty;
            }

            var length = User32.GetWindowTextLength(windowHandle: m_handle);

            if (length <= 0) {
                return string.Empty;
            }

            var buffer = new char[(length + 1)];
            var copied = User32.GetWindowText(
                maxLength: buffer.Length,
                text: buffer,
                windowHandle: m_handle
            );

            return new string(
                length: Math.Max(
                    val1: 0,
                    val2: copied
                ),
                startIndex: 0,
                value: buffer
            );
        }
    }

    private static nint MakePoint(int x, int y) => ((nint)((y << 16) | (x & 0xFFFF)));
    private int KeyState() => m_buttons |
        (m_control ? MkControl : 0) |
        (m_shift ? MkShift : 0);
    // Runs a read in a DPI awareness context on this thread, restoring the thread's own afterwards.
    private static T InContext<T>(nint context, Func<T> read) {
        var previous = User32.SetThreadDpiAwarenessContext(context: context);

        try {
            return read();
        } finally {
            if (previous != 0) {
                _ = User32.SetThreadDpiAwarenessContext(context: previous);
            }
        }
    }
    // The deepest visible, enabled child under a client point of the window, and the point in its client coordinates;
    // read in the window's own DPI context.
    private (nint Target, Point Point) ChildAt(Point point) {
        var target = m_handle;

        while (true) {
            var child = User32.ChildWindowFromPointEx(
                flags: ChildSkipInvisible | ChildSkipDisabled | ChildSkipTransparent,
                parentHandle: target,
                point: point
            );

            if (
                (child == 0) ||
                (child == target)
            ) {
                return (target, point);
            }

            _ = User32.MapWindowPoints(
                count: 1u,
                fromHandle: target,
                points: ref point,
                toHandle: child
            );
            target = child;
        }
    }
    // The window's thread's keyboard focus when it lies inside the window, else the window itself.
    private nint KeyTarget() {
        var thread = User32.GetWindowThreadProcessId(
            processId: out _,
            windowHandle: m_handle
        );
        var info = new GuiThreadInfo { Size = ((uint)Marshal.SizeOf<GuiThreadInfo>()) };

        if (
            (thread != 0) &&
            User32.GetGUIThreadInfo(
                info: ref info,
                threadId: thread
            ) &&
            (info.Focus != 0) &&
            ((info.Focus == m_handle) || User32.IsChild(
                parentHandle: m_handle,
                windowHandle: info.Focus
            ))
        ) {
            return info.Focus;
        }

        return m_handle;
    }
    private void TrackModifier(KeyCode key, bool down) {
        switch (key) {
            case KeyCode.ControlLeft or KeyCode.ControlRight:
                m_control = down;
                break;
            case KeyCode.AltLeft or KeyCode.AltRight:
                m_alt = down;
                break;
            case KeyCode.ShiftLeft or KeyCode.ShiftRight:
                m_shift = down;
                break;
        }
    }

    /// <inheritdoc/>
    public void DeliverKey(in WindowInputEvent inputEvent) {
        if (!User32.IsWindow(windowHandle: m_handle)) {
            return;
        }

        var target = KeyTarget();

        if (inputEvent.Kind == WindowInputKind.Text) {
            foreach (var unit in (inputEvent.Text ?? string.Empty)) {
                _ = User32.PostMessage(
                    lParam: 1,
                    message: WmChar,
                    wParam: unit,
                    windowHandle: target
                );
            }

            return;
        }
        if (inputEvent.Kind != WindowInputKind.Key) {
            return;
        }

        var down = (inputEvent.Phase is not (CommandPhase.Completed or CommandPhase.Canceled));

        TrackModifier(
            down: down,
            key: inputEvent.Key
        );

        if (
            (Win32VirtualKeys.Types(key: inputEvent.Key) && !(m_control || m_alt)) ||
            !Win32VirtualKeys.TryVirtualKeyOf(
                character: inputEvent.Character,
                isExtended: out var extended,
                key: inputEvent.Key,
                scanCode: out var scanCode,
                virtualKey: out var virtualKey
            )
        ) {
            return;
        }

        // An Alt chord without Control is a system key, as the window's own keyboard would report it.
        var system = (m_alt && !m_control);
        var lParam = 1 | (scanCode << 16) | (extended ? KeyExtended : 0) | (m_alt ? KeyAlt : 0) | (down ? 0 : KeyPrevious | KeyTransition);

        _ = User32.PostMessage(
            lParam: lParam,
            message: (down
                ? (system ? WmSysKeyDown : WmKeyDown)
                : (system ? WmSysKeyUp : WmKeyUp)
            ),
            wParam: virtualKey,
            windowHandle: target
        );
    }
    /// <inheritdoc/>
    public void DeliverPointer(in WindowInputEvent inputEvent, SourcePassthroughPoint point) {
        if (!User32.IsWindow(windowHandle: m_handle)) {
            return;
        }

        var client = new Point {
            X = ((int)MathF.Floor(x: point.Logical.X)),
            Y = ((int)MathF.Floor(x: point.Logical.Y)),
        };
        var kind = inputEvent.Kind;
        var button = inputEvent.ButtonIndex;
        var down = (inputEvent.Phase == CommandPhase.Started);
        var wheel = inputEvent.Vector;

        // Every read of the window's coordinates, and each message's key state, is in the window's own units.
        _ = InContext(
            context: User32.GetWindowDpiAwarenessContext(windowHandle: m_handle),
            read: () => {
                var (target, local) = ChildAt(point: client);

                switch (kind) {
                    case WindowInputKind.PointerPosition:
                        _ = User32.PostMessage(
                            lParam: MakePoint(
                                x: local.X,
                                y: local.Y
                            ),
                            message: WmMouseMove,
                            wParam: KeyState(),
                            windowHandle: target
                        );
                        break;
                    case WindowInputKind.PointerButton:
                        var (message, flag, extra) = button switch {
                            0 => ((down ? WmLButtonDown : WmLButtonUp), MkLeftButton, 0),
                            1 => ((down ? WmRButtonDown : WmRButtonUp), MkRightButton, 0),
                            2 => ((down ? WmMButtonDown : WmMButtonUp), MkMiddleButton, 0),
                            3 => ((down ? WmXButtonDown : WmXButtonUp), MkXButton1, (1 << 16)),
                            4 => ((down ? WmXButtonDown : WmXButtonUp), MkXButton2, (2 << 16)),
                            _ => (0u, 0, 0),
                        };

                        if (message == 0u) {
                            break;
                        }

                        m_buttons = (down
                            ? m_buttons | flag
                            : m_buttons & ~flag
                        );
                        _ = User32.PostMessage(
                            lParam: MakePoint(
                                x: local.X,
                                y: local.Y
                            ),
                            message: message,
                            wParam: KeyState() | extra,
                            windowHandle: target
                        );
                        break;
                    case WindowInputKind.PointerWheel:
                        // A wheel message carries the pointer in screen coordinates.
                        var screen = client;

                        _ = User32.ClientToScreen(
                            point: ref screen,
                            windowHandle: m_handle
                        );

                        var at = MakePoint(
                            x: screen.X,
                            y: screen.Y
                        );

                        if (wheel.Y != 0f) {
                            _ = User32.PostMessage(
                                lParam: at,
                                message: WmMouseWheel,
                                wParam: (((int)MathF.Round(x: (wheel.Y * WheelDelta))) << 16) | KeyState(),
                                windowHandle: target
                            );
                        }
                        if (wheel.X != 0f) {
                            _ = User32.PostMessage(
                                lParam: at,
                                message: WmMouseHWheel,
                                wParam: (((int)MathF.Round(x: (wheel.X * WheelDelta))) << 16) | KeyState(),
                                windowHandle: target
                            );
                        }
                        break;
                }

                return 0;
            }
        );
    }
    /// <inheritdoc/>
    public bool TryDescribe(out SourcePassthroughWindow window) {
        window = default;

        if (!User32.IsWindow(windowHandle: m_handle)) {
            return false;
        }

        // The frame and the client area in physical pixels, and the client area in the window's own units.
        var (frameRead, frame, clientRead, client, origin) = InContext(
            context: PerMonitorAwareV2,
            read: () => {
                var frameRead = (Dwmapi.DwmGetWindowAttribute(
                    attribute: Dwmapi.ExtendedFrameBounds,
                    size: ((uint)Marshal.SizeOf<Rectangle>()),
                    value: out var frame,
                    windowHandle: m_handle
                ) == 0);
                var clientRead = User32.GetClientRect(
                    rectangle: out var client,
                    windowHandle: m_handle
                );
                var origin = new Point();

                clientRead &= User32.ClientToScreen(
                    point: ref origin,
                    windowHandle: m_handle
                );

                return (frameRead, frame, clientRead, client, origin);
            }
        );
        var ownWidth = InContext(
            context: User32.GetWindowDpiAwarenessContext(windowHandle: m_handle),
            read: () => (User32.GetClientRect(
                rectangle: out var own,
                windowHandle: m_handle
            )
                ? own.Right
                : 0)
        );
        var frameWidth = (frame.Right - frame.Left);
        var frameHeight = (frame.Bottom - frame.Top);

        if (
            !frameRead ||
            !clientRead ||
            (client.Right <= 0) ||
            (client.Bottom <= 0) ||
            (frameWidth <= 0) ||
            (frameHeight <= 0)
        ) {
            return false;
        }

        window = new SourcePassthroughWindow(
            Client: new SourcePixelRect(
                Height: client.Bottom,
                Width: client.Right,
                X: (origin.X - frame.Left),
                Y: (origin.Y - frame.Top)
            ),
            DpiScale: ((ownWidth > 0)
                ? (client.Right / ((float)ownWidth))
                : 1f
            ),
            FrameHeight: frameHeight,
            FrameWidth: frameWidth
        );

        return true;
    }
}
