using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Registers the camera-probes host — <see cref="WorldProbes"/>, its <see cref="WorldPostRenderExtensionPasses"/>
/// write target, its <see cref="ISnapshotInputCapture"/> contribution to the host loop's per-frame capture order,
/// and its console surface (<see cref="ProbeCommandModule"/>).</summary>
public static class WorldProbesComposition {
    /// <summary>Registers the probes host, its write target, and its console verbs.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddWorldProbes(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        services.AddSingleton<WorldPostRenderExtensionPasses>();
        services.AddSingleton(implementationFactory: static sp => new WorldProbes(
            clock: sp.GetRequiredService<IInputClock>(),
            definitionSource: sp.GetRequiredService<WorldDefinitionSource>(),
            focus: sp.GetRequiredService<IInputFocus>(),
            passes: sp.GetRequiredService<WorldPostRenderExtensionPasses>(),
            roster: sp.GetRequiredService<PlayerRoster>(),
            router: sp.GetRequiredService<InputRouter>(),
            screens: sp.GetRequiredService<WorldScreenBinder>()
        ));
        services.AddSingleton<ISnapshotInputCapture>(implementationFactory: static sp => sp.GetRequiredService<WorldProbes>());
        // Deferred: the command registry enumerates every module while the router (which the host needs) is still
        // being built from that same registry, so a module resolving the host eagerly closes a dependency cycle.
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => new ProbeCommandModule(probes: () => sp.GetRequiredService<WorldProbes>()));

        return services;
    }
}
