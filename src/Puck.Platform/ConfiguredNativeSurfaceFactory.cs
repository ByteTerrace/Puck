namespace Puck.Platform;

public sealed class ConfiguredNativeSurfaceFactory(INativeWindow nativeWindow) : INativeSurfaceFactory {
    private readonly INativeWindow m_nativeWindow = nativeWindow;

    public NativeDisplayKind DisplayKind => m_nativeWindow.DisplayKind;

    public NativeSurfaceBinding CreateSurfaceBinding() {
        return ((m_nativeWindow is INativeSurfaceSourceProvider sourceProvider)
            ? sourceProvider.CreateSurfaceBinding()
            : new NativeSurfaceBinding(m_nativeWindow.DisplayKind));
    }
}
