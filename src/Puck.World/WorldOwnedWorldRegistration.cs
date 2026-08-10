using Microsoft.Extensions.DependencyInjection;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Composition-root wiring for identity-bearing owned world documents.</summary>
internal static class WorldOwnedWorldRegistration {
    /// <summary>Registers the owned-world directory and its live identity views.</summary>
    public static IServiceCollection AddWorldOwnedWorlds(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);
        services.AddSingleton(implementationFactory: static serviceProvider => {
            var definition = serviceProvider.GetRequiredService<WorldDefinitionSource>().Definition;
            var root = WorldStateRoot.Resolve();
            var directory = Path.Combine(path1: root, path2: "owned-worlds");
            var machineId = ResolveMachineId(root: root);
            var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => directory);
            var worlds = new WorldOwnedWorlds(template: definition, directory: directory, machineId: machineId, neighbours: neighbours);

            Console.Error.WriteLine(value: $"[identity] loaded {worlds.All.Count} owned worlds from {directory}");
            return worlds;
        });
        return services;
    }

    private static Guid ResolveMachineId(string root) {
        Directory.CreateDirectory(path: root);
        var path = Path.Combine(path1: root, path2: "machine.id");

        try {
            if (File.Exists(path: path) && Guid.TryParse(input: File.ReadAllText(path: path).Trim(), result: out var stored) && (stored != Guid.Empty)) {
                return stored;
            }
            var created = Guid.NewGuid();

            File.WriteAllText(path: path, contents: created.ToString(format: "D"));
            return created;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[identity] machine id is session-only ({exception.Message})");
            return Guid.NewGuid();
        }
    }
}
