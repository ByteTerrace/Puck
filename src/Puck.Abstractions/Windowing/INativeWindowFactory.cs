namespace Puck.Abstractions.Windowing;

/// <summary>
/// Creates the host's native window from the registered <see cref="NativeWindowOptions"/>: a headless stand-in when
/// <see cref="NativeWindowOptions.Mode"/> is <see cref="NativeWindowMode.Headless"/>, otherwise the platform window
/// for the resolved <see cref="NativeWindowOptions.DisplayKind"/>.
/// </summary>
public interface INativeWindowFactory {
    /// <summary>Creates a new window; the caller owns it and disposes it.</summary>
    /// <returns>The new window.</returns>
    /// <exception cref="PlatformNotSupportedException">The resolved display kind has no platform-window backend (or,
    /// for <see cref="NativeDisplayKind.Vi"/>, the licensed Switch backend is not registered).</exception>
    INativeWindow Create();
}
