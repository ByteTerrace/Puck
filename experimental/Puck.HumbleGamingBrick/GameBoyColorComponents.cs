using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// Registers the machine's hardware components — the cartridge, internal RAM, interrupt controller, and system bus —
/// into a container already seeded by <see cref="MachineServiceRegistration.AddGameBoyColor"/>. Each component is
/// registered once behind its interface and forwarded to <see cref="ISnapshotable"/> so the <see cref="Machine"/>
/// snapshots it; replacing any one of them in a test is a matter of registering a substitute first. The cartridge is
/// built from the configuration's ROM image, so it reloads identically when a machine is forked.
/// </summary>
public static class GameBoyColorComponents {
    /// <summary>Registers the standard component set. The container must already carry a
    /// <see cref="MachineConfiguration"/> whose <see cref="MachineConfiguration.CartridgeRom"/> is non-null.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddGameBoyColorComponents(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        // The decoded header is immutable configuration shared by every component that models a header-dependent boot
        // path (the timer's DIV seed, the PPU's compatibility palettes, the CPU's register handoff).
        services.TryAddScoped(implementationFactory: static provider => CartridgeHeader.Parse(
            rom: (provider.GetRequiredService<MachineConfiguration>().CartridgeRom
                ?? throw new InvalidOperationException(message: "The machine configuration has no cartridge ROM to load."))
        ));

        services.TryAddScoped<SystemMemory>();
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<SystemMemory>());

        services.TryAddScoped<InterruptController>();
        services.AddScoped<IInterruptController>(implementationFactory: static provider => provider.GetRequiredService<InterruptController>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<InterruptController>());

        services.TryAddScoped<TimerComponent>();
        services.AddScoped<ITimer>(implementationFactory: static provider => provider.GetRequiredService<TimerComponent>());
        services.AddScoped<IClockedComponent>(implementationFactory: static provider => provider.GetRequiredService<TimerComponent>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<TimerComponent>());

        services.TryAddScoped<JoypadComponent>();
        services.AddScoped<IJoypad>(implementationFactory: static provider => provider.GetRequiredService<JoypadComponent>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<JoypadComponent>());

        // ORDER IS LOAD-BEARING: KEY1 must register (and therefore snapshot-load) BEFORE the CPU — the CPU's LoadState
        // re-derives the component clock's double-speed flag from KEY1's freshly restored state (the clock stays outside
        // KEY1's dependencies to avoid a DI cycle). SpeedProof check 4 pins this order.
        services.TryAddScoped<Key1Component>();
        services.AddScoped<IKey1>(implementationFactory: static provider => provider.GetRequiredService<Key1Component>());
        services.AddScoped<IClockedComponent>(implementationFactory: static provider => provider.GetRequiredService<Key1Component>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<Key1Component>());

        services.TryAddScoped<SerialComponent>();
        services.AddScoped<ISerial>(implementationFactory: static provider => provider.GetRequiredService<SerialComponent>());
        services.AddScoped<IClockedComponent>(implementationFactory: static provider => provider.GetRequiredService<SerialComponent>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<SerialComponent>());

        services.TryAddScoped<ApuComponent>();
        services.AddScoped<IApu>(implementationFactory: static provider => provider.GetRequiredService<ApuComponent>());
        services.AddScoped<IClockedComponent>(implementationFactory: static provider => provider.GetRequiredService<ApuComponent>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<ApuComponent>());

        // The APU's channel generators run on the fixed dot clock, not the CPU clock: this adapter divides the CPU-tick
        // stream down to one generator tick per whole dot inside the same APU instance. It registers between the APU
        // (whose own tick steps the frame sequencer first) and the audio output stage (which samples the advanced
        // channels). It is stateless (everything lives and snapshots in the APU), so only the clocked facet is
        // registered.
        services.TryAddScoped<ApuGeneratorClock>();
        services.AddScoped<IClockedComponent>(implementationFactory: static provider => provider.GetRequiredService<ApuGeneratorClock>());

        // The host audio output stage registers directly after the APU's two clocked facets so its per-T-cycle tick
        // samples the channels after they have advanced for that cycle. It is a pure consumer: it carries no emulated
        // state (its SaveState writes zero bytes), so it does not perturb snapshot layout or emulated behavior.
        services.TryAddScoped<AudioOutputComponent>();
        services.AddScoped<IAudioSink>(implementationFactory: static provider => provider.GetRequiredService<AudioOutputComponent>());
        services.AddScoped<IClockedComponent>(implementationFactory: static provider => provider.GetRequiredService<AudioOutputComponent>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<AudioOutputComponent>());

        services.TryAddScoped(implementationFactory: static provider => Cartridge.Load(
            rom: (provider.GetRequiredService<MachineConfiguration>().CartridgeRom
                ?? throw new InvalidOperationException(message: "The machine configuration has no cartridge ROM to load.")),
            header: provider.GetRequiredService<CartridgeHeader>()
        ));
        // A cartridge with timed hardware (an MBC3 real-time clock) drives its clock off the emulated timeline; forward
        // its clocked facet, falling back to a no-op for the untimed mappers so one registration serves every cartridge.
        services.AddScoped<IClockedComponent>(implementationFactory: static provider =>
            (provider.GetRequiredService<ICartridge>() as IClockedComponent) ?? NullClockedComponent.Instance);
        services.TryAddScoped(implementationFactory: static provider => new CartridgeSlot(cartridge: provider.GetRequiredService<ICartridge>()));
        services.AddScoped<ICartridgeSlot>(implementationFactory: static provider => provider.GetRequiredService<CartridgeSlot>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<CartridgeSlot>());

        services.TryAddScoped<OamDmaController>();
        services.AddScoped<IOamDma>(implementationFactory: static provider => provider.GetRequiredService<OamDmaController>());
        services.AddScoped<IClockedComponent>(implementationFactory: static provider => provider.GetRequiredService<OamDmaController>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<OamDmaController>());

        services.TryAddScoped<Framebuffer>();
        services.AddScoped<IFramebuffer>(implementationFactory: static provider => provider.GetRequiredService<Framebuffer>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<Framebuffer>());

        // The mode-3 timing knobs default to the shipped values; a sweep harness pre-registers its own instance so this
        // TryAdd is a no-op and the PPU reads the swept parameters (mirrors how the configuration seeds TickResolution).
        services.TryAddScoped(implementationFactory: static provider => PpuTimingParameters.Default);

        services.TryAddScoped<Ppu>();
        services.AddScoped<IPpu>(implementationFactory: static provider => provider.GetRequiredService<Ppu>());
        services.AddScoped<IClockedComponent>(implementationFactory: static provider => provider.GetRequiredService<Ppu>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<Ppu>());

        services.TryAddScoped<HdmaController>();
        services.AddScoped<IHdma>(implementationFactory: static provider => provider.GetRequiredService<HdmaController>());
        services.AddScoped<IClockedComponent>(implementationFactory: static provider => provider.GetRequiredService<HdmaController>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<HdmaController>());

        services.TryAddScoped<SystemBus>();
        services.AddScoped<ISystemBus>(implementationFactory: static provider => provider.GetRequiredService<SystemBus>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<SystemBus>());

        services.TryAddScoped<Sm83>();
        services.AddScoped<ICpu>(implementationFactory: static provider => provider.GetRequiredService<Sm83>());
        services.AddScoped<ISnapshotable>(implementationFactory: static provider => provider.GetRequiredService<Sm83>());

        return services;
    }
}
