using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Bench;

// Constructs a fresh, disposable, in-process WorldServer over a WorldDefinition the same way
// tests/Puck.World.Tests/Fixtures.cs's FreshServer does, without referencing the test project (off limits to this
// verb): a fresh WorldPopulation, an unconfigured WorldRenderEnvelope, a WorldMachineHost with no registered
// engines, and a scratch-directory WorldOwnedWorlds catalog seeded from the same document.
internal sealed class WorldBenchServer : IDisposable {
    private readonly WorldMachineHost m_machines;
    private readonly string m_stateDirectory;

    private WorldBenchServer(WorldServer server, WorldMachineHost machines, string stateDirectory) {
        Server = server;
        m_machines = machines;
        m_stateDirectory = stateDirectory;
    }

    public WorldServer Server { get; }

    public static WorldBenchServer Boot(WorldDefinition definition) {
        var population = new WorldPopulation(definition: definition);
        var machines = new WorldMachineHost(screens: definition.Screens, engines: []);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-bench-world-").FullName;
        var profiles = new WorldOwnedWorlds(template: definition, directory: stateDirectory, machineId: Guid.NewGuid());
        var server = new WorldServer(definition: definition, population: population, profiles: profiles, envelope: new WorldRenderEnvelope(), machines: machines);

        return new WorldBenchServer(server: server, machines: machines, stateDirectory: stateDirectory);
    }

    public void Dispose() {
        m_machines.Dispose();

        try {
            Directory.Delete(path: m_stateDirectory, recursive: true);
        } catch (IOException) {
        }
    }
}
