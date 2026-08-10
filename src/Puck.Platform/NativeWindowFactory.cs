using Microsoft.Extensions.Options;
using Puck.Platform.Linux;
using Puck.Platform.Switch;
using Puck.Platform.Windows;

namespace Puck.Platform;

/// <remarks>
/// Exempt from the constructor rule's retained-<see cref="IServiceProvider"/> prohibition.
/// <see cref="m_serviceProvider"/> is asked exactly
/// once, in <see cref="CreateViWindow"/>, for the optional, licensed <c>ISwitchViWindowBackend</c> — a platform
/// presence probe, not service location for a producer's own dependencies: <see langword="null"/> is the correct,
/// expected answer on every build that does not carry the closed-source Switch SDK, and the caller turns that
/// answer into a named, loud <see cref="PlatformNotSupportedException"/> rather than treating it as failure to
/// resolve a required collaborator. This is the only exemption; any future retained provider owes the same explicit
/// pin at its own declaration, or it is indistinguishable from the defect the rule exists to catch.
/// </remarks>
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
            return new ConfiguredNativeWindow(options: Options.Create(options: m_options));
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
                clipboardService: m_clipboardService,
                options: Options.Create(options: m_options)
            ),
            NativeDisplayKind.Wayland => new WaylandNativeWindow(options: Options.Create(options: m_options)),
            NativeDisplayKind.Xcb => new XcbNativeWindow(options: Options.Create(options: m_options)),
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
            options: Options.Create(options: m_options)
        );
    }
}
