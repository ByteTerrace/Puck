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
    private const int HtClient = 1;
    private const int IdcArrow = 32512;
    // WM_KEYDOWN/WM_KEYUP lParam bit 24 — set for the right-hand Control/Alt (and numpad Enter), clear for the
    // left-hand ones. Shift carries no such distinction (see MapvkVscToVkEx below).
    private const long ExtendedKeyBit = 0x01000000;
    private const int GwlStyle = -16;
    private const int GwlpUserData = -21;
    // MapVirtualKey map type: scan code -> the LEFT/RIGHT-distinguishing extended virtual key. The only way to
    // tell VK_LSHIFT from VK_RSHIFT — Shift's lParam/raw-input extended-key bit is never set for either side.
    private const uint MapvkVscToVkEx = 0x03;
    private const int MonitorDefaultToNearest = 2;
    private const int PmRemove = 0x0001;
    private const int SwShow = 5;
    private const int SwpFrameChanged = 0x0020;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoOwnerZOrder = 0x0200;
    private const int SwpNoZOrder = 0x0004;
    private const int VkA = 0x41;
    private const int Vk0 = 0x30;
    private const int Vk9 = 0x39;
    private const int VkBack = 0x08;
    private const int VkC = 0x43;
    private const int VkCapital = 0x14;
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
    private const int VkNumLock = 0x90;
    private const int VkNumpad0 = 0x60;
    private const int VkNumpad9 = 0x69;
    private const int VkAdd = 0x6B;
    private const int VkSubtract = 0x6D;
    private const int VkOem3 = 0xC0;
    private const int VkOemMinus = 0xBD;
    private const int VkOemPlus = 0xBB;
    private const int VkRShift = 0xA1;
    private const int VkRWin = 0x5C;
    private const int VkReturn = 0x0D;
    private const int VkRight = 0x27;
    private const int VkScroll = 0x91;
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
    private const uint WmMouseHWheel = 0x020E;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmXButtonUp = 0x020C;
    // WHEEL_DELTA: the notch quantum WM_MOUSEWHEEL's high word (and RAWMOUSE's ButtonData for a wheel report) counts
    // in. A free-spin or precision wheel reports FRACTIONS of it, so the neutral event carries the quotient as a
    // float rather than an integer notch count — rounding here would silently drop every sub-notch report a
    // high-resolution wheel makes.
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
    // Raw Input (WM_INPUT): every mouse and keyboard report this window registers for arrives here, keyed to its
    // physical device by RAWINPUTHEADER.hDevice (see ResolveRawDeviceId).
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const ushort HidUsageGenericMouse = 0x02;
    private const ushort HidUsageGenericKeyboard = 0x06;
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort RiMouseMoveAbsolute = 0x01;
    private const ushort RiMouseButton1Down = 0x0001;
    private const ushort RiMouseButton1Up = 0x0002;
    private const ushort RiMouseButton2Down = 0x0004;
    private const ushort RiMouseButton2Up = 0x0008;
    private const ushort RiMouseButton3Down = 0x0010;
    private const ushort RiMouseButton3Up = 0x0020;
    private const ushort RiMouseButton4Down = 0x0040;
    private const ushort RiMouseButton4Up = 0x0080;
    private const ushort RiMouseButton5Down = 0x0100;
    private const ushort RiMouseButton5Up = 0x0200;
    private const ushort RiMouseWheel = 0x0400;
    private const ushort RiMouseHWheel = 0x0800;
    // RAWKEYBOARD.Flags: RI_KEY_BREAK (a release, vs. a make/press) and RI_KEY_E0 (the same left/right-disambiguating
    // extended-key bit WM_KEYDOWN's lParam bit 24 carries).
    private const ushort RiKeyBreak = 0x0001;
    private const ushort RiKeyE0 = 0x0002;
    // RAWKEYBOARD reports "no VK mapping" (an E1 Pause/Break packet, among others) as 0xFF.
    private const ushort RawKeyboardNoVKey = 0xFF;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    // GetSystemMetrics indices for the virtual desktop bounds an absolute-mode raw mouse report (a tablet, RDP) is
    // normalized against.
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;

    private static readonly Lock RegistrationLock = new();
    private static readonly string WindowClassName = "Puck.Win32NativeWindow";
    private static readonly WndProc WndProcDelegate = StaticWindowProcedure;

    private static nint ArrowCursorHandle;
    private static nint InstanceHandleField;
    private static bool WindowClassRegistered;

    private readonly IClipboardService m_clipboardService;
    private readonly NativeWindowOptions m_options;
    private readonly Queue<WindowInputEvent> m_pendingInput = [];
    private readonly GCHandle m_selfHandle;
    // Reused across every ToUnicodeEx call — text derivation runs on the window-pump thread only, so one small
    // scratch buffer is safe to share (sized for a combining dead-key result or a surrogate pair).
    private readonly char[] m_textBuffer = new char[8];
    // Every raw device handle this session has seen, resolved once to its stable InputDeviceId (see
    // ResolveRawDeviceId) and cached for the life of the connection — hDevice is stable only while the device stays
    // connected; a replug mints a new handle, resolved again, that happens to hash back to the SAME InputDeviceId
    // (see InputDeviceId.FromKey's own reconnect-stability contract).
    private readonly Dictionary<nint, InputDeviceId> m_rawDeviceIds = new();
    // One RAWKEYBOARD-fed BYTE[256] per physical keyboard handle, threaded into ToUnicodeEx so a dead key or a
    // held Shift on one keyboard never leaks into another's text derivation.
    private readonly Dictionary<nint, byte[]> m_rawKeyStates = new();
    // One accumulated pointer per physical mouse handle — position, pending delta, and absolute-report tracking.
    private readonly Dictionary<nint, RawMouseState> m_rawMouseStates = new();

    private bool m_disposed;
    private bool m_isFullscreen;
    private bool m_hasPainted;
    private bool m_isOpen = true;
    private bool m_isVisible;
    private Vector2 m_frameMouseDelta;
    private bool m_pointerPositionDirty;
    private bool m_rawKeyboardRegistered;
    private bool m_rawMouseRegistered;
    private int? m_lastMouseX;
    private int? m_lastMouseY;
    private ulong m_resizeCount;
    private Rectangle m_windowedBounds;
    private nint m_windowedStyle;
    private nint m_windowHandle;

    // One physical mouse's accumulated pointer state — see m_rawMouseStates.
    private sealed class RawMouseState {
        public required InputDeviceId DeviceId;
        public Vector2 Position;
        public Vector2 PendingDelta;
        public bool PositionDirty;
        public int? LastAbsoluteX;
        public int? LastAbsoluteY;
    }

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
        if (!m_rawMouseRegistered) {
            // Fallback path: raw mouse registration failed, so WM_MOUSEMOVE/WM_*BUTTON* fed these two legacy
            // accumulators instead (see HandleMouseMove) — one aggregate, device-less pointer, exactly as before
            // every mouse carried its own identity.
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

            return;
        }

        // At most one PointerDelta and one PointerPosition PER DEVICE per frame, so a high-rate mouse that produced
        // many WM_INPUT packets collapses to a single report each observer sees once.
        foreach (var state in m_rawMouseStates.Values) {
            if (state.PendingDelta != Vector2.Zero) {
                m_pendingInput.Enqueue(item: WindowInputEvent.PointerDelta(
                    delta: state.PendingDelta,
                    deviceId: state.DeviceId
                ));
                state.PendingDelta = Vector2.Zero;
            }

            if (state.PositionDirty) {
                m_pendingInput.Enqueue(item: WindowInputEvent.PointerAbsolute(
                    position: state.Position,
                    deviceId: state.DeviceId
                ));
                state.PositionDirty = false;
            }
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
            ArrowCursorHandle = User32.LoadCursor(cursorName: IdcArrow, instanceHandle: 0);

            if (ArrowCursorHandle == 0) {
                throw new InvalidOperationException(message: $"LoadCursorW(IDC_ARROW) failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            var windowClass = new WindowClassEx {
                ClassName = WindowClassName,
                CursorHandle = ArrowCursorHandle,
                InstanceHandle = InstanceHandleField,
                Size = ((uint)Marshal.SizeOf<WindowClassEx>()),
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

        if (!User32.AdjustWindowRectEx(extendedStyle: 0, hasMenu: false, rectangle: ref outer, style: WsOverlappedWindow)) {
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

        RegisterRawInput(windowHandle: windowHandle);

        return windowHandle;
    }
    // Registers the generic mouse AND the generic keyboard for raw input (flags 0 = follow focus, foreground only;
    // the launcher focus-gates anyway), independently — one class's registration failing never withholds the
    // other. On failure, the corresponding m_raw*Registered flag stays false and the legacy WM_MOUSEMOVE/WM_KEYDOWN/
    // WM_CHAR path takes over for that class instead (see HandleMouseMove, HandleKeyDown/Up, HandleCharacterInput).
    private void RegisterRawInput(nint windowHandle) {
        m_rawMouseRegistered = RegisterRawInputDevice(
            usage: HidUsageGenericMouse,
            windowHandle: windowHandle
        );
        m_rawKeyboardRegistered = RegisterRawInputDevice(
            usage: HidUsageGenericKeyboard,
            windowHandle: windowHandle
        );
    }
    private static bool RegisterRawInputDevice(ushort usage, nint windowHandle) {
        RawInputDevice[] devices = [
            new RawInputDevice {
                Flags = 0,
                TargetWindowHandle = windowHandle,
                Usage = usage,
                UsagePage = HidUsagePageGeneric,
            },
        ];

        return User32.RegisterRawInputDevices(
            deviceCount: 1,
            rawInputDevices: devices,
            size: ((uint)Marshal.SizeOf<RawInputDevice>())
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
            case WmMouseHWheel:
                return HandleMouseHWheel(wParam: wParam);
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
            case WmXButtonDown:
                return HandlePointerButtonDown(button: XButtonIndex(wParam: wParam), windowHandle: windowHandle);
            case WmXButtonUp:
                return HandlePointerButtonUp(button: XButtonIndex(wParam: wParam));
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
        var hitTest = (unchecked((ushort)lParam.ToInt64()));

        if (
            m_options.HideMouseCursor &&
            (hitTest == HtClient)
        ) {
            _ = User32.SetCursor(cursorHandle: 0);
            return 1;
        }

        // LOWORD(lParam) is the WM_NCHITTEST result. Own only HTCLIENT: the non-client frame still belongs to
        // DefWindowProc so Windows supplies its native edge/corner resize cursors. The explicit arrow assignment is
        // needed even though the class carries IDC_ARROW too — a class with no cursor leaves whatever process-global
        // cursor happened to be active (commonly the startup wait spinner) unchanged indefinitely.
        if (hitTest == HtClient) {
            _ = User32.SetCursor(cursorHandle: ArrowCursorHandle);
            return 1;
        }

        return User32.DefWindowProc(
            lParam: lParam,
            message: message,
            wParam: wParam,
            windowHandle: windowHandle
        );
    }
    private nint HandleCharacterInput(nint windowHandle, nint wParam, nint lParam) {
        if (m_rawKeyboardRegistered) {
            // Typed text comes entirely from the raw stream's per-device ToUnicodeEx derivation when it is
            // available — see HandleRawKeyboard/EmitRawTypedText.
            return 0;
        }

        var character = checked((char)wParam.ToInt64());

        if (!char.IsControl(c: character)) {
            m_pendingInput.Enqueue(item: WindowInputEvent.TypedText(text: character.ToString()));
        }

        return 0;
    }
    // The legacy key door: when the raw keyboard stream is available it owns every key/letter/text signal (see
    // HandleRawKeyboard), and this handler's only remaining job is deciding whether to swallow the message
    // (returning 0 suppresses the native behaviors of the keys Puck owns — F10/bare Alt menu navigation and Tab
    // dialog traversal included) or let it reach DefWindowProc. When raw registration failed, this handler falls
    // all the way back to emitting the signal itself (device-less, exactly as before Raw Input covered the
    // keyboard).
    private nint HandleKeyDown(nint windowHandle, uint message, nint wParam, nint lParam) {
        var virtualKey = wParam.ToInt64();
        var isSystemKey = (message == WmSysKeyDown);
        var isExtended = IsExtendedKey(lParam: lParam);
        var scanCode = GetScanCode(lParam: lParam);

        if (!m_rawKeyboardRegistered) {
            EmitKeyTransition(
                deviceId: default,
                isDown: true,
                isExtended: isExtended,
                isSystemKey: isSystemKey,
                scanCode: scanCode,
                virtualKey: virtualKey
            );
        }

        if (
            IsConsumedKeyGesture(
            isSystemKey: isSystemKey,
            virtualKey: virtualKey
        ) ||
            (virtualKey is >= VkA and <= VkZ) ||
            TryMapNamedKey(
            isExtended: isExtended,
            key: out _,
            scanCode: scanCode,
            virtualKey: virtualKey
        )
        ) {
            return 0;
        }

        return User32.DefWindowProc(
            lParam: lParam,
            message: WmKeyDown,
            wParam: wParam,
            windowHandle: windowHandle
        );
    }
    private nint HandleKeyUp(nint windowHandle, uint message, nint wParam, nint lParam) {
        var virtualKey = wParam.ToInt64();
        var isExtended = IsExtendedKey(lParam: lParam);
        var scanCode = GetScanCode(lParam: lParam);

        if (!m_rawKeyboardRegistered) {
            EmitKeyTransition(
                deviceId: default,
                isDown: false,
                isExtended: isExtended,
                isSystemKey: (message == WmSysKeyUp),
                scanCode: scanCode,
                virtualKey: virtualKey
            );
        }

        if (
            (virtualKey is >= VkA and <= VkZ) ||
            TryMapNamedKey(
            isExtended: isExtended,
            key: out _,
            scanCode: scanCode,
            virtualKey: virtualKey
        )
        ) {
            return 0;
        }

        return User32.DefWindowProc(
            lParam: lParam,
            message: message,
            wParam: wParam,
            windowHandle: windowHandle
        );
    }
    // Whether virtualKey/isSystemKey is a system gesture consumed WHOLE (Alt+Enter fullscreen toggle, Ctrl+V
    // paste) rather than becoming an ordinary key/letter signal. Read-only: the legacy WM_KEYDOWN suppression
    // decision above calls this without running the effect; EmitKeyTransition is the one place that both checks it
    // and runs the effect.
    private bool IsConsumedKeyGesture(long virtualKey, bool isSystemKey) {
        if (
            isSystemKey &&
            (virtualKey == VkReturn) &&
            IsKeyDown(virtualKey: VkMenu)
        ) {
            return true;
        }

        // Exactly Control (no Shift/Alt/Super) so Ctrl+Shift+V still falls through to its letter signal rather
        // than also pasting.
        return (
            (virtualKey == VkV) &&
            IsKeyDown(virtualKey: VkControl) &&
            !IsKeyDown(virtualKey: VkShift) &&
            !IsKeyDown(virtualKey: VkMenu) &&
            !IsKeyDown(virtualKey: VkLWin) &&
            !IsKeyDown(virtualKey: VkRWin)
        );
    }
    // The one place a key transition becomes a WindowInputEvent, for a keyboard identified by deviceId — the raw
    // stream's per-device door (see HandleRawKeyboard) and the legacy WM_KEYDOWN/WM_KEYUP fallback (device-less,
    // when raw registration failed) both funnel through here so the gesture/letter/named-key logic is written once.
    private void EmitKeyTransition(long virtualKey, bool isExtended, byte scanCode, bool isDown, bool isSystemKey, InputDeviceId deviceId) {
        if (
            isDown &&
            IsConsumedKeyGesture(
            isSystemKey: isSystemKey,
            virtualKey: virtualKey
        )
        ) {
            if (virtualKey == VkReturn) {
                ToggleFullscreen(windowHandle: m_windowHandle);
            } else if (
                m_clipboardService.TryGetText(text: out var clipboardText) &&
                (clipboardText.Length > 0)
            ) {
                m_pendingInput.Enqueue(item: WindowInputEvent.TypedText(
                    deviceId: deviceId,
                    text: clipboardText
                ));
            }

            return;
        }

        // EVERY letter key is a first-class key signal, chorded or plain — a game binds WASD movement the same
        // way it binds Ctrl+C.
        if (virtualKey is >= VkA and <= VkZ) {
            var character = LetterForVirtualKey(virtualKey: virtualKey);

            m_pendingInput.Enqueue(item: (isDown
                ? (WindowInputEvent.LetterDown(
                    character: character,
                    deviceId: deviceId
                ) with {
                    Modifiers = CurrentModifiers(),
                })
                : WindowInputEvent.LetterUp(
                    character: character,
                    deviceId: deviceId
                )
            ));

            return;
        }

        if (TryMapNamedKey(
            isExtended: isExtended,
            key: out var key,
            scanCode: scanCode,
            virtualKey: virtualKey
        )) {
            m_pendingInput.Enqueue(item: (isDown
                ? WindowInputEvent.KeyDown(
                    deviceId: deviceId,
                    key: key
                )
                : WindowInputEvent.KeyUp(
                    deviceId: deviceId,
                    key: key
                )
            ));
        }
    }
    // The single owner of the VK→letter identity BOTH key edges share — a copy-paste slip desyncing the down and up
    // arithmetic (each previously computed it inline) would silently break every held-letter consumer.
    private static char LetterForVirtualKey(long virtualKey) {
        return ((char)('a' + (virtualKey - VkA)));
    }
    // The one VK→named-key table both physical edges (and both the raw and legacy-fallback doors) share.
    // Side-sensitive modifiers derive from the caller's own extended-bit/scan-code reading — the raw stream's
    // RAWKEYBOARD.Flags/MakeCode, or the legacy lParam via IsExtendedKey/GetScanCode.
    private static bool TryMapNamedKey(long virtualKey, bool isExtended, byte scanCode, out KeyCode key) {
        if (virtualKey is >= Vk0 and <= Vk9) {
            key = ((KeyCode)(((int)KeyCode.Digit0) + (virtualKey - Vk0)));
            return true;
        }

        if (virtualKey is >= VkNumpad0 and <= VkNumpad9) {
            key = ((KeyCode)(((int)KeyCode.Numpad0) + (virtualKey - VkNumpad0)));
            return true;
        }

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
            VkOemMinus => KeyCode.Minus,
            VkOemPlus => KeyCode.Equals,
            VkSubtract => KeyCode.NumpadSubtract,
            VkAdd => KeyCode.NumpadAdd,
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
            VkControl => ((isExtended) ? KeyCode.ControlRight : KeyCode.ControlLeft),
            VkMenu => ((isExtended) ? KeyCode.AltRight : KeyCode.AltLeft),
            VkShift => ResolveShiftSide(scanCode: scanCode),
            VkLWin => KeyCode.SuperLeft,
            VkRWin => KeyCode.SuperRight,
            _ => KeyCode.None,
        };

        return (key != KeyCode.None);
    }
    // WM_KEYDOWN/WM_KEYUP lParam bit 24 (ExtendedKeyBit) — set for the right-hand Control/Alt, clear for the
    // left-hand ones. Verified against the documented WM_KEYDOWN/WM_KEYUP lParam layout, not against hardware. The
    // raw stream carries the identical bit as RAWKEYBOARD.Flags' RI_KEY_E0 instead (see HandleRawKeyboard).
    private static bool IsExtendedKey(nint lParam) {
        return ((lParam.ToInt64() & ExtendedKeyBit) != 0);
    }
    // lParam bits 16..23 carry the scan code on the legacy path; RAWKEYBOARD.MakeCode carries the identical value
    // directly on the raw path (see HandleRawKeyboard) — both feed TryMapNamedKey's ResolveShiftSide the same way.
    private static byte GetScanCode(nint lParam) {
        return unchecked((byte)((lParam.ToInt64() >> 16) & 0xFF));
    }
    // Shift's scan code carries no extended-key bit on either side, so MapVirtualKey(MAPVK_VSC_TO_VK_EX) against
    // the physical scan code is the documented way to recover which physical Shift key fired. Ambiguous or
    // unresolved input (0, or neither VK) defaults to the left key.
    private static KeyCode ResolveShiftSide(byte scanCode) {
        var resolvedVirtualKey = User32.MapVirtualKey(code: scanCode, mapType: MapvkVscToVkEx);

        return ((resolvedVirtualKey == VkRShift) ? KeyCode.ShiftRight : KeyCode.ShiftLeft);
    }
    private nint HandleRawInput(nint windowHandle, uint message, nint wParam, nint lParam) {
        var size = ((uint)Marshal.SizeOf<RawInput>());

        if (User32.GetRawInputData(
            command: RidInput,
            data: out var raw,
            headerSize: ((uint)Marshal.SizeOf<RawInputHeader>()),
            rawInput: lParam,
            size: ref size
        ) == unchecked((uint)-1)) {
            return DefaultRawInput(lParam: lParam, message: message, wParam: wParam, windowHandle: windowHandle);
        }

        if (raw.Header.Type == RimTypeMouse) {
            HandleRawMouse(
                deviceHandle: raw.Header.DeviceHandle,
                mouse: in raw.Data.Mouse,
                windowHandle: windowHandle
            );
        } else if (raw.Header.Type == RimTypeKeyboard) {
            HandleRawKeyboard(
                deviceHandle: raw.Header.DeviceHandle,
                keyboard: in raw.Data.Keyboard
            );
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
    // Resolves (creating on first sight) a physical mouse's accumulated pointer state, seeded at the client centre
    // so its first PointerPosition report is a sane on-screen value rather than the client origin.
    private RawMouseState GetOrCreateRawMouseState(nint deviceHandle) {
        if (!m_rawMouseStates.TryGetValue(
            key: deviceHandle,
            value: out var state
        )) {
            state = new RawMouseState {
                DeviceId = ResolveRawDeviceId(deviceHandle: deviceHandle),
                Position = new Vector2(
                    x: (Width / 2f),
                    y: (Height / 2f)
                ),
            };
            m_rawMouseStates[deviceHandle] = state;
        }

        return state;
    }
    private void HandleRawMouse(nint deviceHandle, nint windowHandle, in RawMouse mouse) {
        var state = GetOrCreateRawMouseState(deviceHandle: deviceHandle);

        // Absolute mode (RDP / VMs / tablets / touch-as-mouse): LastX/LastY are absolute normalized coordinates
        // across the virtual desktop, not deltas. The relative-delta report stays in raw device units (matching
        // the relative-mode branch below); the accumulated on-screen POSITION is translated onto the client rect
        // and clamped to it independently.
        if ((mouse.Flags & RiMouseMoveAbsolute) != 0) {
            if (
                (state.LastAbsoluteX is { } previousX) &&
                (state.LastAbsoluteY is { } previousY)
            ) {
                state.PendingDelta += new Vector2(
                    x: (mouse.LastX - previousX),
                    y: (mouse.LastY - previousY)
                );
            }

            state.LastAbsoluteX = mouse.LastX;
            state.LastAbsoluteY = mouse.LastY;
            state.Position = ClampToClient(position: TranslateAbsoluteRawMouse(
                rawX: mouse.LastX,
                rawY: mouse.LastY,
                windowHandle: windowHandle
            ));
        } else {
            state.LastAbsoluteX = null;
            state.LastAbsoluteY = null;

            var delta = new Vector2(
                x: mouse.LastX,
                y: mouse.LastY
            );

            state.PendingDelta += delta;
            state.Position = ClampToClient(position: (state.Position + delta));
        }

        state.PositionDirty = true;

        if (mouse.ButtonFlags != 0) {
            HandleRawMouseButtons(
                buttonData: mouse.ButtonData,
                buttonFlags: mouse.ButtonFlags,
                state: state,
                windowHandle: windowHandle
            );
        }
    }
    private Vector2 ClampToClient(Vector2 position) {
        return new Vector2(
            x: Math.Clamp(value: position.X, min: 0f, max: Width),
            y: Math.Clamp(value: position.Y, min: 0f, max: Height)
        );
    }
    // Maps a RAWMOUSE absolute report (normalized 0..65535 across the virtual desktop, or the current monitor when
    // the device sets MOUSE_VIRTUAL_DESKTOP — not modeled here, matching every raw mouse this window has been
    // verified against) onto client-relative pixels.
    private static Vector2 TranslateAbsoluteRawMouse(nint windowHandle, int rawX, int rawY) {
        var virtualLeft = User32.GetSystemMetrics(index: SmXVirtualScreen);
        var virtualTop = User32.GetSystemMetrics(index: SmYVirtualScreen);
        var virtualWidth = User32.GetSystemMetrics(index: SmCxVirtualScreen);
        var virtualHeight = User32.GetSystemMetrics(index: SmCyVirtualScreen);

        var screenX = (virtualLeft + ((rawX / 65535f) * virtualWidth));
        var screenY = (virtualTop + ((rawY / 65535f) * virtualHeight));

        var clientOrigin = new Point();

        _ = User32.ClientToScreen(
            point: ref clientOrigin,
            windowHandle: windowHandle
        );

        return new Vector2(
            x: (screenX - clientOrigin.X),
            y: (screenY - clientOrigin.Y)
        );
    }
    private void HandleRawMouseButtons(RawMouseState state, nint windowHandle, ushort buttonFlags, ushort buttonData) {
        ApplyRawMouseButton(
            button: 0,
            buttonFlags: buttonFlags,
            downFlag: RiMouseButton1Down,
            state: state,
            upFlag: RiMouseButton1Up,
            windowHandle: windowHandle
        );
        ApplyRawMouseButton(
            button: 1,
            buttonFlags: buttonFlags,
            downFlag: RiMouseButton2Down,
            state: state,
            upFlag: RiMouseButton2Up,
            windowHandle: windowHandle
        );
        ApplyRawMouseButton(
            button: 2,
            buttonFlags: buttonFlags,
            downFlag: RiMouseButton3Down,
            state: state,
            upFlag: RiMouseButton3Up,
            windowHandle: windowHandle
        );
        ApplyRawMouseButton(
            button: 3,
            buttonFlags: buttonFlags,
            downFlag: RiMouseButton4Down,
            state: state,
            upFlag: RiMouseButton4Up,
            windowHandle: windowHandle
        );
        ApplyRawMouseButton(
            button: 4,
            buttonFlags: buttonFlags,
            downFlag: RiMouseButton5Down,
            state: state,
            upFlag: RiMouseButton5Up,
            windowHandle: windowHandle
        );

        // The wheel is NOT summed per frame the way relative motion is: a wheel report is already a discrete act at
        // human cadence, so each one is enqueued as it arrives. The delta rides ButtonData as a SIGNED quantum
        // (positive = away from the user) — the same WHEEL_DELTA convention WM_MOUSEWHEEL's high word used.
        if ((buttonFlags & RiMouseWheel) != 0) {
            var notches = (unchecked((short)buttonData) / WheelDelta);

            if (notches != 0f) {
                m_pendingInput.Enqueue(item: WindowInputEvent.PointerWheel(
                    deviceId: state.DeviceId,
                    notches: notches
                ));
            }
        }

        if ((buttonFlags & RiMouseHWheel) != 0) {
            var notches = (unchecked((short)buttonData) / WheelDelta);

            if (notches != 0f) {
                m_pendingInput.Enqueue(item: WindowInputEvent.PointerWheel(
                    deviceId: state.DeviceId,
                    notches: new Vector2(
                    x: notches,
                    y: 0f
                )
                ));
            }
        }
    }
    // A left-button press captures the mouse: the OS then keeps routing raw reports to this window even after the
    // pointer leaves the client area (or the whole window), so a drag that starts inside and ends outside still
    // streams moves and the matching release edge, instead of going silent the moment the cursor crosses the
    // border. Only the left button captures — see HandlePointerButtonDown's own remarks (the legacy-fallback twin
    // of this same rule).
    private void ApplyRawMouseButton(RawMouseState state, nint windowHandle, ushort downFlag, ushort upFlag, int button, ushort buttonFlags) {
        if ((buttonFlags & downFlag) != 0) {
            if (button == 0) {
                _ = User32.SetCapture(windowHandle: windowHandle);
            }

            m_pendingInput.Enqueue(item: WindowInputEvent.PointerButton(
                button: button,
                deviceId: state.DeviceId,
                phase: CommandPhase.Started
            ));
        } else if ((buttonFlags & upFlag) != 0) {
            if (button == 0) {
                _ = User32.ReleaseCapture();
            }

            m_pendingInput.Enqueue(item: WindowInputEvent.PointerButton(
                button: button,
                deviceId: state.DeviceId,
                phase: CommandPhase.Completed
            ));
        }
    }
    private void HandleRawKeyboard(nint deviceHandle, in RawKeyboard keyboard) {
        if (keyboard.VKey == RawKeyboardNoVKey) {
            // No VK mapping (an E1 Pause/Break packet, among others) — nothing to resolve.
            return;
        }

        var deviceId = ResolveRawDeviceId(deviceHandle: deviceHandle);
        var isDown = ((keyboard.Flags & RiKeyBreak) == 0);
        var isExtended = ((keyboard.Flags & RiKeyE0) != 0);
        var isSystemKey = (keyboard.Message is WmSysKeyDown or WmSysKeyUp);

        EmitKeyTransition(
            deviceId: deviceId,
            isDown: isDown,
            isExtended: isExtended,
            isSystemKey: isSystemKey,
            scanCode: unchecked((byte)keyboard.MakeCode),
            virtualKey: keyboard.VKey
        );

        if (isDown) {
            EmitRawTypedText(
                deviceHandle: deviceHandle,
                deviceId: deviceId,
                isExtended: isExtended,
                keyboard: in keyboard
            );
        }
    }
    // Derives typed text for ONE physical keyboard from its own make/break stream via ToUnicodeEx, so a dead key or
    // a held Shift on keyboard A never combines with a keystroke on keyboard B — each keyboard's BYTE[256] state
    // (m_rawKeyStates) is threaded independently. ToUnicodeEx's dead-key COMPOSITION state itself is maintained by
    // Windows per calling thread, not per device: a dead key on one keyboard immediately followed by a letter on
    // another, on the same thread, can still combine across devices — a real limitation of the Win32 API this
    // window has no way around, not attempted here. IME composition (WM_IME_*) is not read by this path at all;
    // an IME's composed text needs its own window-message-driven pipeline, out of scope here.
    private void EmitRawTypedText(nint deviceHandle, InputDeviceId deviceId, bool isExtended, in RawKeyboard keyboard) {
        if (!m_rawKeyStates.TryGetValue(
            key: deviceHandle,
            value: out var keyState
        )) {
            keyState = new byte[256];
            m_rawKeyStates[deviceHandle] = keyState;
        }

        if (keyboard.VKey < keyState.Length) {
            keyState[keyboard.VKey] = (((keyboard.Flags & RiKeyBreak) == 0) ? (byte)0x80 : (byte)0);
        }

        // Toggle state (bit 0) is a single system-wide fact, not per keyboard — mirrored in from GetKeyState so a
        // Shift+letter or a dead-key sequence resolves against the real CapsLock/NumLock/ScrollLock state.
        keyState[VkCapital] = (byte)((User32.GetKeyState(virtualKey: VkCapital) & 1) != 0 ? 1 : 0);
        keyState[VkNumLock] = (byte)((User32.GetKeyState(virtualKey: VkNumLock) & 1) != 0 ? 1 : 0);
        keyState[VkScroll] = (byte)((User32.GetKeyState(virtualKey: VkScroll) & 1) != 0 ? 1 : 0);

        var scanCode = ((uint)(keyboard.MakeCode | (isExtended ? 0x0100u : 0u)));
        var written = User32.ToUnicodeEx(
            bufferCount: m_textBuffer.Length,
            buffer: m_textBuffer,
            flags: 0,
            keyboardLayout: User32.GetKeyboardLayout(threadId: 0),
            keyState: keyState,
            scanCode: scanCode,
            virtualKey: keyboard.VKey
        );

        // written < 0 is a dead key awaiting its combining character (state held internally by Windows, per
        // thread); written == 0 is a non-printing key (an arrow, a function key). Only a positive result is real
        // text.
        if (
            (written > 0) &&
            !char.IsControl(c: m_textBuffer[0])
        ) {
            m_pendingInput.Enqueue(item: WindowInputEvent.TypedText(
                deviceId: deviceId,
                text: new string(
                    value: m_textBuffer,
                    startIndex: 0,
                    length: written
                )
            ));
        }
    }
    // Resolves (and caches, per connection) a raw device handle to a reconnect-stable InputDeviceId, addressed off
    // its OS device interface path — the same physical mouse or keyboard replugged later mints a NEW hDevice but
    // resolves to the SAME name, and so the SAME id (see InputDeviceId.FromKey). A device whose name could not be
    // read falls back to a connection-only id keyed by the handle itself — stable for this connection, explicitly
    // ineligible for durable preferences.
    private InputDeviceId ResolveRawDeviceId(nint deviceHandle) {
        if (m_rawDeviceIds.TryGetValue(
            key: deviceHandle,
            value: out var cached
        )) {
            return cached;
        }

        var resolved = (TryGetRawInputDeviceName(deviceHandle: deviceHandle, name: out var name)
            ? InputDeviceId.FromKey(key: name)
            : InputDeviceId.FromConnectionKey(key: $"raw-device-{deviceHandle}")
        );

        m_rawDeviceIds[deviceHandle] = resolved;

        return resolved;
    }
    // GetRawInputDeviceInfo's RIDI_DEVICENAME reports the count in CHARACTERS (not bytes); the documented two-call
    // pattern discovers the required size with a null buffer, then fetches into one sized exactly for it.
    private static bool TryGetRawInputDeviceName(nint deviceHandle, out string name) {
        name = string.Empty;

        var size = 0u;

        _ = User32.GetRawInputDeviceInfo(
            command: RidiDeviceName,
            data: 0,
            deviceHandle: deviceHandle,
            size: ref size
        );

        if (size == 0) {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(cb: (checked((int)size) * sizeof(char)));

        try {
            var written = User32.GetRawInputDeviceInfo(
                command: RidiDeviceName,
                data: buffer,
                deviceHandle: deviceHandle,
                size: ref size
            );

            if (unchecked((int)written) < 0) {
                return false;
            }

            name = (Marshal.PtrToStringUni(
                len: checked((int)written),
                ptr: buffer
            ) ?? string.Empty).TrimEnd(trimChar: '\0');

            return (name.Length > 0);
        } finally {
            Marshal.FreeHGlobal(hglobal: buffer);
        }
    }
    private nint HandleMouseMove(nint lParam) {
        if (m_rawMouseRegistered) {
            // Position and delta come entirely from the raw stream when it is available — see HandleRawMouse.
            return 0;
        }

        var mouseX = GetSignedLowWord(value: lParam);
        var mouseY = GetSignedHighWord(value: lParam);

        if (
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
    private nint HandleMouseWheel(nint wParam) {
        if (m_rawMouseRegistered) {
            return 0;
        }

        var notches = (GetSignedHighWord(value: wParam) / WheelDelta);

        if (notches != 0f) {
            m_pendingInput.Enqueue(item: WindowInputEvent.PointerWheel(notches: notches));
        }

        return 0;
    }
    private nint HandleMouseHWheel(nint wParam) {
        if (m_rawMouseRegistered) {
            return 0;
        }

        var notches = (GetSignedHighWord(value: wParam) / WheelDelta);

        if (notches != 0f) {
            m_pendingInput.Enqueue(item: WindowInputEvent.PointerWheel(notches: new Vector2(x: notches, y: 0f)));
        }

        return 0;
    }
    // WM_XBUTTON's high word is 1 for XBUTTON1 and 2 for XBUTTON2. They follow left/right/middle as the neutral
    // zero-based button indices 3 and 4; keeping the conversion arithmetic makes the backend vocabulary scalable.
    private static int XButtonIndex(nint wParam) {
        return (2 + unchecked((ushort)(wParam.ToInt64() >> 16)));
    }
    // Legacy button fallback — only live when raw mouse registration failed (see HandleRawMouseButtons for the
    // normal, per-device door). A left-button press captures the mouse the same way; see that method's own remarks.
    private nint HandlePointerButtonDown(int button, nint windowHandle) {
        if (m_rawMouseRegistered) {
            return 0;
        }

        if (button == 0) {
            _ = User32.SetCapture(windowHandle: windowHandle);
        }

        m_pendingInput.Enqueue(item: WindowInputEvent.PointerButton(button: button, phase: CommandPhase.Started));
        return 0;
    }
    private nint HandlePointerButtonUp(int button) {
        if (m_rawMouseRegistered) {
            return 0;
        }

        if (button == 0) {
            _ = User32.ReleaseCapture();
        }

        m_pendingInput.Enqueue(item: WindowInputEvent.PointerButton(button: button, phase: CommandPhase.Completed));
        return 0;
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
        return ((uint)(lParam.ToInt64() & 0xFFFF));
    }
    internal static uint GetHeightFromSizeLParam(nint lParam) {
        return ((uint)((lParam.ToInt64() >> 16) & 0xFFFF));
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
            Size = ((uint)Marshal.SizeOf<MonitorInfo>()),
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
    // Read off the global key state (GetKeyState), not any one device's own tracked state: a modifier held on a
    // DIFFERENT physical keyboard than the one producing this letter still chords it, matching how a single OS
    // keyboard state has always worked.
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
