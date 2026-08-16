using Microsoft.Extensions.Options;

namespace Puck.Platform.Linux;

/// <summary>The <see cref="NativeDisplayKind.Wayland"/> <see cref="INativeWindowBackend"/>.</summary>
internal sealed class WaylandNativeWindowBackend : INativeWindowBackend {
    public NativeDisplayKind Kind => NativeDisplayKind.Wayland;

    public INativeWindow Create(NativeWindowOptions options) => new WaylandNativeWindow(options: Options.Create(options: options));
}
