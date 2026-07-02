using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The Game Boy Advance composition root. Every subsystem (CPU, PPU, APU, DMA, timers, interrupt controller,
/// bus, cartridge, BIOS) is registered here behind its interface with <c>TryAdd</c>, so a test or alternate
/// composition can <see cref="ServiceCollectionDescriptorExtensions"/>-style replace any one of them — a
/// headless stub PPU, a tracing CPU decorator, a second implementation to A/B against the same test ROM —
/// by registering its own before calling these, with no edit to this root.
/// <para>
/// Stateful subsystems are registered <b>scoped</b>: create one <see cref="IServiceScope"/> per machine and
/// resolve the machine from that scope, so two machines never share a component instance. Immutable, shareable
/// services (such as the BIOS image) are singletons.
/// </para>
/// </summary>
public static class GameBoyAdvanceServiceRegistration {
    /// <summary>Registers the shared Game Boy Advance services. Subsystems are added here as they are built;
    /// supply a BIOS with <see cref="AddReplacementBios"/> (or register a custom <see cref="IBios"/> first).</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddGameBoyAdvance(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        // Subsystem registrations accrue here as each step lands (Ppu, timers, DMA, APU, …), each TryAddScoped so
        // a prior Replace/registration wins — the swappability seam. Scoped lifetime + one scope per machine keeps
        // two machines from sharing a stateful component instance. The consumer supplies the per-machine
        // GbaCartridge and a BIOS (AddReplacementBios) before resolving the machine.
        services.TryAddScoped<GbaScheduler>();
        services.TryAddScoped<IArmCpu, Arm7Tdmi>();
        services.TryAddScoped<IGbaInterruptController, GbaInterruptController>();
        services.TryAddScoped<IGbaTimerController, GbaTimerController>();
        services.TryAddScoped<IGbaDmaController, GbaDmaController>();
        services.TryAddScoped<IGbaSerialController, GbaSerialController>();
        services.TryAddScoped<IGbaPpu, GbaPpu>();
        services.TryAddScoped<IGbaApu, GbaApu>();
        services.TryAddScoped<IGbaBus, GbaBus>();
        services.TryAddScoped<GameBoyAdvanceMachine>();

        return services;
    }

    /// <summary>Registers the open-source replacement BIOS as the default <see cref="IBios"/>. Registered with
    /// <c>TryAdd</c>, so a real dumped BIOS registered earlier takes precedence.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="image">The 16&#160;KiB replacement BIOS image.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="image"/> is not exactly <see cref="ReplacementBios.ImageSize"/> bytes.</exception>
    public static IServiceCollection AddReplacementBios(this IServiceCollection services, ReadOnlyMemory<byte> image) {
        ArgumentNullException.ThrowIfNull(services);

        var bios = new ReplacementBios(image: image.Span);

        services.TryAddSingleton<IBios>(instance: bios);

        return services;
    }
}
