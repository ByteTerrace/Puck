using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Registers the camera-probes hosts — the boot world's <see cref="WorldProbes"/> and its
/// <see cref="WorldPostPasses"/> write target, the <see cref="WorldProbeHosts"/> that services it and a host for every
/// world shown through a screen as its <see cref="ISnapshotInputCapture"/> contribution to the host loop's per-frame
/// capture order, and their console surface (<see cref="ProbeCommandModule"/>).</summary>
public static class WorldProbesComposition {
    /// <summary>Registers the probes host, its write target, and its console verbs.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddWorldProbes(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        services.AddSingleton<WorldPostPasses>();
        services.AddSingleton(implementationFactory: static sp => {
            var definitionSource = sp.GetRequiredService<WorldDefinitionSource>();

            return new WorldProbes(
                clock: sp.GetRequiredService<IInputClock>(),
                definition: definitionSource.Definition,
                documentDirectory: WorldDocumentPaths.DirectoryOf(documentPath: definitionSource.SourcePath),
                focus: sp.GetRequiredService<IInputFocus>(),
                passes: sp.GetRequiredService<WorldPostPasses>(),
                roster: sp.GetRequiredService<PlayerRoster>(),
                router: sp.GetRequiredService<InputRouter>(),
                screens: sp.GetRequiredService<WorldScreenBinder>(),
                world: WorldDefinitionLoader.BootInstanceName
            );
        });
        services.AddSingleton(implementationFactory: static sp => new WorldProbeHosts(
            boot: sp.GetRequiredService<WorldProbes>(),
            clock: sp.GetRequiredService<IInputClock>(),
            focus: sp.GetRequiredService<IInputFocus>(),
            roster: sp.GetRequiredService<PlayerRoster>(),
            router: sp.GetRequiredService<InputRouter>(),
            screens: sp.GetRequiredService<WorldScreenBinder>()
        ));
        services.AddSingleton<ISnapshotInputCapture>(implementationFactory: static sp => sp.GetRequiredService<WorldProbeHosts>());
        // Deferred: the command registry enumerates every module while the router (which the host needs) is still
        // being built from that same registry, so a module resolving the host eagerly closes a dependency cycle.
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => new ProbeCommandModule(probes: () => sp.GetRequiredService<WorldProbeHosts>()));

        return services;
    }
}
