using Puck.World;
using Puck.World.Machines;
using Puck.World.Server;

namespace Puck.Cli.Determinism;

// Constructs a fresh, disposable, in-process WorldServer over a WorldDefinition the same way
// tests/Shared/World/Fixtures.Server.cs's FreshServer does, without referencing the test project (off limits to this
// verb): a fresh WorldPopulation, an unconfigured WorldRenderEnvelope, a WorldMachineHost with no registered
// engines, and a scratch-directory WorldOwnedWorlds catalog seeded from the same document.
public sealed class WorldBenchServer : IDisposable {
    private readonly WorldMachineHost m_machines;
    private readonly RunDirectory m_stateDirectory;

    private WorldBenchServer(WorldServer server, WorldMachineHost machines, RunDirectory stateDirectory) {
        Server = server;
        m_machines = machines;
        m_stateDirectory = stateDirectory;
    }

    public WorldServer Server { get; }

    public static WorldBenchServer Boot(WorldDefinition definition, WorldMachineCatalog? catalog = null) {
        var population = new WorldPopulation(definition: definition);
        var machines = ((catalog is not null)
            ? new WorldMachineHost(
                screens: definition.Screens,
                catalog: catalog
            )
            : new WorldMachineHost(
                screens: definition.Screens,
                engines: []
            )
        );
        // A benchmark has no verdict to keep evidence for, so its state directory is deleted however the run ends.
        var stateDirectory = RunDirectory.Create(
            keepOnFailure: false,
            prefix: "puck-bench-world-"
        );
        var profiles = new WorldOwnedWorlds(
            template: definition,
            directory: stateDirectory.Path,
            machineId: Guid.NewGuid()
        );
        var server = new WorldServer(
            definition: definition,
            population: population,
            profiles: profiles,
            envelope: new WorldRenderEnvelope(),
            machines: machines
        );

        return new WorldBenchServer(
            machines: machines,
            server: server,
            stateDirectory: stateDirectory
        );
    }
    public void Dispose() {
        m_machines.Dispose();
        m_stateDirectory.Dispose();
    }
}
