using Microsoft.Extensions.DependencyInjection;
using Puck.Memory;
using Puck.Platform;
using Puck.Platform.Linux;
using Puck.Vulkan.Presentation;

namespace Puck.Launcher.Linux;

/// <summary>
/// Registers the Linux GPU-host block: platform windowing (native window factory, null clipboard, Wayland/Xcb
/// backends), the allocator, and the Vulkan presenter — the only backend this platform serves; there is no
/// <c>hostsOnDirectX</c> parameter because Direct3D 12 never appears in this project's closure. Does NOT register
/// the launcher terminal or the backend switcher — see <c>Puck.Launcher.Windows.WindowsPresentationRegistration</c>'s
/// remarks for why those stay the composition root's own Engine-services calls.
/// </summary>
public static class LinuxPresentationRegistration {
    /// <summary>Registers windowing, the allocator, and the Vulkan presenter.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddLinuxHostedPresentation(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPlatformWindowing();
        services.AddLinuxPlatformWindowing();
        services.AddPuckAllocator();
        services.AddVulkanHostedPresentation();

        return services;
    }
}
