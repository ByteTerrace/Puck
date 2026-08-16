using Microsoft.Extensions.Options;
using Puck.Input;

namespace Puck.Platform.Switch;

/// <summary>The Nintendo Switch (VI) <see cref="INativeWindow"/>, adapting an
/// <see cref="ISwitchViWindowBackend"/> (the licensed SDK shim) to the engine's window
/// contract and emitting a <see cref="ViNativeSurfaceBinding"/> for Vulkan surface creation.
/// HID/controller input is handled separately and is out of scope here.</summary>
internal sealed class ViNativeWindow : INativeWindow, IWindowInputSource {
    private readonly ISwitchViWindowBackend m_backend;
    private readonly NativeWindowOptions m_options;
    private bool m_disposed;
    private bool m_hasPainted;
    private bool m_isVisible;

    public ViNativeWindow(ISwitchViWindowBackend backend, IOptions<NativeWindowOptions> options) {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);

        m_backend = backend;
        m_options = options.Value;
        Width = m_backend.Width;
        Height = m_backend.Height;
    }

    public NativeDisplayKind DisplayKind => NativeDisplayKind.Vi;
    public bool HasPainted => m_hasPainted;
    public uint Height { get; private set; }
    public bool IsOpen => (!m_disposed && m_backend.IsOpen);
    public bool IsVisible => m_isVisible;
    public ulong ResizeCount { get; private set; }
    public string Title => m_options.Title;
    public uint Width { get; private set; }

    public void Close() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_backend.Close();
        m_isVisible = false;
    }
    public NativeSurfaceBinding CreateSurfaceBinding() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_backend.NativeWindowHandle == 0) {
            throw new InvalidOperationException(message: "The Nintendo Switch (VI) native window handle is not available.");
        }

        return new NativeSurfaceBinding(
            DisplayKind: DisplayKind,
            Vi: new ViNativeSurfaceBinding(WindowHandle: m_backend.NativeWindowHandle)
        );
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_backend.Dispose();
        m_isVisible = false;
        m_disposed = true;
    }
    public void PollEvents() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_backend.Poll();
        UpdateExtent();
    }
    public void Show() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_backend.Show();
        UpdateExtent();
        m_isVisible = true;

        // VI presents the docked/handheld layer immediately; there is no expose event, so
        // first paint is satisfied once the layer is shown.
        m_hasPainted = true;
    }
    public bool TryDequeueInput(out WindowInputEvent inputEvent) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        inputEvent = default;
        return false;
    }

    private void UpdateExtent() {
        var width = m_backend.Width;
        var height = m_backend.Height;

        if (
            (width == 0) ||
            (height == 0)
        ) {
            return;
        }

        if (
            (width != Width) ||
            (height != Height)
        ) {
            Width = width;
            Height = height;
            ResizeCount++;
        }
    }
}
