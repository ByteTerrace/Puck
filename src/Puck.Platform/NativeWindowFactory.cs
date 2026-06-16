using Microsoft.Extensions.Options;
using Puck.Platform.Linux;
using Puck.Platform.Switch;
using Puck.Platform.Windows;

namespace Puck.Platform;

public sealed class NativeWindowFactory(
    IClipboardService clipboardService,
    IOptions<NativeWindowOptions> options,
    INativeWindowPlatformSupport platformSupport,
    IServiceProvider serviceProvider
) : INativeWindowFactory {
    private readonly IClipboardService m_clipboardService = clipboardService;
    private readonly NativeWindowOptions m_options = options.Value;
    private readonly INativeWindowPlatformSupport m_platformSupport = platformSupport;
    private readonly IServiceProvider m_serviceProvider = serviceProvider;

    public INativeWindow Create() {
        if (m_options.Mode == NativeWindowMode.Headless) {
            return new ConfiguredNativeWindow(Options.Create(m_options));
        }

        if (m_options.Mode != NativeWindowMode.PlatformWindow) {
            throw new ArgumentOutOfRangeException(
                actualValue: m_options.Mode,
                message: "Unsupported native window mode.",
                paramName: nameof(m_options)
            );
        }

        var displayKind = m_platformSupport.ResolveDisplayKind(requested: m_options.DisplayKind);

        return displayKind switch {
            NativeDisplayKind.Win32 => new Win32NativeWindow(
                m_clipboardService,
                Options.Create(m_options)
            ),
            NativeDisplayKind.Wayland => new WaylandNativeWindow(Options.Create(m_options)),
            NativeDisplayKind.Xcb => new XcbNativeWindow(Options.Create(m_options)),
            NativeDisplayKind.Vi => CreateViWindow(),
            _ => throw new PlatformNotSupportedException(message: $"Platform windows for display kind '{displayKind}' are not implemented.")
        };
    }

    private INativeWindow CreateViWindow() {
        var backend = (ISwitchViWindowBackend?)m_serviceProvider.GetService(serviceType: typeof(
                ISwitchViWindowBackend
            ));

        if (backend is null) {
            throw new PlatformNotSupportedException(message: "Nintendo Switch (VI) windowing requires the licensed Puck Switch SDK backend (ISwitchViWindowBackend) to be registered; it is not part of the open-source build.");
        }

        return new ViNativeWindow(
            backend: backend,
            options: Options.Create(m_options)
        );
    }
}
