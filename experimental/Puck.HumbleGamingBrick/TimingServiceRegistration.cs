using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Timing;

/// <summary>
/// Registers the per-machine timing core — the <see cref="MasterClock"/> and the <see cref="ComponentClock"/> that
/// advances it and the timed components one CPU T-cycle at a time — behind the scoped lifetime every machine subsystem
/// shares. A consumer registers a <see cref="TickResolution"/> in the same scope to choose the sub-cycle granularity;
/// absent one, <see cref="TickResolution.Default"/> (quarter ticks) is used. Everything is added with <c>TryAdd</c>, so
/// a test or alternate composition can supply its own first.
/// </summary>
public static class TimingServiceRegistration {
    /// <summary>Registers the scoped timing core. Add a <see cref="TickResolution"/> to the scope first to override the
    /// default quarter-tick granularity.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddTimingCore(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        services.TryAdd(descriptor: ServiceDescriptor.Scoped(
            typeof(TickResolution),
            static _ => TickResolution.Default
        ));
        services.TryAddScoped(implementationFactory: static provider => new MasterClock(
            resolution: provider.GetRequiredService<TickResolution>()
        ));
        // The component set is fixed at composition time, so the clock resolves each timed component by its concrete
        // type and holds it as a typed field — the per-T-cycle fan-out is direct sealed calls (ideal plan §7). A test
        // substitutes a component by pre-registering its own instance for the same concrete type.
        services.TryAddScoped(implementationFactory: static provider => new ComponentClock(
            clock: provider.GetRequiredService<MasterClock>(),
            timer: provider.GetRequiredService<TimerComponent>(),
            key1: provider.GetRequiredService<Key1Component>(),
            serial: provider.GetRequiredService<SerialComponent>(),
            apu: provider.GetRequiredService<ApuComponent>(),
            apuGeneratorClock: provider.GetRequiredService<ApuGeneratorClock>(),
            audioOutput: provider.GetRequiredService<AudioOutputComponent>(),
            cartridge: provider.GetRequiredService<ICartridge>(),
            oamDma: provider.GetRequiredService<OamDmaController>(),
            ppu: provider.GetRequiredService<Ppu>(),
            hdma: provider.GetRequiredService<HdmaController>()
        ));

        return services;
    }
}
