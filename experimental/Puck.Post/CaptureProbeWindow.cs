using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Puck.Abstractions.Windowing;
using Puck.Platform;
using Puck.Platform.Windows;

namespace Puck.Post;

/// <summary>
/// A visible, self-pumping Win32 probe window painted with a non-uniform two-tone pattern that changes one channel on
/// every paint, so a Windows Graphics Capture consumer measures the sampler's cadence rather than compositor
/// deduplication. Shared by the isolated <see cref="CaptureLifetimeProbe"/> (which also drives its hostile lifetime
/// states) and the in-process <see cref="CaptureShareStage"/> GPU-transport proof.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class CaptureProbeWindow : IDisposable {
    private readonly ManualResetEventSlim m_ready = new(initialState: false);
    private readonly Thread m_thread;
    private Exception? m_failure;
    private volatile bool m_pumping = true;
    private volatile bool m_stop;
    private nint m_windowHandle;

    public CaptureProbeWindow() {
        Title = $"Puck Capture Probe {Guid.NewGuid():N}";
        m_thread = new Thread(start: WindowLoop) {
            IsBackground = true,
            Name = "capture-hostile-window",
        };
        m_thread.SetApartmentState(state: ApartmentState.STA);
        m_thread.Start();
        if (!m_ready.Wait(millisecondsTimeout: 2000) || (m_windowHandle == 0)) {
            throw new InvalidOperationException(message: m_failure?.Message ?? "The probe window did not initialize.");
        }
    }

    public bool Pumping {
        get => m_pumping;
        set => m_pumping = value;
    }
    public string Title { get; }

    public void Resize(int width, int height) {
        if (!SetWindowPos(window: m_windowHandle, insertAfter: 0, x: 80, y: 80, width: width, height: height, flags: 0x0014)) {
            throw new InvalidOperationException(message: $"SetWindowPos failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }
    public void Minimize() => _ = ShowWindow(window: m_windowHandle, command: 6);
    public void Restore() => _ = ShowWindow(window: m_windowHandle, command: 9);
    public void Close() {
        m_stop = true;
        _ = m_thread.Join(millisecondsTimeout: 2000);
    }
    public void Dispose() {
        Close();
        m_ready.Dispose();
    }

    private void WindowLoop() {
        try {
            using var window = new Win32NativeWindow(
                clipboardService: new NullClipboardService(),
                options: Options.Create(new NativeWindowOptions {
                    DisplayKind = NativeDisplayKind.Win32,
                    Height = 240,
                    Mode = NativeWindowMode.PlatformWindow,
                    Title = Title,
                    Width = 360,
                })
            );
            window.Show();
            m_windowHandle = window.CreateSurfaceBinding().Win32!.Value.WindowHandle;
            var paintPhase = 0;
            PaintPattern(windowHandle: m_windowHandle, phase: paintPhase++);
            m_ready.Set();
            var paintCountdown = 0;
            while (!m_stop) {
                if (m_pumping) {
                    window.PollEvents();
                    if (paintCountdown-- <= 0) {
                        _ = SetWindowText(window: m_windowHandle, text: $"{Title} {paintPhase & 1}");
                        PaintPattern(windowHandle: m_windowHandle, phase: paintPhase++);
                        paintCountdown = 5;
                    }
                }

                Thread.Sleep(millisecondsTimeout: 2);
            }
        } catch (Exception exception) {
            m_failure = exception;
            m_ready.Set();
        } finally {
            m_windowHandle = 0;
        }
    }

    private static void PaintPattern(nint windowHandle, int phase) {
        var deviceContext = GetDC(window: windowHandle);
        if (deviceContext == 0) {
            return;
        }

        // WGC is damage-driven and does not promise callbacks when a window redraws identical pixels. Change one
        // channel on every paint so the cadence probe measures our sampler instead of compositor deduplication.
        var pulse = (uint)(phase & 0x1F);
        var leftBrush = CreateSolidBrush(color: 0x002060C0u + pulse);
        var rightBrush = CreateSolidBrush(color: 0x00C08020u + (pulse << 16));
        try {
            var left = new Rectangle { Left = 0, Top = 0, Right = 180, Bottom = 400 };
            var right = new Rectangle { Left = 180, Top = 0, Right = 800, Bottom = 400 };
            _ = FillRect(deviceContext: deviceContext, rectangle: in left, brush: leftBrush);
            _ = FillRect(deviceContext: deviceContext, rectangle: in right, brush: rightBrush);
        } finally {
            _ = DeleteObject(handle: leftBrush);
            _ = DeleteObject(handle: rightBrush);
            _ = ReleaseDC(window: windowHandle, deviceContext: deviceContext);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(nint window, nint deviceContext);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(nint window, string text);
    [DllImport("user32.dll")]
    private static extern int FillRect(nint deviceContext, in Rectangle rectangle, nint brush);
    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}
