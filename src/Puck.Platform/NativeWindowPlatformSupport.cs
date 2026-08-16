namespace Puck.Platform;

/// <remarks>
/// <see cref="HasWindowBackend"/> is a static membership table over <see cref="NativeDisplayKind"/>, not a read of
/// which <see cref="INativeWindowBackend"/>s are actually registered — it can claim support for a kind a
/// single-platform build never linked (e.g. it reports <see cref="NativeDisplayKind.Wayland"/> true on a build that
/// referenced only <c>Puck.Platform.Windows</c>). <see cref="NativeWindowFactory"/> is what actually enforces the
/// registered set, throwing <see cref="PlatformNotSupportedException"/> for a kind with no matching backend; this
/// table exists for validation (<see cref="NativeWindowOptionsValidator"/>, which self-constructs with no DI
/// container) to give an early, host-OS-shaped answer before a window is ever requested.
/// </remarks>
internal sealed class NativeWindowPlatformSupport(INativeDisplayEnvironment nativeDisplayEnvironment) : INativeWindowPlatformSupport {
    public static bool HasWindowBackend(NativeDisplayKind displayKind) {
        return (displayKind is NativeDisplayKind.Win32
            or NativeDisplayKind.Wayland
            or NativeDisplayKind.Xcb);
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
