using Microsoft.Extensions.Options;

namespace Puck.Platform;

public sealed class NativeWindowFactory : INativeWindowFactory {
    private readonly IReadOnlyDictionary<NativeDisplayKind, INativeWindowBackend> m_backends;
    private readonly NativeWindowOptions m_options;
    private readonly INativeWindowPlatformSupport m_platformSupport;

    public NativeWindowFactory(
        IEnumerable<INativeWindowBackend> backends,
        IOptions<NativeWindowOptions> options,
        INativeWindowPlatformSupport platformSupport
    ) {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(platformSupport);

        m_backends = backends.ToDictionary(keySelector: static backend => backend.Kind);
        m_options = options.Value;
        m_platformSupport = platformSupport;
    }

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

        return (m_backends.TryGetValue(key: displayKind, value: out var backend)
            ? backend.Create(options: m_options)
            : throw new PlatformNotSupportedException(message: $"Platform windows for display kind '{displayKind}' are not implemented."));
    }
}
