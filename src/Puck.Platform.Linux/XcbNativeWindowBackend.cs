using Microsoft.Extensions.Options;

namespace Puck.Platform.Linux;

/// <summary>The <see cref="NativeDisplayKind.Xcb"/> <see cref="INativeWindowBackend"/>.</summary>
internal sealed class XcbNativeWindowBackend : INativeWindowBackend {
    public NativeDisplayKind Kind => NativeDisplayKind.Xcb;

    public INativeWindow Create(NativeWindowOptions options) => new XcbNativeWindow(options: Options.Create(options: options));
}
