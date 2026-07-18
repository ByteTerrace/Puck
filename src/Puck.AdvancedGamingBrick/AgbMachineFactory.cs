using Microsoft.Extensions.DependencyInjection;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// Builds independent <see cref="AgbMachineInstance"/> values. Each call stands up a fresh dependency-injection
/// container seeded with the configuration's BIOS and cartridge ROM, so every machine is fully isolated — the seam
/// that lets many machines run at once and lets any one be forked into a divergent copy. It composes on top of
/// <see cref="AdvancedGamingBrickServiceRegistration.AddAdvancedGamingBrick"/>; an optional composition callback runs
/// <em>before</em> that registration so a caller can pre-register an exotic subsystem (a tracing or debug-log bus
/// decorator, a flat test bus) that the standard <c>TryAdd</c> registrations then defer to.
/// </summary>
public static class AgbMachineFactory {
    /// <summary>Creates a machine for a configuration, optionally applying a composition callback that pre-registers
    /// replacement or decorating subsystems into the container.</summary>
    /// <param name="configuration">The per-machine configuration (BIOS + cartridge ROM).</param>
    /// <param name="compose">An optional callback that registers subsystems into the container before the standard
    /// component set is added; because the standard registrations use <c>TryAdd</c>, anything registered here wins.
    /// Pass <see langword="null"/> for the standard composition. Re-invoked on every <see cref="AgbMachineInstance.Fork"/>,
    /// so a callback that captures host-side state shares that state with the fork.</param>
    /// <returns>The assembled, isolated machine instance. The caller owns it and must dispose it; the machine is
    /// <em>not</em> booted — call <see cref="AdvancedGamingBrickMachine.DirectBoot"/> (or restore a snapshot into
    /// it).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public static AgbMachineInstance Create(AgbMachineConfiguration configuration, Action<IServiceCollection>? compose = null) {
        ArgumentNullException.ThrowIfNull(argument: configuration);

        var services = new ServiceCollection();

        // The composition callback runs first so its registrations win the TryAdd race in AddAdvancedGamingBrick — the
        // exotic-bus seam (a tracing or debug-log decorator wrapping the concrete AgbBus) that the diagnostics rely on.
        compose?.Invoke(obj: services);

        _ = services.AddAdvancedGamingBrick();
        _ = services.AddReplacementBios(image: configuration.Bios);

        // A fresh cartridge per machine, built from the configuration's ROM: the ROM bytes are shared verbatim (never
        // written), but each machine owns its own mutable backup/RTC state so a fork never aliases the source's saves.
        _ = services.AddScoped<AgbCartridge>(implementationFactory: _ => new AgbCartridge(rom: configuration.Rom));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var machine = scope.ServiceProvider.GetRequiredService<AdvancedGamingBrickMachine>();

        return new AgbMachineInstance(
            provider: provider,
            scope: scope,
            machine: machine,
            configuration: configuration,
            compose: compose
        );
    }
}
