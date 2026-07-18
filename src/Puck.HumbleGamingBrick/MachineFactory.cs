using Microsoft.Extensions.DependencyInjection;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// Builds independent <see cref="MachineInstance"/> values. Each call stands up a fresh dependency-injection container
/// seeded with the supplied configuration and component registrations, so every machine is fully isolated — the seam
/// that lets many machines, each with its own configuration, run at once and lets any one be forked into divergent
/// copies.
/// </summary>
public static class MachineFactory {
    /// <summary>Creates a machine for a configuration, applying a composition callback that registers the machine's
    /// components.</summary>
    /// <param name="configuration">The per-machine configuration.</param>
    /// <param name="compose">A callback that registers the machine's components into its container; each component is
    /// typically registered once by its concrete type (which the <see cref="Timing.ComponentClock"/> resolves for the
    /// per-tick fan-out) and forwarded to <see cref="ISnapshotable"/>.</param>
    /// <returns>The assembled, isolated machine instance. The caller owns it and must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="compose"/> is
    /// <see langword="null"/>.</exception>
    public static MachineInstance Create(MachineConfiguration configuration, Action<IServiceCollection> compose) {
        ArgumentNullException.ThrowIfNull(argument: configuration);
        ArgumentNullException.ThrowIfNull(argument: compose);

        var services = new ServiceCollection();

        _ = services.AddHumbleGamingBrick();

        compose(obj: services);

        _ = services.AddSingleton(implementationInstance: configuration);

        var provider = services.BuildServiceProvider(validateScopes: true);
        var scope = provider.CreateScope();
        var machine = scope.ServiceProvider.GetRequiredService<Machine>();

        return new(
            provider: provider,
            scope: scope,
            machine: machine,
            configuration: configuration,
            compose: compose
        );
    }
}
