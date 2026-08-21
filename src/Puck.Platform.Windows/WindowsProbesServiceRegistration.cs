using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Platform.Probes;

namespace Puck.Platform.Windows;

/// <summary>
/// Registers the Windows <see cref="IProbeKernelHost"/>, the counterpart of
/// <c>Puck.Platform.Probes.PlatformProbesServiceRegistration.AddNullProbes</c> for the branch of the composition
/// root that also calls <see cref="WindowsPlatformServiceRegistration.AddWindowsCameraCapture"/>.
/// </summary>
public static class WindowsProbesServiceRegistration {
    /// <summary>Registers <see cref="Win32ProbeKernelHost"/> as the platform's <see cref="IProbeKernelHost"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    [SupportedOSPlatform("windows10.0.19041")]
    public static IServiceCollection AddWindowsProbes(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProbeKernelHost, Win32ProbeKernelHost>();

        return services;
    }
}
