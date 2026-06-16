using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

internal sealed partial class Win32NativeWindow : INativeWindow, INativeSurfaceSourceProvider, INativeWindowLoadingPresenter {
    private const int BkModeTransparent = 1;
    private const int CwUseDefault = unchecked((int)0x80000000);
    private const uint DtCenter = 0x00000001;
    private const uint DtSingleLine = 0x00000020;
    private const uint DtVCenter = 0x00000004;
    private const uint DtWordBreak = 0x00000010;
    private const int ErrorClassAlreadyExists = 1410;
    private const int GwlStyle = -16;
    private const int GwlpUserData = -21;
    private const int MonitorDefaultToNearest = 2;
    private const int PmRemove = 0x0001;
    private const int SwShow = 5;
    private const int SwpFrameChanged = 0x0020;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoOwnerZOrder = 0x0200;
    private const int SwpNoZOrder = 0x0004;
    private const int VkA = 0x41;
    private const int VkAKey = 0x41;
    private const int VkBack = 0x08;
    private const int VkC = 0x43;
    private const int VkControl = 0x11;
    private const int VkD = 0x44;
    private const int VkDown = 0x28;
    private const int VkE = 0x45;
    private const int VkEscape = 0x1B;
    private const int VkF = 0x46;
    private const int VkF1 = 0x70;
    private const int VkF2 = 0x71;
    private const int VkF3 = 0x72;
    private const int VkF4 = 0x73;
    private const int VkF5 = 0x74;
    private const int VkF6 = 0x75;
    private const int VkF7 = 0x76;
    private const int VkF8 = 0x77;
    private const int VkLeft = 0x25;
    private const int VkMenu = 0x12;
    private const int VkOem3 = 0xC0;
    private const int VkQ = 0x51;
    private const int VkR = 0x52;
    private const int VkReturn = 0x0D;
    private const int VkRight = 0x27;
    private const int VkS = 0x53;
    private const int VkTab = 0x09;
    private const int VkUp = 0x26;
    private const int VkV = 0x56;
    private const int VkW = 0x57;
    private const uint WmActivate = 0x0006;
    private const uint WmChar = 0x0102;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmKeyDown = 0x0100;
    private const uint WmMouseMove = 0x0200;
    private const uint WmNcCreate = 0x0081;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmPaint = 0x000F;
    private const uint WmSetCursor = 0x0020;
    private const uint WmShowWindow = 0x0018;
    private const uint WmSize = 0x0005;
    private const uint WmSysKeyDown = 0x0104;
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
    private readonly Queue<InputPacket> m_pendingInput = [];
    private readonly GCHandle m_selfHandle;
    private bool m_disposed;
    private bool m_isFullscreen;
    private bool m_hasPainted;
    private bool m_isOpen = true;
    private bool m_isVisible;
    private int? m_lastMouseX;
    private int? m_lastMouseY;
    private string m_loadingFrameDetail = string.Empty;
    private string m_loadingFrameHeading = string.Empty;
    private string? m_loadingFrameImagePath;
    private ulong m_resizeCount;
    private bool m_showLoadingFrame;
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
    public void RenderLoadingFrame(string heading, string detail, string? imagePath) {
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

        m_loadingFrameHeading = (string.IsNullOrWhiteSpace(value: heading)
            ? "Puck Loading"
            : heading);
        m_loadingFrameDetail = (string.IsNullOrWhiteSpace(value: detail)
            ? "Initializing Vulkan resources"
            : detail);
        m_loadingFrameImagePath = (string.IsNullOrWhiteSpace(value: imagePath)
            ? null
            : imagePath);
        m_showLoadingFrame = true;

        RequestLoadingFramePaint();
    }
    public void ClearLoadingFrame() {
        if (
            m_disposed ||
            (m_windowHandle == 0)
        ) {
            return;
        }

        m_showLoadingFrame = false;
        m_loadingFrameHeading = string.Empty;
        m_loadingFrameDetail = string.Empty;
        m_loadingFrameImagePath = null;
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
    }
    public bool TryDequeueInput(out InputPacket inputEvent) {
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
        var windowHandle = User32.CreateWindowEx(
            className: WindowClassName,
            extendedStyle: 0,
            height: checked((int)options.Height),
            instanceHandle: InstanceHandleField,
            menuHandle: 0,
            parameter: GCHandle.ToIntPtr(value: m_selfHandle),
            parentHandle: 0,
            style: WsOverlappedWindow,
            width: checked((int)options.Width),
            windowName: options.Title,
            x: CwUseDefault,
            y: CwUseDefault
        );

        if (windowHandle == 0) {
            throw new InvalidOperationException(message: $"CreateWindowExW failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return windowHandle;
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
            case WmPaint:
                _ = User32.BeginPaint(
                    paintStruct: out var paintStruct,
                    windowHandle: windowHandle
                );
                m_hasPainted = true;
                if (m_showLoadingFrame) {
                    DrawLoadingFrame(
                        paintStruct: in paintStruct,
                        windowHandle: windowHandle
                    );
                }

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
    private void RequestLoadingFramePaint() {
        if (!User32.InvalidateRect(
            m_windowHandle,
            0,
            eraseBackground: false
        )) {
            return;
        }

        _ = User32.UpdateWindow(windowHandle: m_windowHandle);
    }
    private void DrawLoadingFrame(nint windowHandle, in PaintStruct paintStruct) {
        if (!User32.GetClientRect(
            rectangle: out var clientRect,
            windowHandle: windowHandle
        )) {
            return;
        }

        var width = (clientRect.Right - clientRect.Left);
        var height = (clientRect.Bottom - clientRect.Top);

        if (
            (width <= 0) ||
            (height <= 0)
        ) {
            return;
        }

        if (
            OperatingSystem.IsWindowsVersionAtLeast(
                6,
                1
            ) &&
            TryDrawLoadingSplashImage(
                clientRect: clientRect,
                deviceContextHandle: paintStruct.DeviceContextHandle,
                height: height,
                width: width
            )
        ) {
            DrawLoadingSplashTextOverlay(
                clientRect: clientRect,
                deviceContextHandle: paintStruct.DeviceContextHandle,
                height: height,
                width: width
            );
            return;
        }

        DrawLoadingTextPanel(
            clientRect: clientRect,
            deviceContextHandle: paintStruct.DeviceContextHandle,
            height: height,
            width: width
        );
    }
    private void DrawLoadingTextPanel(nint deviceContextHandle, Rectangle clientRect, int width, int height) {
        FillRect(
            deviceContextHandle,
            clientRect,
            red: 16,
            green: 22,
            blue: 35
        );

        var panelRect = new Rectangle {
            Bottom = ((clientRect.Top + Math.Max(
                val1: 20,
                val2: (height / 3)
            )) + Math.Max(
                val1: 88,
                val2: (height / 4)
            )),
            Left = (clientRect.Left + Math.Max(
                val1: 24,
                val2: (width / 8)
            )),
            Right = (clientRect.Right - Math.Max(
                val1: 24,
                val2: (width / 8)
            )),
            Top = (clientRect.Top + Math.Max(
                val1: 20,
                val2: (height / 3)
            )),
        };

        FillRect(
            deviceContextHandle,
            panelRect,
            red: 52,
            green: 178,
            blue: 153
        );

        var contentRect = new Rectangle {
            Bottom = (panelRect.Bottom - 3),
            Left = (panelRect.Left + 3),
            Right = (panelRect.Right - 3),
            Top = (panelRect.Top + 3),
        };

        FillRect(
            deviceContextHandle,
            contentRect,
            red: 21,
            green: 30,
            blue: 47
        );

        _ = Gdi32.SetBkMode(
            deviceContextHandle: deviceContextHandle,
            mode: BkModeTransparent
        );

        var headingRect = new Rectangle {
            Bottom = (contentRect.Top + ((contentRect.Bottom - contentRect.Top) / 2)),
            Left = (contentRect.Left + 12),
            Right = (contentRect.Right - 12),
            Top = (contentRect.Top + 6),
        };

        _ = Gdi32.SetTextColor(
            colorRef: CreateColorRef(
                blue: 255,
                green: 248,
                red: 244
            ),
            deviceContextHandle: deviceContextHandle
        );
        _ = User32.DrawText(
            deviceContextHandle: deviceContextHandle,
            format: DtCenter | DtSingleLine | DtVCenter,
            rectangle: ref headingRect,
            text: m_loadingFrameHeading,
            textLength: -1
        );

        var detailRect = new Rectangle {
            Bottom = (contentRect.Bottom - 6),
            Left = (contentRect.Left + 12),
            Right = (contentRect.Right - 12),
            Top = headingRect.Bottom,
        };

        _ = Gdi32.SetTextColor(
            colorRef: CreateColorRef(
                blue: 208,
                green: 188,
                red: 174
            ),
            deviceContextHandle: deviceContextHandle
        );
        _ = User32.DrawText(
            deviceContextHandle: deviceContextHandle,
            format: DtCenter | DtWordBreak,
            rectangle: ref detailRect,
            text: m_loadingFrameDetail,
            textLength: -1
        );
    }
    private void DrawLoadingSplashTextOverlay(nint deviceContextHandle, Rectangle clientRect, int width, int height) {
        _ = Gdi32.SetBkMode(
            deviceContextHandle: deviceContextHandle,
            mode: BkModeTransparent
        );

        var horizontalInset = Math.Max(
            val1: 32,
            val2: (width / 12)
        );
        var textBandHeight = Math.Max(
            val1: 88,
            val2: (height / 5)
        );
        var headingRect = new Rectangle {
            Bottom = (clientRect.Bottom - (textBandHeight / 2)),
            Left = (clientRect.Left + horizontalInset),
            Right = (clientRect.Right - horizontalInset),
            Top = (clientRect.Bottom - textBandHeight),
        };

        _ = Gdi32.SetTextColor(
            colorRef: CreateColorRef(
                blue: 255,
                green: 251,
                red: 248
            ),
            deviceContextHandle: deviceContextHandle
        );
        _ = User32.DrawText(
            deviceContextHandle: deviceContextHandle,
            format: DtCenter | DtSingleLine | DtVCenter,
            rectangle: ref headingRect,
            text: m_loadingFrameHeading,
            textLength: -1
        );

        var detailRect = new Rectangle {
            Bottom = (clientRect.Bottom - Math.Max(
                val1: 16,
                val2: (height / 36)
            )),
            Left = headingRect.Left,
            Right = headingRect.Right,
            Top = headingRect.Bottom,
        };

        _ = Gdi32.SetTextColor(
            colorRef: CreateColorRef(
                blue: 240,
                green: 224,
                red: 214
            ),
            deviceContextHandle: deviceContextHandle
        );
        _ = User32.DrawText(
            deviceContextHandle: deviceContextHandle,
            format: DtCenter | DtWordBreak,
            rectangle: ref detailRect,
            text: m_loadingFrameDetail,
            textLength: -1
        );
    }
    [SupportedOSPlatform("windows6.1")]
    private bool TryDrawLoadingSplashImage(nint deviceContextHandle, Rectangle clientRect, int width, int height) {
        if (
            string.IsNullOrWhiteSpace(value: m_loadingFrameImagePath) ||
            !File.Exists(path: m_loadingFrameImagePath)
        ) {
            return false;
        }

        try {
            using var image = System.Drawing.Image.FromFile(m_loadingFrameImagePath);
            using var graphics = System.Drawing.Graphics.FromHdc(deviceContextHandle);

            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.DrawImage(
                image,
                CreateCoverDestinationRectangle(
                    clientRect,
                    width,
                    height,
                    image.Width,
                    image.Height
                )
            );

            using var washBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(
                alpha: 34,
                blue: 30,
                green: 18,
                red: 10
            ));

            graphics.FillRectangle(
                washBrush,
                clientRect.Left,
                clientRect.Top,
                width,
                height
            );

            using var textBandBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(
                    height: height,
                    width: width,
                    x: clientRect.Left,
                    y: clientRect.Top
                ),
                System.Drawing.Color.FromArgb(
                    alpha: 0,
                    blue: 22,
                    green: 13,
                    red: 8
                ),
                System.Drawing.Color.FromArgb(
                    alpha: 172,
                    blue: 22,
                    green: 13,
                    red: 8
                ),
                System.Drawing.Drawing2D.LinearGradientMode.Vertical
            );

            graphics.FillRectangle(
                textBandBrush,
                clientRect.Left,
                clientRect.Top,
                width,
                height
            );
            return true;
        } catch (ArgumentException) {
            return false;
        } catch (ExternalException) {
            return false;
        } catch (IOException) {
            return false;
        } catch (OutOfMemoryException) {
            return false;
        } catch (UnauthorizedAccessException) {
            return false;
        }
    }
    [SupportedOSPlatform("windows6.1")]
    private static System.Drawing.Rectangle CreateCoverDestinationRectangle(
        Rectangle clientRect,
        int clientWidth,
        int clientHeight,
        int imageWidth,
        int imageHeight
    ) {
        var clientAspectRatio = (clientWidth / (double)clientHeight);
        var imageAspectRatio = (imageWidth / (double)imageHeight);

        if (imageAspectRatio > clientAspectRatio) {
            var destinationHeight = clientHeight;
            var destinationWidth = (int)Math.Ceiling(a: (destinationHeight * imageAspectRatio));

            return new System.Drawing.Rectangle(
                height: destinationHeight,
                width: destinationWidth,
                x: (clientRect.Left - ((destinationWidth - clientWidth) / 2)),
                y: clientRect.Top
            );
        }

        var coverWidth = clientWidth;
        var coverHeight = (int)Math.Ceiling(a: (coverWidth / imageAspectRatio));

        return new System.Drawing.Rectangle(
            height: coverHeight,
            width: coverWidth,
            x: clientRect.Left,
            y: (clientRect.Top - ((coverHeight - clientHeight) / 2))
        );
    }
    private static void FillRect(nint deviceContextHandle, Rectangle rectangle, byte red, byte green, byte blue) {
        var brushHandle = Gdi32.CreateSolidBrush(colorRef: CreateColorRef(
            blue: blue,
            green: green,
            red: red
        ));

        if (brushHandle == 0) {
            return;
        }

        try {
            _ = User32.FillRect(
                brushHandle: brushHandle,
                deviceContextHandle: deviceContextHandle,
                rectangle: in rectangle
            );
        } finally {
            _ = Gdi32.DeleteObject(objectHandle: brushHandle);
        }
    }
    private static uint CreateColorRef(byte red, byte green, byte blue) {
        return (uint)(red | (green << 8) | (blue << 16));
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
            m_pendingInput.Enqueue(item: InputPacket.TextInput(text: character.ToString()));
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

        var controlKeyState = User32.GetKeyState(virtualKey: VkControl);

        if (IsControlTabGesture(
            controlKeyState: controlKeyState,
            message: message,
            wParam: wParam
        )) {
            m_pendingInput.Enqueue(item: InputPacket.CycleFocus());
            return 0;
        }

        if (IsControlPressed(controlKeyState: controlKeyState)) {
            switch (wParam.ToInt64()) {
                case VkA:
                    m_pendingInput.Enqueue(item: InputPacket.SelectAll());
                    return 0;
                case VkC:
                    m_pendingInput.Enqueue(item: InputPacket.CopyInput());
                    return 0;
                case VkV:
                    if (
                        m_clipboardService.TryGetText(text: out var clipboardText) &&
                        (clipboardText.Length > 0)
                    ) {
                        m_pendingInput.Enqueue(item: InputPacket.TextInput(text: clipboardText));
                    }

                    return 0;
            }
        }

        switch (wParam.ToInt64()) {
            case VkOem3:
                m_pendingInput.Enqueue(item: InputPacket.ToggleConsole());
                m_suppressNextCharacterInput = true;
                return 0;
            case VkBack:
                m_pendingInput.Enqueue(item: InputPacket.Backspace());
                return 0;
            case VkEscape:
                m_pendingInput.Enqueue(item: InputPacket.ToggleMainMenu());
                return 0;
            case VkReturn:
                m_pendingInput.Enqueue(item: InputPacket.Submit());
                return 0;
            case VkF1:
                m_pendingInput.Enqueue(item: InputPacket.Function1());
                return 0;
            case VkF2:
                m_pendingInput.Enqueue(item: InputPacket.Function2());
                return 0;
            case VkF3:
                m_pendingInput.Enqueue(item: InputPacket.Function3());
                return 0;
            case VkF4:
                m_pendingInput.Enqueue(item: InputPacket.Function4());
                return 0;
            case VkF5:
                m_pendingInput.Enqueue(item: InputPacket.Function5());
                return 0;
            case VkF6:
                m_pendingInput.Enqueue(item: InputPacket.Function6());
                return 0;
            case VkF7:
                m_pendingInput.Enqueue(item: InputPacket.Function7());
                return 0;
            case VkF8:
                m_pendingInput.Enqueue(item: InputPacket.Function8());
                return 0;
            case VkUp:
                m_pendingInput.Enqueue(item: InputPacket.ArrowUp());
                return 0;
            case VkDown:
                m_pendingInput.Enqueue(item: InputPacket.ArrowDown());
                return 0;
            case VkLeft:
                m_pendingInput.Enqueue(item: InputPacket.ArrowLeft());
                return 0;
            case VkRight:
                m_pendingInput.Enqueue(item: InputPacket.ArrowRight());
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
    private nint HandleMouseMove(nint lParam) {
        var mouseX = GetSignedLowWord(value: lParam);
        var mouseY = GetSignedHighWord(value: lParam);

        if (
            (m_lastMouseX is { } lastMouseX) &&
            (m_lastMouseY is { } lastMouseY)
        ) {
            var deltaX = (mouseX - lastMouseX);
            var deltaY = (mouseY - lastMouseY);

            if (
                (deltaX != 0) ||
                (deltaY != 0)
            ) {
                m_pendingInput.Enqueue(item: InputPacket.MouseMove(
                    deltaX: deltaX,
                    deltaY: deltaY
                ));
            }
        }

        m_lastMouseX = mouseX;
        m_lastMouseY = mouseY;
        return 0;
    }

    internal static bool IsAltEnterGesture(uint message, nint wParam, short altKeyState) {
        return (
            (message == WmSysKeyDown) &&
            (wParam.ToInt64() == VkReturn) &&
            ((altKeyState & 0x8000) != 0)
        );
    }
    internal static bool IsControlTabGesture(uint message, nint wParam, short controlKeyState) {
        return (
            (message == WmKeyDown) &&
            (wParam.ToInt64() == VkTab) &&
            IsControlPressed(controlKeyState: controlKeyState)
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
    private static bool IsControlPressed(short controlKeyState) {
        return ((controlKeyState & 0x8000) != 0);
    }
}
