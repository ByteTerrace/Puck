using Microsoft.Extensions.Options;

namespace Puck.Platform.Windows;

/// <summary>The <see cref="NativeDisplayKind.Win32"/> <see cref="INativeWindowBackend"/>.</summary>
/// <param name="clipboardService">The clipboard service the created window binds to.</param>
internal sealed class Win32NativeWindowBackend(IClipboardService clipboardService) : INativeWindowBackend {
    public NativeDisplayKind Kind => NativeDisplayKind.Win32;

    public INativeWindow Create(NativeWindowOptions options) => new Win32NativeWindow(
        clipboardService: clipboardService,
        options: Options.Create(options: options)
    );
}
