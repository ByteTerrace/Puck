using Microsoft.Extensions.Options;

using Puck.Input;

namespace Puck.Platform;

internal sealed class ConfiguredNativeWindow(IOptions<NativeWindowOptions> options) : INativeWindow, IWindowInputSource {
    private bool m_disposed;
    private readonly NativeWindowOptions m_options = options.Value;
    private bool m_visible;

    public NativeDisplayKind DisplayKind { get; } = NativeDisplayKind.Headless;
    public bool HasPainted => (m_visible && !m_disposed);
    public uint Height => m_options.Height;
    public bool IsOpen => !m_disposed;
    public bool IsVisible => m_visible;
    public ulong ResizeCount => 0;
    public string Title => m_options.Title;
    public uint Width => m_options.Width;

    public void Close() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_visible = false;
        m_disposed = true;
    }
    public NativeSurfaceBinding CreateSurfaceBinding() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        return new NativeSurfaceBinding(DisplayKind);
    }
    public void Dispose() {
        m_disposed = true;
        m_visible = false;
    }
    public void PollEvents() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
    }
    public void Show() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_visible = true;
    }
    public bool TryDequeueInput(out WindowInputEvent inputEvent) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        inputEvent = default;
        return false;
    }
}
