using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Platform.Windows;

namespace Puck.Platform;

/// <summary>
/// Registers the live camera-capture backend (parallel to <see cref="PlatformWindowingServiceRegistration.AddPlatformWindowing"/>):
/// Media Foundation on Windows, the null service everywhere else. The composition root calls this so a live-camera
/// content source resolves <see cref="ICameraCaptureService"/> from DI rather than self-constructing a backend.
/// </summary>
public static class CameraCaptureServiceRegistration {
    /// <summary>Registers the platform <see cref="ICameraCaptureService"/> implementation.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddCameraCapture(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICameraCaptureService>(implementationFactory: static _ =>
            (OperatingSystem.IsWindows()
                ? new Win32MediaFoundationCameraService()
                : new NullCameraCaptureService()));

        return services;
    }
}
