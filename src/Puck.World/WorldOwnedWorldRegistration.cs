using Microsoft.Extensions.DependencyInjection;
using Puck.World.Machines;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Composition-root wiring for identity-bearing owned world documents.</summary>
internal static class WorldOwnedWorldRegistration {
    /// <summary>Registers the owned-world directory and its live identity views.</summary>
    public static IServiceCollection AddWorldOwnedWorlds(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);
        services.AddSingleton(implementationFactory: static serviceProvider => {
            var definition = serviceProvider.GetRequiredService<WorldDefinitionSource>().Definition;
            var catalog = serviceProvider.GetRequiredService<WorldMachineCatalog>();
            var catalogFingerprint = WorldBootComposition.MachineCatalogFingerprint(machineCatalog: catalog);
            var root = serviceProvider.GetRequiredService<WorldStateRoot>();
            var directory = root.PathOf(name: "owned-worlds");
            var machineId = root.MachineId(
                failure: out var machineIdFailure,
                fileName: "machine.id"
            );

            if (machineIdFailure is not null) {
                Console.Error.WriteLine(value: $"[identity] machine id is session-only ({machineIdFailure})");
            }
            var neighbours = new WorldFileNeighbourResolver(
                baseDirectory: () => directory,
                catalog: catalog,
                catalogFingerprint: catalogFingerprint
            );
            var worlds = new WorldOwnedWorlds(
                directory: directory,
                machineId: machineId,
                neighbours: neighbours,
                template: definition,
                machineCatalog: catalog,
                catalogFingerprint: catalogFingerprint
            );

            Console.Error.WriteLine(value: $"[identity] loaded {worlds.All.Count} owned worlds from {directory}");
            return worlds;
        });
        return services;
    }
}
