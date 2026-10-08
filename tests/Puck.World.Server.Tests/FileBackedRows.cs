using Puck.Testing;
using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>
/// Host rows over real document files, for the laws a crossing mints off the ordinary per-step scan. References resolve
/// relative to the authoring document's own file, and a destination adopts a running row by canonical file path
/// (<c>WorldInstanceHost.TryFindRunningInstanceByOrigin</c>, <see cref="WorldInstance.SourcePath"/>), so each row names
/// its file as both its origin and its document origin; the adopted row's in-memory server is what answers a transfer,
/// never a reload of the file.
/// </summary>
internal static class FileBackedRows {
    /// <summary>Writes each document to its path.</summary>
    /// <param name="rows">The paths and the documents to write there.</param>
    public static void Write(params (string Path, WorldDefinition Definition)[] rows) {
        foreach (var (path, definition) in rows) {
            File.WriteAllBytes(
                bytes: WorldDefinitionSerialization.Serialize(definition: definition),
                path: path
            );
        }
    }
    /// <summary>Builds one row over its file: a server with its own state directory, over a loopback link.</summary>
    /// <param name="name">The row's instance name.</param>
    /// <param name="path">The document's file.</param>
    /// <param name="definition">The document the file holds.</param>
    /// <returns>The row, its server, and the state directory the caller disposes.</returns>
    public static (WorldInstance Instance, WorldServer Server, TemporaryDirectory StateDirectory) Build(string name, string path, WorldDefinition definition) {
        var population = new WorldPopulation(definition: definition);
        var machines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );
        var stateDirectory = new TemporaryDirectory(prefix: $"puck-file-row-{name}-");
        var profiles = new WorldOwnedWorlds(
            directory: stateDirectory.RootPath,
            machineId: Guid.NewGuid(),
            template: definition
        );
        var server = new WorldServer(
            definition: definition,
            envelope: new WorldRenderEnvelope(),
            instanceIdentity: name,
            machines: machines,
            narrationSink: new WorldConsoleNarrationSink(),
            population: population,
            profiles: profiles
        );
        var instance = new WorldInstance(
            documentOrigin: new WorldFileOrigin(resolvedPath: path),
            federation: new WorldFederationIdentity(
                Authenticator: new InertAuthenticator(),
                Subject: server.AuthorityIdentity
            ),
            link: new LoopbackTransport(server: server),
            name: name,
            origin: () => path,
            ownedMachines: machines,
            server: server
        );

        return (instance, server, stateDirectory);
    }
}
