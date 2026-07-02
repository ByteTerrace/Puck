using Puck.HumbleGamingBrick.Timing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The composition root for a Game Boy Color machine. It registers the timing core and the <see cref="Machine"/>
/// driver behind the scoped lifetime every subsystem shares, and derives the timeline's
/// <see cref="TickResolution"/> from the per-machine <see cref="MachineConfiguration"/>. Components are not registered
/// here — each is added by its own registration so a test or alternate composition can replace any one of them — and
/// the <see cref="Machine"/> picks them all up through the injected component lists.
/// </summary>
public static class MachineServiceRegistration {
    /// <summary>Registers the shared Game Boy Color services. The caller adds a per-machine
    /// <see cref="MachineConfiguration"/> and the component registrations to the same container before resolving
    /// <see cref="Machine"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddGameBoyColor(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        // The configuration drives the resolution; registering it before the timing core means the core's default
        // registration is a no-op (TryAdd) and the machine runs at the configured granularity.
        services.TryAdd(descriptor: ServiceDescriptor.Scoped(
            typeof(TickResolution),
            static provider => provider.GetRequiredService<MachineConfiguration>().TickResolution
        ));
        services.AddTimingCore();
        services.TryAddScoped<Machine>();

        return services;
    }
}
