using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Commands;

namespace Puck.World;

/// <summary>Registers the host's creation faults and the <c>gpu.faults</c> verb that arms them.</summary>
public static class GpuFaultsServiceRegistration {
    /// <summary>Registers one <see cref="GpuCreationFaults"/>, which each backend's services pass through when it
    /// creates them, and <see cref="GpuFaultsCommandModule"/> over it as an <see cref="ICommandModule"/>.</summary>
    /// <param name="services">The service collection of a presentation shape that creates a GPU device.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddGpuCreationFaults(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<GpuCreationFaults>();

        return services.AddSingleton<ICommandModule>(implementationFactory: static sp => new GpuFaultsCommandModule(faults: sp.GetRequiredService<GpuCreationFaults>()));
    }
}
