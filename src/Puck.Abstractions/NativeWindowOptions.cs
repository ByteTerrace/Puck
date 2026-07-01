namespace Puck.Abstractions;

/// <summary>
/// The window-creation preferences a host registers (via the options pattern) for <see cref="INativeWindowFactory"/>
/// to consume: mode, display kind, extent, title, and the best-effort presentation flags. Values are validated at
/// startup — the title must be non-blank, the extent non-zero and at most <see cref="int.MaxValue"/>, and the mode
/// supported for the resolved display kind.
/// </summary>
public sealed class NativeWindowOptions {
    /// <summary>The requested windowing system. Defaults to <see cref="NativeDisplayKind.Auto"/>, which resolves to
    /// the platform's detected kind; set a concrete kind to pin it (required for <see cref="NativeDisplayKind.Vi"/>,
    /// which is never auto-detected).</summary>
    public NativeDisplayKind DisplayKind { get; set; } = NativeDisplayKind.Auto;
    /// <summary>The initial window height, in pixels. Defaults to 600.</summary>
    public uint Height { get; set; } = 600;
    /// <summary>Whether to hide the mouse cursor while it is over the window. Best-effort: only the Win32 backend
    /// honors it today. Defaults to <see langword="false"/>.</summary>
    public bool HideMouseCursor { get; set; }
    /// <summary>Whether a real platform window or a headless stand-in is created. Defaults to
    /// <see cref="NativeWindowMode.Headless"/>.</summary>
    public NativeWindowMode Mode { get; set; } = NativeWindowMode.Headless;
    /// <summary>Whether the window enters borderless fullscreen when first shown. Best-effort: only the Win32 backend
    /// honors it today (Alt+Enter toggles it thereafter). Defaults to <see langword="false"/>.</summary>
    public bool StartFullscreen { get; set; }
    /// <summary>The window title. Must be non-blank. Defaults to <c>"Puck"</c>.</summary>
    public string Title { get; set; } = "Puck";
    /// <summary>The initial window width, in pixels. Defaults to 800.</summary>
    public uint Width { get; set; } = 800;
}
