using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Puck.Platform;

/// <summary>
/// Registers the OS-neutral half of the native-windowing stack: the display-environment probe, platform-window
/// support, and the native window factory (which dispatches over whatever <see cref="INativeWindowBackend"/>s are
/// registered). The composition root calls this AND one of <c>Puck.Platform.Windows</c>'s
/// <c>AddWindowsPlatformWindowing</c> or <c>Puck.Platform.Linux</c>'s <c>AddLinuxPlatformWindowing</c> — the latter
/// contributes the concrete clipboard service and window backend(s), so this method alone leaves no
/// <see cref="IClipboardService"/> and no window backend registered.
/// </summary>
public static class PlatformWindowingServiceRegistration {
    /// <summary>Registers the display probe, platform support, and native window factory.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddPlatformWindowing(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<INativeDisplayEnvironment, NativeDisplayEnvironment>();
        services.TryAddSingleton<INativeWindowPlatformSupport, NativeWindowPlatformSupport>();
        services.TryAddSingleton<INativeWindowFactory, NativeWindowFactory>();

        return services;
    }
}
