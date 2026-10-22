using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Commands;
using Puck.Input;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

/// <summary>
/// A whole monitor a monitor capture shows, as a passthrough input target: what it is given reaches Windows as the
/// mouse and keyboard's own input (<c>SendInput</c>), so the taskbar, the Start menu and every window on the monitor
/// take it, and a window it presses comes forward as a click would bring it.
/// </summary>
/// <remarks>
/// <para>Every point is read and every input sent in physical pixels (per-monitor aware), whatever this process's
/// awareness. A pointer press on the picture is replayed when it is released, as one batch the system queues in order:
/// the cursor moves to the press's point, presses, moves to where the drag ended, releases, and moves back to where it
/// was, so the press lands on whatever Windows shows there while the cursor stays where the local user put it. A press
/// whose point shows a window of this process (the host's own window, mirrored in the picture) is replayed nowhere, so
/// nothing the host sends reaches it, and a click on its mirror cannot repeat itself.</para>
/// <para>Window-pump-thread only: the chord window this registers the reserved chord on is pumped by the thread that
/// claims it.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class Win32DesktopPassthrough : ISourcePassthroughDesktop {
    private const uint AncestorRoot = 2;
    // How far below its frame's top edge a window's caption is clicked, in physical pixels.
    private const int CaptionInset = 12;
    private const nint HitCaption = 2;
    private const uint WmNcHitTest = 0x0084;
    // The extra information every input this sends carries (dwExtraInfo, which GetMessageExtraInfo reads back): the
    // ASCII bytes PUCK, naming the input as the host's own.
    private const nint Signature = 0x4B435550;
    private const int ChordId = 0x5043;
    private const nint MessageOnlyParent = -3;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseHorizontalWheel = 0x1000;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseMove = 0x0001;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseWheel = 0x0800;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
    private const nint PerMonitorAwareV2 = -4;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int VkEscape = 0x1B;
    private const int VkMenu = 0x12;
    private const int WheelDelta = 120;
    private const string ChordClassName = "Puck.DesktopChord";
    private const uint WmHotKey = 0x0312;

    private static readonly Dictionary<nint, Win32DesktopPassthrough> ChordOwners = [];
    private static readonly WndProc ChordProcedure = ChordWindowProcedure;
    private static bool ChordClassRegistered;

    private readonly nint m_monitor;
    private readonly Action<SystemInput[]> m_send;
    private readonly string m_title;
    private readonly Dictionary<int, SourcePassthroughPoint> m_presses = [];

    private nint m_chordWindow;
    private int m_chords;
    // The exact point a pad last moved the cursor to, and the whole pixel it put the cursor on.
    private Vector2? m_padPointer;
    private Point m_padPointerAt;

    /// <summary>Initializes a new instance of the <see cref="Win32DesktopPassthrough"/> class.</summary>
    /// <param name="monitorHandle">The monitor's handle (<c>HMONITOR</c>); nonzero.</param>
    /// <param name="title">The name the monitor is shown by, such as its device name.</param>
    /// <param name="send">The sink every batch of input goes to, in the order the system queues it; <see langword="null"/>
    /// sends it to the system (<c>SendInput</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="monitorHandle"/> is zero.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="title"/> is <see langword="null"/>.</exception>
    public Win32DesktopPassthrough(nint monitorHandle, string title, Action<SystemInput[]>? send = null) {
        if (monitorHandle == 0) {
            throw new ArgumentException(
                message: "A desktop passthrough needs a monitor handle.",
                paramName: nameof(monitorHandle)
            );
        }

        ArgumentNullException.ThrowIfNull(argument: title);
        m_monitor = monitorHandle;
        m_send = (send ?? SendToSystem);
        m_title = title;
    }

    /// <inheritdoc/>
    public bool ClaimsChord {
        get => (m_chordWindow != 0);
        set {
            if (value == ClaimsChord) {
                return;
            }
            if (value) {
                Claim();
            } else {
                Release();
            }
        }
    }
    /// <inheritdoc/>
    public int Height => (TryMonitor(monitor: out var monitor)
        ? (monitor.Bottom - monitor.Top)
        : 0);
    /// <inheritdoc/>
    public string Title => m_title;

    private static nint ChordWindowProcedure(nint windowHandle, uint message, nint wParam, nint lParam) {
        if (
            (message == WmHotKey) &&
            (wParam == ChordId) &&
            ChordOwners.TryGetValue(
                key: windowHandle,
                value: out var owner
            )
        ) {
            owner.m_chords++;

            return 0;
        }

        return User32.DefWindowProc(
            lParam: lParam,
            message: message,
            wParam: wParam,
            windowHandle: windowHandle
        );
    }
    private static T InContext<T>(Func<T> read) {
        var previous = User32.SetThreadDpiAwarenessContext(context: PerMonitorAwareV2);

        try {
            return read();
        } finally {
            if (previous != 0) {
                _ = User32.SetThreadDpiAwarenessContext(context: previous);
            }
        }
    }
    private static SystemInput KeyOf(int virtualKey, byte scanCode, bool extended, bool pressed) => new() {
        Type = SystemInput.Keyboard,
        Union = new SystemInputUnion {
            Keyboard = new KeyboardInput {
                ExtraInfo = Signature,
                Flags = ((pressed ? 0u : KeyEventKeyUp) | (extended ? KeyEventExtended : 0u) | ((scanCode != 0) ? KeyEventScanCode : 0u)),
                ScanCode = scanCode,
                VirtualKey = ((ushort)virtualKey),
            },
        },
    };
    private static SystemInput MouseOf(uint flags, int data = 0, int x = 0, int y = 0) => new() {
        Type = SystemInput.Mouse,
        Union = new SystemInputUnion {
            Mouse = new MouseInput {
                Data = data,
                ExtraInfo = Signature,
                Flags = flags,
                X = x,
                Y = y,
            },
        },
    };
    // An absolute move to a physical screen point, normalized over the virtual desktop as MOUSEEVENTF_VIRTUALDESK reads
    // it: 0 at its left or top edge and 65535 at its last pixel.
    private static SystemInput MoveTo(Point point) {
        var left = User32.GetSystemMetrics(index: SmXVirtualScreen);
        var top = User32.GetSystemMetrics(index: SmYVirtualScreen);
        var width = Math.Max(
            val1: 2,
            val2: User32.GetSystemMetrics(index: SmCxVirtualScreen)
        );
        var height = Math.Max(
            val1: 2,
            val2: User32.GetSystemMetrics(index: SmCyVirtualScreen)
        );

        return MouseOf(
            flags: MouseMove | MouseAbsolute | MouseVirtualDesk,
            x: ((int)Math.Round(a: (((point.X - left) * 65535.0) / (width - 1)))),
            y: ((int)Math.Round(a: (((point.Y - top) * 65535.0) / (height - 1))))
        );
    }
    private static (uint Down, uint Up, int Data) ButtonOf(int button) => button switch {
        0 => (MouseLeftDown, MouseLeftUp, 0),
        1 => (MouseRightDown, MouseRightUp, 0),
        2 => (MouseMiddleDown, MouseMiddleUp, 0),
        3 => (MouseXDown, MouseXUp, 1),
        4 => (MouseXDown, MouseXUp, 2),
        _ => (0u, 0u, 0),
    };
    private void Send(params SystemInput[] inputs) => m_send(inputs);
    private static void SendToSystem(SystemInput[] inputs) => _ = User32.SendInput(
        count: ((uint)inputs.Length),
        inputs: inputs,
        size: Marshal.SizeOf<SystemInput>()
    );
    // Whether a window of this process shows at a physical screen point.
    private static bool IsOwn(Point point) {
        var root = User32.GetAncestor(
            flags: AncestorRoot,
            windowHandle: User32.WindowFromPoint(point: point)
        );

        if (root == 0) {
            return false;
        }

        _ = User32.GetWindowThreadProcessId(
            processId: out var process,
            windowHandle: root
        );

        return (process == ((uint)Environment.ProcessId));
    }
    // The monitor's rectangle in physical pixels.
    private bool TryMonitor(out Rectangle monitor) {
        var info = new MonitorInfo { Size = ((uint)Marshal.SizeOf<MonitorInfo>()) };
        var read = InContext(read: () => User32.GetMonitorInfo(
            monitorHandle: m_monitor,
            monitorInfo: ref info
        ));

        monitor = info.MonitorRectangle;

        return (
            read &&
            (monitor.Right > monitor.Left) &&
            (monitor.Bottom > monitor.Top)
        );
    }
    private bool TryScreen(SourcePassthroughPoint point, out Point screen) {
        screen = default;

        if (!TryMonitor(monitor: out var monitor)) {
            return false;
        }

        screen = new Point {
            X = Math.Clamp(
                max: (monitor.Right - 1),
                min: monitor.Left,
                value: (monitor.Left + ((int)MathF.Floor(x: point.Physical.X)))
            ),
            Y = Math.Clamp(
                max: (monitor.Bottom - 1),
                min: monitor.Top,
                value: (monitor.Top + ((int)MathF.Floor(x: point.Physical.Y)))
            ),
        };

        return true;
    }
    private void Claim() {
        if (!ChordClassRegistered) {
            var windowClass = new WindowClassEx {
                ClassName = ChordClassName,
                InstanceHandle = Kernel32.GetModuleHandle(moduleName: null),
                Size = ((uint)Marshal.SizeOf<WindowClassEx>()),
                WindowProcedure = ChordProcedure,
            };

            _ = User32.RegisterClassEx(windowClass: ref windowClass);
            ChordClassRegistered = true;
        }

        var window = User32.CreateWindowEx(
            className: ChordClassName,
            extendedStyle: 0,
            height: 0,
            instanceHandle: Kernel32.GetModuleHandle(moduleName: null),
            menuHandle: 0,
            parameter: 0,
            parentHandle: MessageOnlyParent,
            style: 0,
            width: 0,
            windowName: string.Empty,
            x: 0,
            y: 0
        );

        if (window == 0) {
            return;
        }
        if (!User32.RegisterHotKey(
            id: ChordId,
            modifiers: ModControl | ModAlt | ModNoRepeat,
            virtualKey: VkEscape,
            windowHandle: window
        )) {
            _ = User32.DestroyWindow(windowHandle: window);

            return;
        }

        ChordOwners[window] = this;
        m_chordWindow = window;
        m_chords = 0;
    }
    private void Release() {
        _ = User32.UnregisterHotKey(
            id: ChordId,
            windowHandle: m_chordWindow
        );
        _ = ChordOwners.Remove(key: m_chordWindow);
        _ = User32.DestroyWindow(windowHandle: m_chordWindow);
        m_chordWindow = 0;
        m_chords = 0;
    }
    // Replays a press released at a point: the cursor moves to where it was pressed, presses, moves to where it was
    // released, releases, and returns to where it is now, in one batch the system queues in order.
    private void Replay(int button, SourcePassthroughPoint pressed, SourcePassthroughPoint released) {
        var (down, up, data) = ButtonOf(button: button);

        if (down == 0u) {
            return;
        }

        _ = InContext(read: () => {
            if (
                !TryScreen(
                    point: pressed,
                    screen: out var from
                ) ||
                !TryScreen(
                    point: released,
                    screen: out var to
                ) ||
                IsOwn(point: from) ||
                !User32.GetCursorPos(point: out var back)
            ) {
                return 0;
            }

            Send(
                MoveTo(point: from),
                MouseOf(
                    data: data,
                    flags: down
                ),
                MoveTo(point: to),
                MouseOf(
                    data: data,
                    flags: up
                ),
                MoveTo(point: back)
            );

            return 0;
        });
    }
    private void TurnWheelAt(SourcePassthroughPoint point, Vector2 notches) => _ = InContext(read: () => {
        if (
            !TryScreen(
                point: point,
                screen: out var at
            ) ||
            IsOwn(point: at) ||
            !User32.GetCursorPos(point: out var back)
        ) {
            return 0;
        }

        Send([
            MoveTo(point: at),
            .. WheelOf(notches: notches),
            MoveTo(point: back),
        ]);

        return 0;
    });
    private static SystemInput[] WheelOf(Vector2 notches) {
        var inputs = new List<SystemInput>(capacity: 2);

        if (notches.Y != 0f) {
            inputs.Add(item: MouseOf(
                data: ((int)MathF.Round(x: (notches.Y * WheelDelta))),
                flags: MouseWheel
            ));
        }
        if (notches.X != 0f) {
            inputs.Add(item: MouseOf(
                data: ((int)MathF.Round(x: (notches.X * WheelDelta))),
                flags: MouseHorizontalWheel
            ));
        }

        return [.. inputs];
    }

    /// <inheritdoc/>
    /// <remarks>The desktop takes no key from the host: keys reach whichever window Windows gives them to.</remarks>
    public void DeliverKey(in WindowInputEvent inputEvent) {
    }
    /// <inheritdoc/>
    /// <remarks>A move reaches nothing, since moving the system cursor would move the pointer the host reads; a press
    /// is replayed at its release (<see cref="Win32DesktopPassthrough"/>), and a wheel turn moves the cursor to its
    /// point, turns there and moves it back.</remarks>
    public void DeliverPointer(in WindowInputEvent inputEvent, SourcePassthroughPoint point) {
        switch (inputEvent.Kind) {
            case WindowInputKind.PointerButton when (inputEvent.Phase == CommandPhase.Started):
                m_presses[inputEvent.ButtonIndex] = point;
                break;
            case WindowInputKind.PointerButton:
                if (m_presses.Remove(
                    key: inputEvent.ButtonIndex,
                    value: out var pressed
                )) {
                    Replay(
                        button: inputEvent.ButtonIndex,
                        pressed: pressed,
                        released: point
                    );
                }
                break;
            case WindowInputKind.PointerWheel:
                TurnWheelAt(
                    notches: inputEvent.Vector,
                    point: point
                );
                break;
        }
    }
    /// <inheritdoc/>
    public void MovePointer(Vector2 distance) => _ = InContext(read: () => {
        if (
            !TryMonitor(monitor: out var monitor) ||
            !User32.GetCursorPos(point: out var at)
        ) {
            return 0;
        }

        var from = (((m_padPointer is { } exact) && (m_padPointerAt.X == at.X) && (m_padPointerAt.Y == at.Y))
            ? exact
            : new Vector2(
                x: at.X,
                y: at.Y
            ));
        var to = new Vector2(
            x: Math.Clamp(
                max: (monitor.Right - 1f),
                min: monitor.Left,
                value: (from.X + distance.X)
            ),
            y: Math.Clamp(
                max: (monitor.Bottom - 1f),
                min: monitor.Top,
                value: (from.Y + distance.Y)
            )
        );
        var point = new Point {
            X = ((int)MathF.Floor(x: to.X)),
            Y = ((int)MathF.Floor(x: to.Y)),
        };

        m_padPointer = to;
        m_padPointerAt = point;
        Send(MoveTo(point: point));

        return 0;
    });
    /// <inheritdoc/>
    public void PressKey(KeyCode key, bool pressed) {
        if (Win32VirtualKeys.TryVirtualKeyOf(
            character: '\0',
            isExtended: out var extended,
            key: key,
            scanCode: out var scanCode,
            virtualKey: out var virtualKey
        )) {
            Send(KeyOf(
                extended: extended,
                pressed: pressed,
                scanCode: scanCode,
                virtualKey: virtualKey
            ));
        }
    }
    /// <inheritdoc/>
    public void PressPointer(int button, bool pressed) {
        var (down, up, data) = ButtonOf(button: button);

        if (down != 0u) {
            Send(MouseOf(
                data: data,
                flags: (pressed ? down : up)
            ));
        }
    }
    /// <inheritdoc/>
    /// <remarks>Windows lets a process bring its window forward only while it holds the last input; when it does not,
    /// a tap of Alt, sent as the keyboard's, gives it that before the window comes forward, and where the shell still
    /// keeps the foreground, a click on the window's caption takes it.</remarks>
    public void ReturnForeground() {
        var own = (nint)0;

        _ = User32.EnumThreadWindows(
            callback: (window, _) => {
                if (!User32.IsWindowVisible(windowHandle: window)) {
                    return true;
                }

                own = window;

                return false;
            },
            parameter: 0,
            threadId: Kernel32.GetCurrentThreadId()
        );

        if (
            (own == 0) ||
            (User32.GetForegroundWindow() == own) ||
            User32.SetForegroundWindow(windowHandle: own)
        ) {
            return;
        }

        Send(
            KeyOf(
                extended: false,
                pressed: true,
                scanCode: 0,
                virtualKey: VkMenu
            ),
            KeyOf(
                extended: false,
                pressed: false,
                scanCode: 0,
                virtualKey: VkMenu
            )
        );

        if (User32.SetForegroundWindow(windowHandle: own)) {
            return;
        }

        // The shell's own surfaces (Start, the taskbar's flyouts) keep the foreground from every call; a click on the
        // window's caption, where it shows, dismisses them and brings the window forward as the mouse's would.
        _ = InContext(read: () => {
            if (
                !User32.GetWindowRect(
                    rectangle: out var frame,
                    windowHandle: own
                ) ||
                !User32.GetCursorPos(point: out var back)
            ) {
                return 0;
            }

            var caption = new Point {
                X = ((frame.Left + frame.Right) / 2),
                Y = (frame.Top + CaptionInset),
            };

            if (
                !IsOwn(point: caption) ||
                (User32.SendMessage(
                    lParam: ((nint)((caption.Y << 16) | (caption.X & 0xFFFF))),
                    message: WmNcHitTest,
                    wParam: 0,
                    windowHandle: own
                ) != HitCaption)
            ) {
                return 0;
            }

            Send(
                MoveTo(point: caption),
                MouseOf(flags: MouseLeftDown),
                MouseOf(flags: MouseLeftUp),
                MoveTo(point: back)
            );

            return 0;
        });
    }
    /// <inheritdoc/>
    public bool TakeChord() {
        if (m_chords == 0) {
            return false;
        }

        m_chords = 0;

        return true;
    }
    /// <inheritdoc/>
    /// <remarks>The frame and the client area are the monitor, in physical pixels, at a scale of 1.</remarks>
    public bool TryDescribe(out SourcePassthroughWindow window) {
        if (!TryMonitor(monitor: out var monitor)) {
            window = default;

            return false;
        }

        var width = (monitor.Right - monitor.Left);
        var height = (monitor.Bottom - monitor.Top);

        window = new SourcePassthroughWindow(
            Client: new SourcePixelRect(
                Height: height,
                Width: width,
                X: 0,
                Y: 0
            ),
            DpiScale: 1f,
            FrameHeight: height,
            FrameWidth: width
        );

        return true;
    }
    /// <inheritdoc/>
    public void TurnWheel(Vector2 notches) {
        var inputs = WheelOf(notches: notches);

        if (inputs.Length > 0) {
            Send(inputs);
        }
    }
}
