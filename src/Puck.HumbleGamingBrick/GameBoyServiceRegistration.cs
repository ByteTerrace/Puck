using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The composition root for a monochrome/color handheld machine. Every subsystem (CPU, PPU, APU, timer, OAM DMA,
/// interrupt controller, joypad, serial, bus) is registered here behind its interface with <c>TryAdd</c>, so a test
/// or an alternate composition can replace any one of them — a headless stub PPU, a tracing CPU decorator, a second
/// implementation to A/B against the same ROM — by registering its own first, with no edit to this root.
/// <para>
/// Every service is registered <b>scoped</b>: create one <see cref="IServiceScope"/> per machine and resolve the
/// machine from that scope, so two machines never share a stateful component instance. The consumer supplies the
/// per-machine <see cref="MachineConfiguration"/> and <see cref="ICartridge"/> into that scope before resolving.
/// </para>
/// </summary>
public static class GameBoyServiceRegistration {
    /// <summary>Registers the shared handheld services. The consumer adds a per-machine
    /// <see cref="MachineConfiguration"/> and <see cref="ICartridge"/> to the scope before resolving
    /// <see cref="GameBoyMachine"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddGameBoy(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<SystemMemory>();
        services.TryAddScoped<ClockState>();
        services.TryAddScoped<IInterruptController, InterruptController>();
        services.TryAddScoped<ITimer, Timer>();
        services.TryAddScoped<IOamDma, OamDma>();
        services.TryAddScoped<IPpu, Ppu>();
        services.TryAddScoped<IJoypad, Joypad>();
        services.TryAddScoped<ISerial, Serial>();
        services.TryAddScoped<IApu, Apu>();
        services.TryAddScoped<ISystemBus, SystemBus>();
        // The CPU's narrow bus seam (ICpuBus) and the full bus (ISystemBus) resolve to the same instance.
        services.TryAddScoped<ICpuBus>(implementationFactory: provider => provider.GetRequiredService<ISystemBus>());
        services.TryAddScoped<ICpu, Sm83>();
        services.TryAddScoped<GameBoyMachine>();

        return services;
    }
}
