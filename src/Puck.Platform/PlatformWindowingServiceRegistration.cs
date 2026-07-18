using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Platform.Windows;

namespace Puck.Platform;

/// <summary>
/// Registers the concrete native-windowing stack: the display-environment probe, platform-window support, the clipboard
/// service (Win32 or a no-op), and the native window factory. The composition root calls this to supply the platform
/// IMPLEMENTATIONS behind the windowing contract that the generic launcher consumes — so the launcher itself references
/// nothing platform-specific.
/// </summary>
public static class PlatformWindowingServiceRegistration {
    /// <summary>Registers the native window factory + its supporting platform services.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddPlatformWindowing(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<INativeDisplayEnvironment, NativeDisplayEnvironment>();
        services.TryAddSingleton<INativeWindowPlatformSupport, NativeWindowPlatformSupport>();
        services.TryAddSingleton<IClipboardService>(implementationFactory: static sp =>
            ((sp.GetRequiredService<INativeWindowPlatformSupport>().CurrentDisplayKind == NativeDisplayKind.Win32)
                ? new Win32ClipboardService()
                : new NullClipboardService()));
        services.TryAddSingleton<INativeWindowFactory, NativeWindowFactory>();

        return services;
    }
}
