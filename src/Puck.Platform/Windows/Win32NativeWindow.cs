using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Puck.Commands;
using Puck.Input;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

internal sealed partial class Win32NativeWindow : INativeWindow, IWindowInputSource {
    private const int CwUseDefault = unchecked((int)0x80000000);
    private const int ErrorClassAlreadyExists = 1410;
    // WM_KEYDOWN/WM_KEYUP lParam bit 24 — set for the right-hand Control/Alt (and numpad Enter), clear for the
    // left-hand ones. Shift carries no such distinction (see MapvkVscToVkEx below).
    private const long ExtendedKeyBit = 0x01000000;
    private const int GwlStyle = -16;
    private const int GwlpUserData = -21;
    // MapVirtualKey map type: scan code -> the LEFT/RIGHT-distinguishing extended virtual key. The only way to
    // tell VK_LSHIFT from VK_RSHIFT — Shift's lParam extended-key bit is never set for either side.
    private const uint MapvkVscToVkEx = 0x03;
    private const int MonitorDefaultToNearest = 2;
    private const int PmRemove = 0x0001;
    private const int SwShow = 5;
    private const int SwpFrameChanged = 0x0020;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoOwnerZOrder = 0x0200;
    private const int SwpNoZOrder = 0x0004;
    private const int VkA = 0x41;
    private const int VkBack = 0x08;
    private const int VkC = 0x43;
    private const int VkControl = 0x11;
    private const int VkDown = 0x28;
    private const int VkEscape = 0x1B;
    private const int VkF1 = 0x70;
    private const int VkF10 = 0x79;
    private const int VkF11 = 0x7A;
    private const int VkF12 = 0x7B;
    private const int VkF2 = 0x71;
    private const int VkF3 = 0x72;
    private const int VkF4 = 0x73;
    private const int VkF5 = 0x74;
    private const int VkF6 = 0x75;
    private const int VkF7 = 0x76;
    private const int VkF8 = 0x77;
    private const int VkF9 = 0x78;
    private const int VkLWin = 0x5B;
    private const int VkLeft = 0x25;
    private const int VkMenu = 0x12;
    private const int VkOem3 = 0xC0;
    private const int VkRShift = 0xA1;
    private const int VkRWin = 0x5C;
    private const int VkReturn = 0x0D;
    private const int VkRight = 0x27;
    private const int VkShift = 0x10;
    private const int VkSpace = 0x20;
    private const int VkTab = 0x09;
    private const int VkUp = 0x26;
    private const int VkV = 0x56;
    private const int VkZ = 0x5A;
    private const uint WmChar = 0x0102;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmInput = 0x00FF;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmKillFocus = 0x0008;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmMouseMove = 0x0200;
    private const uint WmMouseWheel = 0x020A;
    // WHEEL_DELTA: the notch quantum WM_MOUSEWHEEL's high word counts in. A free-spin or precision wheel reports
    // FRACTIONS of it, so the neutral event carries the quotient as a float rather than an integer notch count —
    // rounding here would silently drop every sub-notch report a high-resolution wheel makes.
    private const float WheelDelta = 120f;
    private const uint WmNcCreate = 0x0081;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmPaint = 0x000F;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmSetCursor = 0x0020;
    private const uint WmShowWindow = 0x0018;
    private const uint WmSize = 0x0005;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmWindowPosChanged = 0x0047;
    // Raw Input (WM_INPUT): un-accelerated, full-rate relative mouse motion, summed pump-level per frame.
    private const uint RidInput = 0x10000003;
    private const ushort HidUsageGenericMouse = 0x02;
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort RiMouseMoveAbsolute = 0x01;
    private const uint RimTypeMouse = 0;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;

    private static readonly object RegistrationLock = new();
    private static readonly string WindowClassName = "Puck.Win32NativeWindow";
    private static readonly WndProc WndProcDelegate = StaticWindowProcedure;
    private static nint InstanceHandleField;
    private static bool WindowClassRegistered;
    private readonly IClipboardService m_clipboardService;
    private readonly NativeWindowOptions m_options;
    private readonly Queue<WindowInputEvent> m_pendingInput = [];
    private readonly GCHandle m_selfHandle;
    private bool m_disposed;
    private bool m_isFullscreen;
    private bool m_hasPainted;
    private bool m_isOpen = true;
    private bool m_isVisible;
    private Vector2 m_frameMouseDelta;
    private bool m_pointerPositionDirty;
    private bool m_rawMouseRegistered;
    private int? m_lastMouseX;
    private int? m_lastMouseY;
    private int? m_lastRawAbsoluteX;
    private int? m_lastRawAbsoluteY;
    private ulong m_resizeCount;
    private bool m_suppressNextCharacterInput;
    private Rectangle m_windowedBounds;
    private nint m_windowedStyle;
    private nint m_windowHandle;

    public Win32NativeWindow(IClipboardService clipboardService, IOptions<NativeWindowOptions> options) {
        ArgumentNullException.ThrowIfNull(clipboardService);
        ArgumentNullException.ThrowIfNull(options);

        m_clipboardService = clipboardService;
        m_options = options.Value;
        Width = m_options.Width;
        Height = m_options.Height;
        m_selfHandle = GCHandle.Alloc(value: this);

        EnsureWindowClassRegistered();
        m_windowHandle = CreateWindow(options: m_options);
    }

    public NativeDisplayKind DisplayKind => NativeDisplayKind.Win32;
    public bool HasPainted => m_hasPainted;
    public uint Height { get; private set; }
    public bool IsOpen => (!m_disposed && m_isOpen);
    public bool IsVisible => m_isVisible;
    public ulong ResizeCount => m_resizeCount;
    public string Title => m_options.Title;
    public uint Width { get; private set; }

    public void Show() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_windowHandle == 0) {
            throw new InvalidOperationException(message: "The Win32 window handle is not available.");
        }

        if (!User32.ShowWindow(
            command: SwShow,
            windowHandle: m_windowHandle
        )) {
            _ = Marshal.GetLastWin32Error();
        }

        if (
            m_options.StartFullscreen &&
            !m_isFullscreen
        ) {
            EnterFullscreen(windowHandle: m_windowHandle);
        }

        if (m_options.HideMouseCursor) {
            _ = User32.SetCursor(cursorHandle: 0);
        }

        if (!User32.UpdateWindow(windowHandle: m_windowHandle)) {
            throw new InvalidOperationException(message: $"UpdateWindow failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }
    public void PollEvents() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        while (
            (m_windowHandle != 0) &&
            User32.PeekMessage(
                filterMax: 0,
                filterMin: 0,
                message: out var message,
                removeMessage: PmRemove,
                windowHandle: 0
            )
        ) {
            User32.TranslateMessage(message: in message);
            _ = User32.DispatchMessage(message: in message);
        }

        FlushPointerFrame();
    }

    private void FlushPointerFrame() {
        // Emit at most one PointerDelta (the frame's summed relative motion) and one PointerAbsolute per
        // frame, so a high-rate mouse that produced many WM_INPUT packets collapses to a single delta each
        // observer sees once (a per-frame consumer sums or last-wins; one event makes either exact).
        if (m_frameMouseDelta != Vector2.Zero) {
            m_pendingInput.Enqueue(item: WindowInputEvent.PointerDelta(delta: m_frameMouseDelta));
            m_frameMouseDelta = Vector2.Zero;
        }

        if (
            m_pointerPositionDirty &&
            (m_lastMouseX is { } absoluteX) &&
            (m_lastMouseY is { } absoluteY)
        ) {
            m_pendingInput.Enqueue(item: WindowInputEvent.PointerAbsolute(position: new Vector2(
                x: absoluteX,
                y: absoluteY
            )));
            m_pointerPositionDirty = false;
        }
    }

    public bool TryDequeueInput(out WindowInputEvent inputEvent) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_pendingInput.Count == 0) {
            inputEvent = default;
            return false;
        }

        inputEvent = m_pendingInput.Dequeue();
        return true;
    }
    public void Close() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (
            (m_windowHandle == 0) ||
            !m_isOpen
        ) {
            return;
        }

        if (!User32.DestroyWindow(windowHandle: m_windowHandle)) {
            throw new InvalidOperationException(message: $"DestroyWindow failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }
    public NativeSurfaceBinding CreateSurfaceBinding() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_windowHandle == 0) {
            throw new InvalidOperationException(message: "The Win32 window handle is not available.");
        }

        return new NativeSurfaceBinding(
            DisplayKind: DisplayKind,
            Win32: new Win32NativeSurfaceBinding(
                InstanceHandle: InstanceHandleField,
                WindowHandle: m_windowHandle
            )
        );
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (m_windowHandle != 0) {
            _ = User32.DestroyWindow(windowHandle: m_windowHandle);
            m_windowHandle = 0;
        }

        m_precisionTimer?.Dispose();

        if (m_selfHandle.IsAllocated) {
            m_selfHandle.Free();
        }

        m_isOpen = false;
        m_isVisible = false;
        m_disposed = true;
    }

    private static void EnsureWindowClassRegistered() {
        lock (RegistrationLock) {
            if (WindowClassRegistered) {
                return;
            }

            InstanceHandleField = Kernel32.GetModuleHandle(moduleName: null);
            var windowClass = new WindowClassEx {
                ClassName = WindowClassName,
                InstanceHandle = InstanceHandleField,
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = WndProcDelegate,
            };

            var atom = User32.RegisterClassEx(windowClass: ref windowClass);
            var error = Marshal.GetLastWin32Error();

            if (
                (atom == 0) &&
                (error != ErrorClassAlreadyExists)
            ) {
                throw new InvalidOperationException(message: $"RegisterClassExW failed with Win32 error {error}.");
            }

            WindowClassRegistered = true;
        }
    }
    private static nint StaticWindowProcedure(nint windowHandle, uint message, nint wParam, nint lParam) {
        if (message == WmNcCreate) {
            var createStruct = Marshal.PtrToStructure<CreateStruct>(ptr: lParam);

            if (createStruct.CreateParameters != 0) {
                _ = User32.SetWindowLongPtr(
                    index: GwlpUserData,
                    newLong: createStruct.CreateParameters,
                    windowHandle: windowHandle
                );
            }
        }

        var userData = User32.GetWindowLongPtr(
            index: GwlpUserData,
            windowHandle: windowHandle
        );

        if (userData != 0) {
            var handle = GCHandle.FromIntPtr(value: userData);

            if (handle.Target is Win32NativeWindow window) {
                return window.HandleMessage(
                    lParam: lParam,
                    message: message,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
            }
        }

        return User32.DefWindowProc(
            lParam: lParam,
            message: message,
            wParam: wParam,
            windowHandle: windowHandle
        );
    }
    private nint CreateWindow(NativeWindowOptions options) {
        // options.Width/Height are the CLIENT (render/swapchain) size — everything downstream sizes to them: the
        // compositor renders at this resolution and the swapchain follows the client rect. CreateWindowEx takes the
        // OUTER size (title bar + borders included), so without this adjustment a 1280x800 request yielded a
        // ~1264x761 client area and the presented image lost its bottom edge (the overworld's bottom pane row).
        var outer = new Rectangle {
            Bottom = checked((int)options.Height),
            Left = 0,
            Right = checked((int)options.Width),
            Top = 0,
        };

        if (!User32.AdjustWindowRectEx(rectangle: ref outer, style: WsOverlappedWindow, hasMenu: false, extendedStyle: 0)) {
            throw new InvalidOperationException(message: $"AdjustWindowRectEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        var windowHandle = User32.CreateWindowEx(
            className: WindowClassName,
            extendedStyle: 0,
            height: (outer.Bottom - outer.Top),
            instanceHandle: InstanceHandleField,
            menuHandle: 0,
            parameter: GCHandle.ToIntPtr(value: m_selfHandle),
            parentHandle: 0,
            style: WsOverlappedWindow,
            width: (outer.Right - outer.Left),
            windowName: options.Title,
            x: CwUseDefault,
            y: CwUseDefault
        );

        if (windowHandle == 0) {
            throw new InvalidOperationException(message: $"CreateWindowExW failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        RegisterRawMouse(windowHandle: windowHandle);

        return windowHandle;
    }
    private void RegisterRawMouse(nint windowHandle) {
        // Register the generic mouse for raw input (flags 0 = follow focus, foreground only). The launcher
        // focus-gates anyway. On failure, m_rawMouseRegistered stays false and HandleMouseMove derives the
        // delta from WM_MOUSEMOVE instead — feeding the same pump-level accumulator, never both.
        var device = new RawInputDevice {
            Flags = 0,
            TargetWindowHandle = windowHandle,
            Usage = HidUsageGenericMouse,
            UsagePage = HidUsagePageGeneric,
        };

        m_rawMouseRegistered = User32.RegisterRawInputDevices(
            deviceCount: 1,
            rawInputDevices: in device,
            size: (uint)Marshal.SizeOf<RawInputDevice>()
        );
    }
    private nint HandleMessage(nint windowHandle, uint message, nint wParam, nint lParam) {
        if (ShouldSuppressBackgroundErase(message: message)) {
            return 1;
        }

        switch (message) {
            case WmKeyDown:
            case WmSysKeyDown:
                return HandleKeyDown(
                    lParam: lParam,
                    message: message,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
            case WmKeyUp:
            case WmSysKeyUp:
                return HandleKeyUp(
                    lParam: lParam,
                    message: message,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
            case WmInput:
                return HandleRawInput(
                    lParam: lParam,
                    message: message,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
            case WmKillFocus:
                // OS window focus loss (Alt-Tab away, click-away): a modifier key's own release can be delivered
                // to whatever window just stole focus and never reach this process at all, permanently stranding
                // a slot's chord tracker mid-page (the pre-existing lt/rt trigger modifiers self-heal because an
                // analog trigger re-reports its value every tick; a keyboard modifier is edge-only and never
                // re-asserts). Surfaced through the same TryDequeueInput path as every other window event so the
                // pump can release every held command AND every slot's chord state in one place.
                m_pendingInput.Enqueue(item: WindowInputEvent.FocusLost());
                return 0;
            case WmShowWindow:
                m_isVisible = (wParam != 0);
                return 0;
            case WmSize:
                var previousWidth = Width;
                var previousHeight = Height;
                Width = GetWidthFromSizeLParam(lParam: lParam);
                Height = GetHeightFromSizeLParam(lParam: lParam);
                if (
                    (Width != previousWidth) ||
                    (Height != previousHeight)
                ) {
                    m_resizeCount++;
                }

                return 0;
            case WmDisplayChange:
                // Display mode/topology changed — signal timing or advertised VRR capabilities may differ; re-query. Still forward to
                // DefWindowProc so any default processing runs.
                OnDisplayConfigurationChanged();
                return User32.DefWindowProc(
                    lParam: lParam,
                    message: message,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
            case WmWindowPosChanged:
                // A move/resize/z-order change; bump the refresh-config version only if the window crossed to a different
                // monitor. MUST forward to DefWindowProc so it still generates WM_SIZE/WM_MOVE (the resize path depends on it).
                OnWindowPositionChanged(windowHandle: windowHandle);
                return User32.DefWindowProc(
                    lParam: lParam,
                    message: message,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
            case WmPaint:
                _ = User32.BeginPaint(
                    paintStruct: out var paintStruct,
                    windowHandle: windowHandle
                );
                m_hasPainted = true;

                if (!User32.EndPaint(
                    paintStruct: in paintStruct,
                    windowHandle: windowHandle
                )) {
                    throw new InvalidOperationException(message: $"EndPaint failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                return 0;
            case WmSetCursor:
                return HandleSetCursor(
                    lParam: lParam,
                    message: message,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
            case WmChar:
                return HandleCharacterInput(
                    lParam: lParam,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
            case WmMouseMove:
                return HandleMouseMove(lParam: lParam);
            case WmMouseWheel:
                return HandleMouseWheel(wParam: wParam);
            case WmLButtonDown:
                return HandlePointerButtonDown(button: 0, windowHandle: windowHandle);
            case WmLButtonUp:
                return HandlePointerButtonUp(button: 0);
            case WmRButtonDown:
                return HandlePointerButtonDown(button: 1, windowHandle: windowHandle);
            case WmRButtonUp:
                return HandlePointerButtonUp(button: 1);
            case WmMButtonDown:
                return HandlePointerButtonDown(button: 2, windowHandle: windowHandle);
            case WmMButtonUp:
                return HandlePointerButtonUp(button: 2);
            case WmClose:
                _ = User32.DestroyWindow(windowHandle: windowHandle);
                return 0;
            case WmDestroy:
                m_isOpen = false;
                m_isVisible = false;
                return 0;
            case WmNcDestroy:
                m_windowHandle = 0;
                m_isOpen = false;
                m_isVisible = false;
                return User32.DefWindowProc(
                    lParam: lParam,
                    message: message,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
            default:
                return User32.DefWindowProc(
                    lParam: lParam,
                    message: message,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
        }
    }
    private nint HandleSetCursor(nint windowHandle, uint message, nint wParam, nint lParam) {
        if (!m_options.HideMouseCursor) {
            return User32.DefWindowProc(
                lParam: lParam,
                message: message,
                wParam: wParam,
                windowHandle: windowHandle
            );
        }

        _ = User32.SetCursor(cursorHandle: 0);
        return 1;
    }
    private nint HandleCharacterInput(nint windowHandle, nint wParam, nint lParam) {
        var character = checked((char)wParam.ToInt64());

        // The console-toggle keydown is consumed as a command, but TranslateMessage
        // still emits its WM_CHAR — swallowing exactly that character keeps the toggle
        // from typing into the console it just opened. Only the toggle key's own
        // characters are swallowed, so synthetic toggles posted without a translated
        // character (the PostMessage verification workflow) never eat real text.
        if (m_suppressNextCharacterInput) {
            m_suppressNextCharacterInput = false;
            if (character is '`' or '~') {
                return 0;
            }
        }

        if (!char.IsControl(c: character)) {
            m_pendingInput.Enqueue(item: WindowInputEvent.TypedText(text: character.ToString()));
        }

        return 0;
    }
    private nint HandleKeyDown(nint windowHandle, uint message, nint wParam, nint lParam) {
        // Any keydown ends the toggle-character suppression window: a key's WM_CHAR
        // always arrives before the next key's WM_KEYDOWN, so a toggle keydown that
        // produced no character (other layouts, posted messages) can't swallow an
        // unrelated character later.
        m_suppressNextCharacterInput = false;

        if (IsAltEnterGesture(
            altKeyState: User32.GetKeyState(virtualKey: VkMenu),
            message: message,
            wParam: wParam
        )) {
            ToggleFullscreen(windowHandle: windowHandle);
            return 0;
        }

        // Ctrl+V pastes: the clipboard text flows through the text pipeline. The chord is consumed WHETHER OR NOT the
        // clipboard has text — otherwise an empty clipboard would let Ctrl+V fall through to the letter case below
        // and emit a LetterDown('v', Control) that a full clipboard suppresses: a clipboard-state-dependent binding
        // behavior no consumer could reason about. Exactly Control (no Shift/Alt/Super) so Ctrl+Shift+V still falls
        // through to its letter signal rather than also pasting.
        if (
            IsKeyDown(virtualKey: VkControl) &&
            !IsKeyDown(virtualKey: VkShift) &&
            !IsKeyDown(virtualKey: VkMenu) &&
            !IsKeyDown(virtualKey: VkLWin) &&
            !IsKeyDown(virtualKey: VkRWin) &&
            (wParam.ToInt64() == VkV)
        ) {
            if (
                m_clipboardService.TryGetText(text: out var clipboardText) &&
                (clipboardText.Length > 0)
            ) {
                m_pendingInput.Enqueue(item: WindowInputEvent.TypedText(text: clipboardText));
            }

            return 0;
        }

        switch (wParam.ToInt64()) {
            case VkOem3:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Backtick));
                m_suppressNextCharacterInput = true;
                return 0;
            case VkBack:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Backspace));
                return 0;
            case VkEscape:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Escape));
                return 0;
            case VkReturn:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Enter));
                return 0;
            case VkF1:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F1));
                return 0;
            case VkF2:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F2));
                return 0;
            case VkF3:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F3));
                return 0;
            case VkF4:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F4));
                return 0;
            case VkF5:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F5));
                return 0;
            case VkF6:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F6));
                return 0;
            case VkF7:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F7));
                return 0;
            case VkF8:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F8));
                return 0;
            case VkF9:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F9));
                return 0;
            case VkF10:
                // F10 alone is ALSO a system key (it activates keyboard menu-navigation mode via WM_SYSKEYDOWN,
                // the same route that carries Alt+letter mnemonics) — handling it here and returning 0 suppresses
                // that the same way the Alt+F4/Alt+Space/Alt+letter cases already suppress theirs.
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F10));
                return 0;
            case VkF11:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F11));
                return 0;
            case VkF12:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F12));
                return 0;
            case VkUp:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.ArrowUp));
                return 0;
            case VkDown:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.ArrowDown));
                return 0;
            case VkLeft:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.ArrowLeft));
                return 0;
            case VkRight:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.ArrowRight));
                return 0;
            case VkSpace:
                // Space is a first-class named key (the world binds it to the jump action lane, held for variable
                // height). Its WM_CHAR (a literal ' ') still arrives independently — TranslateMessage runs before
                // dispatch — so typed text keeps flowing to the text pipeline; an unbound Space signal is ignored by
                // the binding table. Mirrors the letter/arrow keys: down here, up via TryMapNamedKey in HandleKeyUp.
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Space));
                return 0;
            case VkTab:
                // Tab is a first-class named key, chorded or plain — bare Tab drives the radial action menu's hold
                // binding, so its down edge must reach the pipeline exactly as its up edge always has
                // (TryMapNamedKey maps VkTab unconditionally in HandleKeyUp). Consuming it here also keeps
                // DefWindowProc from treating it as dialog navigation.
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Tab));
                return 0;
            case VkControl:
                // The generic Control VK; the lParam extended-key bit resolves the physical side (set for the
                // right key, clear for the left — see ExtendedKeyBit's remarks). Was previously unhandled here
                // and fell to DefWindowProc, so bare Ctrl never reached bindings.
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(
                    key: ((IsExtendedKey(lParam: lParam)) ? KeyCode.ControlRight : KeyCode.ControlLeft)
                ));
                return 0;
            case VkMenu:
                // Same side resolution as Control. Bare Alt (no chorded key) previously fell through untouched;
                // consuming it here also suppresses whatever DefWindowProc did with the WM_SYSKEYDOWN it no
                // longer sees (a bare Alt tap normally arms keyboard menu-navigation mode).
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(
                    key: ((IsExtendedKey(lParam: lParam)) ? KeyCode.AltRight : KeyCode.AltLeft)
                ));
                return 0;
            case VkShift:
                // Shift carries no extended-key bit on either side; MapVirtualKey on the scan code is the only
                // way to resolve which physical key fired — see MapvkVscToVkEx's remarks.
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: ResolveShiftSide(lParam: lParam)));
                return 0;
            case VkLWin:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.SuperLeft));
                return 0;
            case VkRWin:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.SuperRight));
                return 0;
            case >= VkA and <= VkZ:
                // EVERY letter key is a first-class key signal, chorded or plain — a game binds WASD movement the
                // same way it binds Ctrl+C. The letter's WM_CHAR still arrives independently (TranslateMessage runs
                // in the pump before dispatch), so typed text keeps flowing to the text pipeline; an unbound letter
                // signal is simply ignored by the binding table. The modifier chord rides along so a consumer (the
                // console's Ctrl+A/C/X) can distinguish a plain letter from a chorded one without a second binding.
                m_pendingInput.Enqueue(item: WindowInputEvent.LetterDown(
                    character: LetterForVirtualKey(virtualKey: wParam.ToInt64())
                ) with {
                    Modifiers = CurrentModifiers(),
                });
                return 0;
            default:
                return User32.DefWindowProc(
                    lParam: lParam,
                    message: WmKeyDown,
                    wParam: wParam,
                    windowHandle: windowHandle
                );
        }
    }
    private nint HandleKeyUp(nint windowHandle, uint message, nint wParam, nint lParam) {
        // Release edges for the named navigation/function/special keys AND the letter keys — a held-key
        // consumer (e.g. a movement binding's release edge) needs the up transition or the hold sticks.
        // Releases are inert by default (CommandBinding.ActivateOn ignores Completed).
        var virtualKey = wParam.ToInt64();

        if (virtualKey is >= VkA and <= VkZ) {
            m_pendingInput.Enqueue(item: WindowInputEvent.LetterUp(character: LetterForVirtualKey(virtualKey: virtualKey)));
            return 0;
        }

        if (TryMapNamedKey(lParam: lParam, virtualKey: virtualKey, key: out var key)) {
            m_pendingInput.Enqueue(item: WindowInputEvent.KeyUp(key: key));
            return 0;
        }

        return User32.DefWindowProc(
            lParam: lParam,
            message: message,
            wParam: wParam,
            windowHandle: windowHandle
        );
    }
    // The single owner of the VK→letter identity BOTH key edges share — a copy-paste slip desyncing the down and up
    // arithmetic (each previously computed it inline) would silently break every held-letter consumer.
    private static char LetterForVirtualKey(long virtualKey) {
        return (char)('a' + (virtualKey - VkA));
    }
    private static bool TryMapNamedKey(long virtualKey, nint lParam, out KeyCode key) {
        key = virtualKey switch {
            VkOem3 => KeyCode.Backtick,
            VkBack => KeyCode.Backspace,
            VkEscape => KeyCode.Escape,
            VkReturn => KeyCode.Enter,
            VkTab => KeyCode.Tab,
            VkUp => KeyCode.ArrowUp,
            VkDown => KeyCode.ArrowDown,
            VkLeft => KeyCode.ArrowLeft,
            VkRight => KeyCode.ArrowRight,
            VkSpace => KeyCode.Space,
            VkF1 => KeyCode.F1,
            VkF2 => KeyCode.F2,
            VkF3 => KeyCode.F3,
            VkF4 => KeyCode.F4,
            VkF5 => KeyCode.F5,
            VkF6 => KeyCode.F6,
            VkF7 => KeyCode.F7,
            VkF8 => KeyCode.F8,
            VkF9 => KeyCode.F9,
            VkF10 => KeyCode.F10,
            VkF11 => KeyCode.F11,
            VkF12 => KeyCode.F12,
            VkControl => ((IsExtendedKey(lParam: lParam)) ? KeyCode.ControlRight : KeyCode.ControlLeft),
            VkMenu => ((IsExtendedKey(lParam: lParam)) ? KeyCode.AltRight : KeyCode.AltLeft),
            VkShift => ResolveShiftSide(lParam: lParam),
            VkLWin => KeyCode.SuperLeft,
            VkRWin => KeyCode.SuperRight,
            _ => KeyCode.None,
        };

        return (key != KeyCode.None);
    }
    // WM_KEYDOWN/WM_KEYUP lParam bit 24 (ExtendedKeyBit) — set for the right-hand Control/Alt, clear for the
    // left-hand ones. Verified against the documented WM_KEYDOWN/WM_KEYUP lParam layout, not against hardware.
    private static bool IsExtendedKey(nint lParam) {
        return ((lParam.ToInt64() & ExtendedKeyBit) != 0);
    }
    // Shift's lParam carries no extended-key bit on either side, so the scan code (lParam bits 16..23) plus
    // MapVirtualKey(MAPVK_VSC_TO_VK_EX) is the documented way to recover which physical Shift key fired.
    // Ambiguous or unresolved input (0, or neither VK) defaults to the left key.
    private static KeyCode ResolveShiftSide(nint lParam) {
        var scanCode = (uint)((lParam.ToInt64() >> 16) & 0xFF);
        var resolvedVirtualKey = User32.MapVirtualKey(code: scanCode, mapType: MapvkVscToVkEx);

        return ((resolvedVirtualKey == VkRShift) ? KeyCode.ShiftRight : KeyCode.ShiftLeft);
    }
    private nint HandleRawInput(nint windowHandle, uint message, nint wParam, nint lParam) {
        var size = (uint)Marshal.SizeOf<RawInput>();

        if (User32.GetRawInputData(
            command: RidInput,
            data: out var raw,
            headerSize: (uint)Marshal.SizeOf<RawInputHeader>(),
            rawInput: lParam,
            size: ref size
        ) == unchecked((uint)-1)) {
            return DefaultRawInput(lParam: lParam, message: message, wParam: wParam, windowHandle: windowHandle);
        }

        if (raw.Header.Type == RimTypeMouse) {
            AccumulateRawMouse(mouse: in raw.Mouse);
        }

        // WM_INPUT must always reach DefWindowProc for system cleanup (per the Raw Input contract).
        return DefaultRawInput(lParam: lParam, message: message, wParam: wParam, windowHandle: windowHandle);
    }
    private nint DefaultRawInput(nint windowHandle, uint message, nint wParam, nint lParam) {
        return User32.DefWindowProc(
            lParam: lParam,
            message: message,
            wParam: wParam,
            windowHandle: windowHandle
        );
    }
    private void AccumulateRawMouse(in RawMouse mouse) {
        // Absolute mode (RDP / VMs / tablets / touch-as-mouse): lLastX/lLastY are absolute normalized coords,
        // not deltas — derive the delta from the previous absolute sample instead of summing garbage.
        if ((mouse.Flags & RiMouseMoveAbsolute) != 0) {
            if (
                (m_lastRawAbsoluteX is { } previousX) &&
                (m_lastRawAbsoluteY is { } previousY)
            ) {
                m_frameMouseDelta += new Vector2(
                    x: (mouse.LastX - previousX),
                    y: (mouse.LastY - previousY)
                );
            }

            m_lastRawAbsoluteX = mouse.LastX;
            m_lastRawAbsoluteY = mouse.LastY;
            return;
        }

        m_lastRawAbsoluteX = null;
        m_lastRawAbsoluteY = null;
        m_frameMouseDelta += new Vector2(
            x: mouse.LastX,
            y: mouse.LastY
        );
    }
    private nint HandleMouseMove(nint lParam) {
        var mouseX = GetSignedLowWord(value: lParam);
        var mouseY = GetSignedHighWord(value: lParam);

        // WM_MOUSEMOVE owns the absolute position (PointerAbsolute). It only owns the relative delta as a
        // fallback when raw input could not be registered — otherwise WM_INPUT is the single delta emitter,
        // so the two never both feed the pump-level accumulator for the same motion.
        if (
            !m_rawMouseRegistered &&
            (m_lastMouseX is { } lastMouseX) &&
            (m_lastMouseY is { } lastMouseY)
        ) {
            m_frameMouseDelta += new Vector2(
                x: (mouseX - lastMouseX),
                y: (mouseY - lastMouseY)
            );
        }

        m_lastMouseX = mouseX;
        m_lastMouseY = mouseY;
        m_pointerPositionDirty = true;
        return 0;
    }
    // The wheel is NOT summed per frame the way relative motion is: a wheel report is already a discrete act at
    // human cadence, so each one is enqueued as it arrives and consumers accumulate what they care about. The
    // delta rides the SIGNED high word of wParam (positive = away from the user) — an unsigned read would turn
    // every scroll toward the user into a large positive number.
    private nint HandleMouseWheel(nint wParam) {
        var notches = (GetSignedHighWord(value: wParam) / WheelDelta);

        if (notches != 0f) {
            m_pendingInput.Enqueue(item: WindowInputEvent.PointerWheel(notches: notches));
        }

        return 0;
    }
    // A left-button press captures the mouse: the OS then keeps routing WM_MOUSEMOVE/WM_LBUTTONUP to this window
    // even after the pointer leaves the client area (or the whole window), so a drag that starts inside and ends
    // outside still streams moves and the matching release edge, instead of going silent the moment the cursor
    // crosses the border. Only the left button captures — a middle/right press dragging a panel is not a contract
    // this window makes, and stacking captures across buttons would need a ref-count this simple pump doesn't have.
    private nint HandlePointerButtonDown(int button, nint windowHandle) {
        if (button == 0) {
            _ = User32.SetCapture(windowHandle: windowHandle);
        }

        m_pendingInput.Enqueue(item: WindowInputEvent.PointerButton(button: button, phase: CommandPhase.Started));
        return 0;
    }
    private nint HandlePointerButtonUp(int button) {
        if (button == 0) {
            _ = User32.ReleaseCapture();
        }

        m_pendingInput.Enqueue(item: WindowInputEvent.PointerButton(button: button, phase: CommandPhase.Completed));
        return 0;
    }

    internal static bool IsAltEnterGesture(uint message, nint wParam, short altKeyState) {
        return (
            (message == WmSysKeyDown) &&
            (wParam.ToInt64() == VkReturn) &&
            ((altKeyState & 0x8000) != 0)
        );
    }
    internal static nint CreateFullscreenWindowStyle(nint currentStyle) {
        var currentStyleValue = unchecked((uint)currentStyle.ToInt64());
        var fullscreenStyle = (currentStyleValue & WsVisible) | WsPopup;

        return unchecked((nint)fullscreenStyle);
    }
    internal static bool ShouldSuppressBackgroundErase(uint message) {
        return (message == WmEraseBkgnd);
    }
    internal static uint GetWidthFromSizeLParam(nint lParam) {
        return (uint)(lParam.ToInt64() & 0xFFFF);
    }
    internal static uint GetHeightFromSizeLParam(nint lParam) {
        return (uint)((lParam.ToInt64() >> 16) & 0xFFFF);
    }

    private static int GetSignedLowWord(nint value) {
        return unchecked((short)(value.ToInt64() & 0xFFFF));
    }
    private static int GetSignedHighWord(nint value) {
        return unchecked((short)((value.ToInt64() >> 16) & 0xFFFF));
    }
    private void ToggleFullscreen(nint windowHandle) {
        if (m_isFullscreen) {
            ExitFullscreen(windowHandle: windowHandle);
            return;
        }

        EnterFullscreen(windowHandle: windowHandle);
    }
    private void EnterFullscreen(nint windowHandle) {
        if (!User32.GetWindowRect(
            rectangle: out m_windowedBounds,
            windowHandle: windowHandle
        )) {
            throw new InvalidOperationException(message: $"GetWindowRect failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        m_windowedStyle = User32.GetWindowLongPtr(
            index: GwlStyle,
            windowHandle: windowHandle
        );

        var monitorHandle = User32.MonitorFromWindow(
            flags: MonitorDefaultToNearest,
            windowHandle: windowHandle
        );

        if (monitorHandle == 0) {
            throw new InvalidOperationException(message: "Could not locate a monitor for the active Win32 window.");
        }

        var monitorInfo = new MonitorInfo {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
        };

        if (!User32.GetMonitorInfo(
            monitorHandle: monitorHandle,
            monitorInfo: ref monitorInfo
        )) {
            throw new InvalidOperationException(message: $"GetMonitorInfo failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        _ = User32.SetWindowLongPtr(
            index: GwlStyle,
            newLong: CreateFullscreenWindowStyle(currentStyle: m_windowedStyle),
            windowHandle: windowHandle
        );
        ApplyWindowBounds(
            bounds: monitorInfo.MonitorRectangle,
            windowHandle: windowHandle
        );
        m_isFullscreen = true;
    }
    private void ExitFullscreen(nint windowHandle) {
        _ = User32.SetWindowLongPtr(
            index: GwlStyle,
            newLong: m_windowedStyle,
            windowHandle: windowHandle
        );
        ApplyWindowBounds(
            bounds: m_windowedBounds,
            windowHandle: windowHandle
        );
        m_isFullscreen = false;
    }
    private static void ApplyWindowBounds(nint windowHandle, Rectangle bounds) {
        var width = (bounds.Right - bounds.Left);
        var height = (bounds.Bottom - bounds.Top);

        if (!User32.SetWindowPos(
            flags: SwpFrameChanged | SwpNoActivate | SwpNoOwnerZOrder | SwpNoZOrder,
            height: height,
            insertAfterHandle: 0,
            width: width,
            windowHandle: windowHandle,
            x: bounds.Left,
            y: bounds.Top
        )) {
            throw new InvalidOperationException(message: $"SetWindowPos failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }
    private static bool IsKeyDown(int virtualKey) {
        return ((User32.GetKeyState(virtualKey: virtualKey) & 0x8000) != 0);
    }
    // The currently-held modifier chord, provider-neutral (Puck.Input.WindowInputModifiers), for stamping onto a
    // key event — either physical Super key folds into the one Super flag, mirroring left/right Control/Alt above.
    private static WindowInputModifiers CurrentModifiers() {
        var modifiers = WindowInputModifiers.None;

        if (IsKeyDown(virtualKey: VkControl)) {
            modifiers |= WindowInputModifiers.Control;
        }

        if (IsKeyDown(virtualKey: VkShift)) {
            modifiers |= WindowInputModifiers.Shift;
        }

        if (IsKeyDown(virtualKey: VkMenu)) {
            modifiers |= WindowInputModifiers.Alt;
        }

        if (IsKeyDown(virtualKey: VkLWin) || IsKeyDown(virtualKey: VkRWin)) {
            modifiers |= WindowInputModifiers.Super;
        }

        return modifiers;
    }
}
