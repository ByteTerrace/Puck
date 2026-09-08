using Microsoft.Extensions.DependencyInjection;
using Puck.DirectX.Presentation;
using Puck.Memory;
using Puck.Platform;
using Puck.Platform.Windows;
using Puck.Vulkan.Presentation;

namespace Puck.Launcher.Windows;

/// <summary>
/// Registers the Windows GPU-host block a composition root shares across windowed entry points: platform windowing
/// (native window factory, Win32 clipboard, allocator) and the one launch-selected Vulkan or Direct3D 12 presenter.
/// Only the selected backend enters the service provider so its neutral compute services, device, presenter, and
/// shader format cannot disagree. Does NOT register the launcher terminal or the backend switcher — those are
/// Engine-services calls (<c>Puck.Launcher.LauncherServiceRegistration</c>) the composition root makes itself, since
/// a Presentation-row project cannot reference Engine services (the upward edge <c>PUCKARCH001</c> refuses).
/// </summary>
public static class WindowsPresentationRegistration {
    /// <summary>Registers windowing, the allocator, and the selected presenter.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="hostsOnDirectX">Whether Direct3D 12 is the selected host backend (else Vulkan).</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="PlatformNotSupportedException"><paramref name="hostsOnDirectX"/> is <see langword="true"/> and the OS is older than Windows 10.</exception>
    public static IServiceCollection AddWindowsHostedPresentation(this IServiceCollection services, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPlatformWindowing();
        services.AddWindowsPlatformWindowing();
        services.AddPuckAllocator();

        if (hostsOnDirectX) {
            if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)) {
                throw new PlatformNotSupportedException(message: "The Direct3D 12 host requires Windows 10 or newer.");
            }

            services.AddDirectXPresenter();
            services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(
                Name: "directx",
                // The outer IsWindowsVersionAtLeast throw above already refused this path on an older OS; the
                // analyzer cannot see across this lambda into that guard, so it is restated here for CA1416.
                Presenter: (OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)
                    ? sp.GetRequiredService<DirectXSurfacePresenter>()
                    : throw new PlatformNotSupportedException(message: "The Direct3D 12 host requires Windows 10 or newer."))
            ));
        } else {
            services.AddVulkanHostedPresentation();
        }

        return services;
    }
}
