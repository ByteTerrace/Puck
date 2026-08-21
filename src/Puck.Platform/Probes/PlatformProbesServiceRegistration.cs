using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Puck.Platform.Probes;

/// <summary>
/// Registers the "no sense kernel host" fallback for a platform with no <see cref="IProbeKernelHost"/> backend. The
/// composition root calls this from its neutral branch — the counterpart of
/// <c>Puck.Platform.Windows</c>'s <c>WindowsProbesServiceRegistration.AddWindowsProbes</c> — so a KERNEL probe
/// always resolves an <see cref="IProbeKernelHost"/> and faults by name instead of failing to resolve at all.
/// </summary>
public static class PlatformProbesServiceRegistration {
    /// <summary>Registers <see cref="NullProbeKernelHost"/> as the platform's <see cref="IProbeKernelHost"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddNullProbes(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProbeKernelHost, NullProbeKernelHost>();

        return services;
    }
}
