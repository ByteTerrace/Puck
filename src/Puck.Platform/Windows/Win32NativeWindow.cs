using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Puck.Commands;
using Puck.Input;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

internal sealed partial class Win32NativeWindow : INativeWindow, INativeSurfaceSourceProvider, INativeWindowLoadingPresenter, IWindowInputSource {
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
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkShift = 0x10;
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
    private const uint WmInput = 0x00FF;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmMouseMove = 0x0200;
    private const uint WmNcCreate = 0x0081;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmPaint = 0x000F;
    private const uint WmSetCursor = 0x0020;
    private const uint WmShowWindow = 0x0018;
    private const uint WmSize = 0x0005;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    // Raw Input (WM_INPUT): un-accelerated, full-rate relative mouse motion, summed pump-level per frame.
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const ushort RiMouseMoveAbsolute = 0x01;
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort HidUsageGenericMouse = 0x02;
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

        FlushPointerFrame();
    }

    private void FlushPointerFrame() {
        // Emit at most one pointer.move (the frame's summed relative motion) and one pointer.position per
        // frame, so a high-rate mouse that produced many WM_INPUT packets collapses to a single delta the
        // command registry records correctly (its polled value is last-wins; one signal makes that exact).
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

        var modifiers = ComputeModifiers();

        // Ctrl+V pastes: the clipboard text flows through the text pipeline. (Copy/select-all/cycle-focus
        // are emitted below as first-class chords for the app to bind.)
        if (
            (modifiers == InputModifiers.Control) &&
            (wParam.ToInt64() == VkV) &&
            m_clipboardService.TryGetText(text: out var clipboardText) &&
            (clipboardText.Length > 0)
        ) {
            m_pendingInput.Enqueue(item: WindowInputEvent.TypedText(text: clipboardText));
            return 0;
        }

        switch (wParam.ToInt64()) {
            case VkOem3:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Backtick, modifiers: modifiers));
                m_suppressNextCharacterInput = true;
                return 0;
            case VkBack:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Backspace, modifiers: modifiers));
                return 0;
            case VkEscape:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Escape, modifiers: modifiers));
                return 0;
            case VkReturn:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Enter, modifiers: modifiers));
                return 0;
            case VkF1:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F1, modifiers: modifiers));
                return 0;
            case VkF2:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F2, modifiers: modifiers));
                return 0;
            case VkF3:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F3, modifiers: modifiers));
                return 0;
            case VkF4:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F4, modifiers: modifiers));
                return 0;
            case VkF5:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F5, modifiers: modifiers));
                return 0;
            case VkF6:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F6, modifiers: modifiers));
                return 0;
            case VkF7:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F7, modifiers: modifiers));
                return 0;
            case VkF8:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.F8, modifiers: modifiers));
                return 0;
            case VkUp:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.ArrowUp, modifiers: modifiers));
                return 0;
            case VkDown:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.ArrowDown, modifiers: modifiers));
                return 0;
            case VkLeft:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.ArrowLeft, modifiers: modifiers));
                return 0;
            case VkRight:
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.ArrowRight, modifiers: modifiers));
                return 0;
            case VkTab when (modifiers != InputModifiers.None):
                m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: KeyCode.Tab, modifiers: modifiers));
                return 0;
            case VkA when (modifiers != InputModifiers.None):
                m_pendingInput.Enqueue(item: WindowInputEvent.LetterDown(character: 'a', modifiers: modifiers));
                return 0;
            case VkC when (modifiers != InputModifiers.None):
                m_pendingInput.Enqueue(item: WindowInputEvent.LetterDown(character: 'c', modifiers: modifiers));
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
        // Release edges for the named navigation/function/special keys. Letter chords (Ctrl+A/Ctrl+C) are
        // one-shot press actions, so they have no useful release edge. Releases are inert by default
        // (CommandBinding.ActivateOn ignores Completed) — they exist so a future held-key feature needs no
        // seam re-cut. Modifiers are not recomputed: a key-up carries no chord intent.
        var virtualKey = wParam.ToInt64();

        if (TryMapNamedKey(virtualKey: virtualKey, key: out var key)) {
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
    private static bool TryMapNamedKey(long virtualKey, out KeyCode key) {
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
            VkF1 => KeyCode.F1,
            VkF2 => KeyCode.F2,
            VkF3 => KeyCode.F3,
            VkF4 => KeyCode.F4,
            VkF5 => KeyCode.F5,
            VkF6 => KeyCode.F6,
            VkF7 => KeyCode.F7,
            VkF8 => KeyCode.F8,
            _ => KeyCode.None,
        };

        return (key != KeyCode.None);
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

        // WM_MOUSEMOVE owns the absolute position (pointer.position). It only owns the relative delta as a
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
    private static InputModifiers ComputeModifiers() {
        var modifiers = InputModifiers.None;

        if (IsKeyDown(virtualKey: VkControl)) {
            modifiers |= InputModifiers.Control;
        }

        if (IsKeyDown(virtualKey: VkShift)) {
            modifiers |= InputModifiers.Shift;
        }

        if (IsKeyDown(virtualKey: VkMenu)) {
            modifiers |= InputModifiers.Alt;
        }

        if (
            IsKeyDown(virtualKey: VkLWin) ||
            IsKeyDown(virtualKey: VkRWin)
        ) {
            modifiers |= InputModifiers.Super;
        }

        return modifiers;
    }
}
