namespace Puck.Platform;

public sealed class NativeWindowPlatformSupport(INativeDisplayEnvironment nativeDisplayEnvironment) : INativeWindowPlatformSupport {
    public static bool HasWindowBackend(NativeDisplayKind displayKind) {
        return (displayKind is NativeDisplayKind.Win32
            or NativeDisplayKind.Wayland
            or NativeDisplayKind.Xcb
            or NativeDisplayKind.Vi);
    }

    private readonly INativeDisplayEnvironment m_nativeDisplayEnvironment = nativeDisplayEnvironment;

    public NativeDisplayKind CurrentDisplayKind => NativeDisplayKindSelector.Select(
        platform: m_nativeDisplayEnvironment.CurrentPlatform,
        waylandDisplay: m_nativeDisplayEnvironment.WaylandDisplay,
        xdgSessionType: m_nativeDisplayEnvironment.XdgSessionType
    );
    public bool SupportsPlatformWindow => HasWindowBackend(displayKind: CurrentDisplayKind);

    public NativeWindowPlatformSupport()
        : this(new NativeDisplayEnvironment()) {
    }

    public NativeDisplayKind ResolveDisplayKind(NativeDisplayKind requested) {
        return ((requested == NativeDisplayKind.Auto)
            ? CurrentDisplayKind
            : requested);
    }
    public bool SupportsWindowFor(NativeDisplayKind requested) {
        return HasWindowBackend(displayKind: ResolveDisplayKind(requested: requested));
    }
}
